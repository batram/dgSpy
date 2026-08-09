using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using dgSpy.ExtensionContracts;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Contracts.Tests;

public sealed class ContractAssemblyBoundaryTests {
	// The provider ABI is a host plug-in contract and the HookLab assembly carries wire DTOs that reach a
	// target probe. Neither may drag WPF, dnSpy, or the Gateway along, and the HookLab side must not
	// reference the provider ABI at all: a probe that could see it would blur the boundary the plans draw.
	static readonly string[] ForbiddenReferencePrefixes = { "PresentationFramework", "PresentationCore", "WindowsBase", "dnSpy", "dgSpy.Gateway" };

	[Fact]
	public void AtomicStatusEnumsMatchFrozenPlanExactly() {
		Assert.Equal(new[] { "trigger_not_reached", "reached_not_evaluable", "nearby_slot_not_found", "action_failed", "verification_failed", "completed" }, Enum.GetNames(typeof(ActionOutcome)));
		Assert.Equal(new[] { "none", "timeout", "cancelled", "client_disconnected", "target_exited", "appdomain_unloaded", "ui_shutdown", "dispatcher_degraded", "external_debugger_action" }, Enum.GetNames(typeof(InterruptionReason)));
		Assert.Equal(new[] { "not_required", "completed", "failed", "ambiguous" }, Enum.GetNames(typeof(CleanupOutcome)));
	}

	[Fact]
	public void ContractAssembliesHaveDisjointTypeNamesAndSafeReferences() {
		var providerAssembly = typeof(IDgSpyExtensionProvider).Assembly;
		var hookAssembly = typeof(HookDocument).Assembly;
		var duplicateNames = providerAssembly.GetExportedTypes().Select(type => type.Name)
			.Intersect(hookAssembly.GetExportedTypes().Select(type => type.Name), StringComparer.Ordinal).ToArray();
		Assert.Empty(duplicateNames);

		foreach (var assembly in new[] { providerAssembly, hookAssembly }) {
			Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
				ForbiddenReferencePrefixes.Any(prefix => reference.Name!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
		}
		Assert.DoesNotContain(providerAssembly.GetExportedTypes(), type => type.Namespace?.StartsWith("HookLab", StringComparison.Ordinal) == true);
		Assert.DoesNotContain(hookAssembly.GetReferencedAssemblies(), reference => reference.Name == providerAssembly.GetName().Name);
	}

	// GetReferencedAssemblies only reports what survived compilation, so an unused ProjectReference to
	// dnSpy or WPF is elided from metadata and the runtime check above passes while the project file
	// says otherwise. Declared intent is what a future edit changes first, so assert on that too.
	[Theory]
	[InlineData("dgSpy.ExtensionContracts", "dgSpy.ExtensionContracts.csproj")]
	[InlineData("HookLab\\HookLab.Contracts", "HookLab.Contracts.csproj")]
	public void ContractProjectsDeclareNoForbiddenReferences(string projectDirectory, string projectFile) {
		var path = Path.Combine(RepositoryRoot(), projectDirectory, projectFile);
		Assert.True(File.Exists(path), $"Contract project not found at '{path}'.");
		var declared = XDocument.Load(path).Descendants()
			.Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference" or "Reference")
			.Select(element => (string?)element.Attribute("Include") ?? string.Empty)
			.ToArray();

		var offenders = declared.Where(include => ForbiddenReferencePrefixes.Any(prefix =>
			Path.GetFileName(include).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
			include.Split('\\', '/').Any(segment => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))).ToArray();
		Assert.Empty(offenders);

		Assert.Equal("netstandard2.0", XDocument.Load(path).Descendants()
			.First(element => element.Name.LocalName == "TargetFramework").Value);
	}

	static string RepositoryRoot() {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dnSpy.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new InvalidOperationException("Repository root not found above the test output directory.");
	}
}
