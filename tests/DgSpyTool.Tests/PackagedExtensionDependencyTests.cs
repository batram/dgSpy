using Xunit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// Every dgSpy-owned assembly the packaged extension references must actually be in the package.
///
/// The defect this prevents: the extension gained a reference to HookLab.Injector, and
/// <c>DgSpyTool</c>'s ExtensionFiles allowlist was not updated. The package built, verified, and
/// installed; dnSpy loaded the extension; MEF composed it; every tool call that did not touch HookLab
/// worked. The first <c>initialize_hooklab</c> threw
/// <c>FileNotFoundException: Could not load file or assembly 'HookLab.Injector'</c>, and only a live
/// smoke found it - after the full unit suite and the composition test had gone green.
///
/// The allowlist is deliberately an explicit list rather than a glob, because a glob would let a
/// build-output change quietly add files to the packaged host. This test is the other half of that
/// bargain: naming files by hand is safe only if something checks the names are sufficient.
///
/// Scope is narrow on purpose. It asserts nothing about framework or dnSpy host assemblies, whose
/// resolution has its own rules and its own failure modes; it asserts that an assembly we build and
/// the extension references is present somewhere the CLR will find it.
/// </summary>
public sealed class PackagedExtensionDependencyTests {
	/// <summary>Assembly-name prefixes for code this repository builds and ships itself.</summary>
	static readonly string[] OwnedPrefixes={ "HookLab.","dgSpy." };

	[Fact]
	public void Every_owned_assembly_the_extension_references_is_packaged_beside_it() {
		var layout=Layout();
		var extensionDirectory=Path.Combine(layout,"bin","Extensions","dgSpy");
		var extension=Path.Combine(extensionDirectory,"dgSpy.Extension.x.dll");
		Assert.True(File.Exists(extension),"The layout has no packaged extension: "+extension);

		// The CLR probes the extension's own directory and the host bin directory; both are real
		// resolution locations for a dnSpy extension, so either satisfies the reference.
		var searchDirectories=new[]{ extensionDirectory,Path.Combine(layout,"bin") };
		var missing=new List<string>();
		foreach(var reference in OwnedReferences(extension)) {
			var found=searchDirectories.Any(directory=>File.Exists(Path.Combine(directory,reference+".dll")));
			if(!found) missing.Add(reference);
		}
		Assert.True(missing.Count==0,
			"The packaged extension references assemblies this repository builds that are absent from the package: "+
			String.Join(", ",missing)+Environment.NewLine+
			"Add them to ExtensionFiles in Build/DgSpyTool/Program.cs. A package missing one of these loads, composes, "+
			"and fails only when the code path that needs it first runs.");
	}

	/// <summary>The list is expected to be non-trivial; an empty one would mean the reader failed and
	/// the test above proved nothing.</summary>
	[Fact]
	public void The_reference_reader_finds_the_extensions_owned_dependencies() {
		var extension=Path.Combine(Layout(),"bin","Extensions","dgSpy","dgSpy.Extension.x.dll");
		Assert.True(File.Exists(extension),"The layout has no packaged extension: "+extension);
		var references=OwnedReferences(extension).ToArray();
		Assert.Contains("HookLab.Contracts",references);
		Assert.Contains("dgSpy.Protocol",references);
	}

	static IEnumerable<string> OwnedReferences(string assemblyPath) {
		// Metadata only. Loading a packaged extension into the test process would drag in dnSpy's
		// contracts and WPF, and would prove something about this process rather than about the package.
		using var stream=File.OpenRead(assemblyPath);
		using var reader=new PEReader(stream);
		var metadata=reader.GetMetadataReader();
		foreach(var handle in metadata.AssemblyReferences) {
			var name=metadata.GetString(metadata.GetAssemblyReference(handle).Name);
			if(OwnedPrefixes.Any(prefix=>name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))) yield return name;
		}
	}

	/// <summary>The composed layout the gate has just built, or the newest one on disk.</summary>
	static string Layout() {
		var configured=Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		if(!String.IsNullOrWhiteSpace(configured)&&Directory.Exists(configured)) return Path.GetFullPath(configured);
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var layouts=Path.Combine(repo,"artifacts","layouts");
		var newest=Directory.Exists(layouts)
			?new DirectoryInfo(layouts).GetDirectories().OrderByDescending(directory=>directory.LastWriteTimeUtc).FirstOrDefault()
			:null;
		Assert.True(newest is not null,"No composed layout was found. Build one first: dotnet run --project Build\\DgSpyTool -- pipeline");
		return newest!.FullName;
	}
}
