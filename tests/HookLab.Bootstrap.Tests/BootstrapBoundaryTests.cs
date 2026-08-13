using System;
using System.IO;
using System.Linq;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	/// <summary>Boundary assertions read the project files, not just the built assemblies.
	/// GetReferencedAssemblies cannot see a forbidden reference that nothing happens to use yet; the csproj
	/// can.</summary>
	public class BootstrapBoundaryTests {
		static string RepoRoot {
			get {
				var directory = new DirectoryInfo(AppContext.BaseDirectory);
				while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
				return directory?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
			}
		}

		static string BootstrapProject => WithoutComments(File.ReadAllText(Path.Combine(RepoRoot, "HookLab", "HookLab.Bootstrap", "HookLab.Bootstrap.csproj")));
		static string TestProject => WithoutComments(File.ReadAllText(Path.Combine(RepoRoot, "tests", "HookLab.Bootstrap.Tests", "HookLab.Bootstrap.Tests.csproj")));

		/// <summary>These assertions are about what the project declares, not what its comments explain -
		/// and the comments name the very assembly the boundary forbids.</summary>
		static string WithoutComments(string project) {
			while (true) {
				var start = project.IndexOf("<!--", StringComparison.Ordinal);
				if (start < 0) return project;
				var end = project.IndexOf("-->", start, StringComparison.Ordinal);
				if (end < 0) return project.Substring(0, start);
				project = project.Substring(0, start) + project.Substring(end + 3);
			}
		}

		[Fact]
		public void The_bootstrap_takes_no_package_dependency() {
			Assert.DoesNotContain("<PackageReference", BootstrapProject, StringComparison.Ordinal);
		}

		[Fact]
		public void The_bootstrap_never_references_the_host_transport() {
			// HookLab.Host.Transport Compile-includes the probe's ProbeWireProtocol.cs and
			// ProbeAuthentication.cs under their original namespace, so an assembly referencing both gets
			// CS0433. The bootstrap references only what it embeds, which keeps that hazard unreachable
			// rather than worked around with extern alias.
			Assert.DoesNotContain("HookLab.Host.Transport", BootstrapProject, StringComparison.Ordinal);
			Assert.DoesNotContain("HookLab.Host.Transport", TestProject, StringComparison.Ordinal);
			Assert.DoesNotContain("extern alias", File.ReadAllText(Path.Combine(RepoRoot, "HookLab", "HookLab.Bootstrap", "ProbeStartup.cs")), StringComparison.Ordinal);
			Assert.DoesNotContain("HookLab.Host.Transport",
				string.Join(";", typeof(HookLabBootstrap).Assembly.GetReferencedAssemblies().Select(a => a.Name)), StringComparison.Ordinal);
		}

		[Fact]
		public void Every_payload_reference_is_embedded_rather_than_deployed() {
			foreach (var line in BootstrapProject.Split('\n').Where(l => l.Contains("<ProjectReference")))
				Assert.Contains("Private=\"false\"", line, StringComparison.Ordinal);
		}

		[Fact]
		public void No_payload_assembly_sits_beside_the_test_host() {
			// If any of these were on disk the CLR would bind them before AssemblyResolve is raised, and
			// every "resolved from embedded bytes" assertion in this suite would pass without the resolver
			// having done anything.
			foreach (var name in new[] { "HookLab.Contracts.dll", "HookLab.Probe.CorDebug.dll", "0Harmony.dll" })
				Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name + " is on disk beside the test host.");
		}

		[Fact]
		public void The_bootstrap_output_directory_holds_the_bootstrap_alone() {
			var output = Path.Combine(RepoRoot, "HookLab", "HookLab.Bootstrap", "bin", "Release", "net48");
			if (!Directory.Exists(output)) return;
			var assemblies = Directory.GetFiles(output, "*.dll").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
			Assert.Equal(new[] { "HookLab.Bootstrap.dll" }, assemblies);
		}

		[Fact]
		public void The_test_project_references_only_the_bootstrap() {
			var references = TestProject.Split('\n').Where(l => l.Contains("<ProjectReference")).ToArray();
			Assert.Single(references);
			Assert.Contains("HookLab.Bootstrap.csproj", references[0], StringComparison.Ordinal);
		}
	}
}
