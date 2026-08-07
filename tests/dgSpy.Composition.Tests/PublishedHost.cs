/*
    Loads the published dnSpy host and reproduces the MEF composition it performs
    at startup, so tests can inspect the result.

    dnSpy composes with VS-MEF and checks the outcome like this:

        Debug.Assert(config.ThrowOnErrors() == config);

    Under Debug.Assert, which the Release build we ship compiles away. So in a
    shipping host a part whose imports cannot be satisfied is silently dropped,
    along with everything that imported it, and there is no error anywhere. That
    is not a hypothetical: it is how the Debug Program dialog lost its .NET and
    .NET Framework pages and became unable to start a CorDebug session at all.

    These tests do in a test run what the Release build declines to do.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.Composition;

namespace dgSpy.Composition.Tests;

/// <summary>
/// The published host directory, its assemblies, and the composition they produce.
/// Built once per test run; composition is not cheap.
/// </summary>
sealed class PublishedHost {
	const string ExtensionSearchPattern = "*.x.dll";

	public string BinDirectory { get; }
	public IReadOnlyList<Assembly> Assemblies { get; }
	public CompositionConfiguration Configuration { get; }
	public ComposableCatalog Catalog { get; }

	static readonly Lazy<PublishedHost> instance = new(() => new PublishedHost(), isThreadSafe: true);
	public static PublishedHost Instance => instance.Value;

	/// <summary>
	/// Null when the host has not been published, so tests can report that clearly
	/// instead of failing with an unrelated file-not-found.
	/// </summary>
	public static string? LocateBinDirectory() {
		var fromEnv = Environment.GetEnvironmentVariable("DGSPY_PUBLISH_BIN");
		if (!string.IsNullOrEmpty(fromEnv))
			return Directory.Exists(fromEnv) ? fromEnv : null;

		var dir = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && dir is not null; i++) {
			var candidate = Path.Combine(dir,
				"dnSpy", "dnSpy", "bin", "Release", "net10.0-windows", "win-x64", "publish", "bin");
			if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "dnSpy.Contracts.DnSpy.dll")))
				return candidate;
			dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
		}
		return null;
	}

	PublishedHost() {
		BinDirectory = LocateBinDirectory()
			?? throw new InvalidOperationException(
				"published host not found. Build it first:  .\\build.ps1 net-x64 -NoMsbuild  then  .\\build-dgspy.ps1");

		// The published host is self-contained, so every dependency sits next to
		// the assemblies under test. Resolve from there rather than from the test
		// host, or half the graph fails to load and the composition looks broken
		// for reasons that have nothing to do with the code under test.
		var context = new HostLoadContext(BinDirectory);
		Assemblies = LoadHostAssemblies(context);

		// VS-MEF resolves type references through this loader while validating the
		// graph. The default one calls Assembly.Load, which searches the test host's
		// own context and cannot see anything loaded above, so it has to be pointed
		// at the same context or every part fails to resolve its parameters.
		var resolver = new Resolver(new HostAssemblyLoader(context));
		var discovery = new AttributedPartDiscoveryV1(resolver);
		var parts = discovery.CreatePartsAsync(Assemblies).GetAwaiter().GetResult();

		Catalog = ComposableCatalog.Create(resolver).AddParts(parts);
		Configuration = CompositionConfiguration.Create(Catalog);
	}

	/// <summary>
	/// Mirrors App.GetAssemblies: the core assemblies plus every *.x.dll extension,
	/// in bin and under bin\Extensions (one level deep).
	/// </summary>
	List<Assembly> LoadHostAssemblies(HostLoadContext context) {
		var names = new List<string> {
			"dnSpy.dll",
			"dnSpy.Contracts.DnSpy.dll",
			"dnSpy.Roslyn.dll",
			"Microsoft.VisualStudio.Text.Logic.dll",
			"Microsoft.VisualStudio.Text.UI.dll",
			"Microsoft.VisualStudio.Text.UI.Wpf.dll",
			"dnSpy.Roslyn.EditorFeatures.dll",
			"dnSpy.Roslyn.CSharp.EditorFeatures.dll",
			"dnSpy.Roslyn.VisualBasic.EditorFeatures.dll",
		};

		var paths = new List<string>();
		foreach (var n in names) {
			var p = Path.Combine(BinDirectory, n);
			if (File.Exists(p))
				paths.Add(p);
		}

		paths.AddRange(Directory.GetFiles(BinDirectory, ExtensionSearchPattern));
		var extDir = Path.Combine(BinDirectory, "Extensions");
		if (Directory.Exists(extDir)) {
			paths.AddRange(Directory.GetFiles(extDir, ExtensionSearchPattern));
			foreach (var d in Directory.GetDirectories(extDir))
				paths.AddRange(Directory.GetFiles(d, ExtensionSearchPattern));
		}

		var loaded = new List<Assembly>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in paths.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) {
			if (!seen.Add(Path.GetFileName(p)))
				continue;
			try {
				loaded.Add(context.LoadFromAssemblyPath(p));
			}
			catch (BadImageFormatException) {
				// native or mixed-mode file in the publish dir; not a MEF assembly
			}
		}
		return loaded;
	}

	sealed class HostAssemblyLoader : IAssemblyLoader {
		readonly HostLoadContext context;

		public HostAssemblyLoader(HostLoadContext context) => this.context = context;

		public Assembly LoadAssembly(AssemblyName assemblyName) =>
			context.LoadFromAssemblyName(assemblyName);

		public Assembly LoadAssembly(string assemblyFullName, string? codeBasePath) {
			if (!string.IsNullOrEmpty(codeBasePath) && File.Exists(codeBasePath))
				return context.LoadFromAssemblyPath(codeBasePath);
			return context.LoadFromAssemblyName(new AssemblyName(assemblyFullName));
		}
	}

	sealed class HostLoadContext : AssemblyLoadContext {
		readonly string binDir;

		public HostLoadContext(string binDir) : base(nameof(HostLoadContext), isCollectible: false) =>
			this.binDir = binDir;

		protected override Assembly? Load(AssemblyName assemblyName) {
			if (assemblyName.Name is null)
				return null;

			// MEF matches attributes by Type identity. The published host is
			// self-contained, so loading its copy of System.ComponentModel.Composition
			// here would give every [Export] a different ExportAttribute type than the
			// one the discovery engine looks for, and nothing at all would be
			// discovered. Anything the default context can already supply must
			// therefore be shared with it rather than loaded again.
			try {
				var shared = Default.LoadFromAssemblyName(assemblyName);
				if (shared is not null)
					return shared;
			}
			catch (FileNotFoundException) {
				// not available to the test host; load it from the publish below
			}
			catch (FileLoadException) {
			}

			var path = Path.Combine(binDir, assemblyName.Name + ".dll");
			return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
		}
	}
}
