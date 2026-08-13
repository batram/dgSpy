/*
    No unit test can catch a MEF composition failure by exercising behaviour: the
    part simply does not exist, so there is nothing to call and nothing to assert
    against. The only place the damage is visible is the composition itself.

    These tests read it directly.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.Composition;
using Xunit;

namespace dgSpy.Composition.Tests;

public class CompositionTests {
	// Deliberately a failure rather than a skip. These tests exist to catch damage
	// that is invisible everywhere else, so quietly passing when the host was never
	// built would defeat the point. The gate builds before it runs them.
	static void RequirePublishedHost() =>
		Assert.True(PublishedHost.LocateBinDirectory() is not null,
			"verified host not found. Run DgSpyTool pipeline or point DGSPY_PUBLISH_BIN at its bin directory.");

	/// <summary>
	/// The check dnSpy itself performs only under Debug.Assert, and therefore not
	/// in the Release build that ships. Any part that cannot be satisfied is
	/// reported here instead of vanishing.
	/// </summary>
	[Fact]
	public void Composition_has_no_errors() {
		RequirePublishedHost();
		var host = PublishedHost.Instance;

		if (host.Configuration.CompositionErrors.IsEmpty)
			return;

		var sb = new StringBuilder();
		sb.AppendLine("MEF composition produced errors. Each unsatisfied part silently");
		sb.AppendLine("disappears at runtime, taking everything that imported it:");
		sb.AppendLine();
		foreach (var level in host.Configuration.CompositionErrors) {
			foreach (var error in level) {
				sb.Append("  - ").AppendLine(error.Message);
				foreach (var part in error.Parts)
					sb.Append("      part: ").AppendLine(part.Definition.Type.FullName);
			}
		}
		Assert.Fail(sb.ToString());
	}

	/// <summary>
	/// The regression that motivated all of this. IDbgEnvironmentEditorService is
	/// declared in dnSpy.Contracts.Debugger and consumed by three CorDebug types.
	/// When its only exporter was deleted, StartDebuggingOptionsPageProviderImpl
	/// could not compose, so the Debug Program dialog offered only the two Unity
	/// engines and could not start a CorDebug session at all.
	/// </summary>
	[Fact]
	public void Environment_editor_service_is_exported() {
		RequirePublishedHost();
		var exporters = PartsExporting(PublishedHost.Instance.Catalog,
			"dnSpy.Contracts.Debugger.Dialogs.IDbgEnvironmentEditorService");

		Assert.True(exporters.Count > 0,
			"nothing exports IDbgEnvironmentEditorService. Its consumers in " +
			"dnSpy.Debugger.DotNet.CorDebug cannot compose without it, which removes " +
			"the .NET and .NET Framework pages from the Debug Program dialog.");
	}

	/// <summary>
	/// Every debug engine the GUI can offer arrives as a StartDebuggingOptionsPageProvider.
	/// CorDebug contributes .NET Framework and .NET; Mono contributes Unity and
	/// Unity (Connect). Losing the CorDebug provider is invisible until someone
	/// opens the dropdown, so assert both providers are present.
	/// </summary>
	[Fact]
	public void Both_start_debugging_option_page_providers_compose() {
		RequirePublishedHost();
		var providers = PartsExporting(PublishedHost.Instance.Catalog,
			"dnSpy.Contracts.Debugger.StartDebugging.Dialog.StartDebuggingOptionsPageProvider");

		var names = providers.Select(a => a.Definition.Type.FullName ?? "").ToList();

		Assert.True(names.Any(n => n.Contains("CorDebug", StringComparison.Ordinal)),
			"the CorDebug StartDebuggingOptionsPageProvider did not compose, so the " +
			"Debug Program dialog cannot offer .NET Framework or .NET. Exported providers: " +
			string.Join(", ", names));

		Assert.True(names.Any(n => n.Contains("Mono", StringComparison.Ordinal)),
			"the Mono StartDebuggingOptionsPageProvider did not compose, so the " +
			"Debug Program dialog cannot offer Unity. Exported providers: " +
			string.Join(", ", names));
	}

	/// <summary>
	/// dgSpy consumes only the contracts assembly. CorDebug owns the implementation export, so the
	/// extension never needs a runtime reference to the scanner-loaded implementation assembly.
	/// </summary>
	[Fact]
	public void Owned_breakpoint_service_contract_is_exported() {
		RequirePublishedHost();
		var exporters = PartsExporting(PublishedHost.Instance.Catalog,
			"dnSpy.Contracts.Debugger.DotNet.CorDebug.IDgSpyOwnedBreakpointService");
		Assert.Single(exporters);
		Assert.Contains("CorDebug", exporters[0].Definition.Type.FullName ?? "", StringComparison.Ordinal);
	}

	[Fact]
	public void HookLab_gui_command_window_and_loader_compose() {
		RequirePublishedHost();
		var names = PublishedHost.Instance.Catalog.Parts.Select(part => part.Type.FullName ?? "").ToHashSet(StringComparer.Ordinal);
		Assert.Contains("dgSpy.Extension.ToolWindows.AddHookFromCodeCommand", names);
		Assert.Contains("dgSpy.Extension.ToolWindows.CreateCustomHookFromCodeCommand", names);
		Assert.Contains("dgSpy.Extension.ToolWindows.HookLabToolWindowProvider", names);
		Assert.Contains("dgSpy.Extension.ToolWindows.HookLabToolWindowLoader", names);
		Assert.Contains("dgSpy.Extension.ToolWindows.HookLabGlyphMarker", names);
		Assert.Contains("dgSpy.Extension.ToolWindows.ShowHookLabGlyphCommand", names);
	}

	[Fact]
	public void Debugger_composes_with_zero_action_guards() {
		RequirePublishedHost();
		var configuration=PublishedHost.Instance.ComposeWithoutAssembly("dgSpy.Extension.x");
		Assert.True(configuration.CompositionErrors.IsEmpty,
			"Removing dgSpy.Extension.x must leave the optional ImportMany<DbgActionGuard> imports satisfied so stock dnSpy behavior is unchanged.");
	}

	/// <summary>
	/// The cold path, through the real composition rather than a direct construction. dnSpy imports its
	/// guards as <c>Lazy&lt;DbgActionGuard&gt;</c> and first realizes them inside
	/// <c>CaptureActionAuthorization()</c>, so on a freshly started host nothing exists when the first
	/// atomic action acquires its lease - the action's own mutation is what brings the guard into being.
	/// A guard that derived ownership from <c>LeaseChanged</c> notifications therefore started blind and
	/// refused its own owner's mutation, and every unit test missed it by constructing the guard first.
	/// This realizes the export only after the lease exists, which is the order production actually uses.
	/// </summary>
	[Fact]
	public void The_lazily_realized_action_guard_answers_for_a_lease_acquired_before_it_existed() {
		RequirePublishedHost();
		var host = PublishedHost.Instance;
		var handle = host.Configuration.CreateExportProviderFactory().CreateExportProvider()
			.GetExports(new ImportDefinition("dnSpy.Contracts.Debugger.DbgActionGuard", ImportCardinality.ZeroOrMore,
				new Dictionary<string, object?>(), Array.Empty<IImportSatisfiabilityConstraint>()))
			.Single();

		// The coordinator lives in the host load context, so this suite cannot reference it statically.
		var extension = host.Assemblies.First(a => a.GetName().Name == "dgSpy.Extension.x");
		var coordinatorType = extension.GetType("dgSpy.Extension.Debugger.ActionLeaseCoordinator", throwOnError: true)!;
		var shared = coordinatorType.GetProperty("Shared")!.GetValue(null)!;
		var lease = coordinatorType.GetMethod("Acquire")!.Invoke(shared, new object?[] {
			987654, "run_to", "composition-cold-lease", DateTime.UtcNow.AddSeconds(30),
			"get_atomic_action_status", "cancel_atomic_action", CancellationToken.None })!;
		var executeMutation = lease.GetType().GetMethods().First(m => m.Name == "ExecuteMutation" && !m.IsGenericMethod);
		try {
			// Realized only now, by the owner's own mutation, exactly as DbgManagerImpl realizes it.
			var guard = handle.Value!;
			Assert.Equal("dgSpy.Extension.Debugger.ActionLeaseGuard", guard.GetType().FullName);
			var capture = guard.GetType().GetMethod("CaptureAuthorization")!;
			var tryGetBlock = guard.GetType().GetMethods().First(m => m.Name == "TryGetBlock" && m.GetParameters().Length == 4);

			object? captured = null;
			executeMutation.Invoke(lease, new object?[] { (Action)(() => captured = capture.Invoke(guard, null)) });
			Assert.NotNull(captured);

			// A null process is the process-less (breakpoint) mutation, the one path this suite can reach
			// without a live DbgProcess. The owner's capture passes it; an external caller's does not.
			var owner = new object?[] { null, "continue", captured, null };
			Assert.False((bool)tryGetBlock.Invoke(guard, owner)!,
				"the lazily realized guard refused its own owner's captured mutation, which is the cold-start defect");
			var external = new object?[] { null, "continue", null, null };
			Assert.True((bool)tryGetBlock.Invoke(guard, external)!,
				"the lazily realized guard admitted an external mutation while an atomic action owned the process");
		}
		finally {
			lease.GetType().GetMethod("Dispose")!.Invoke(lease, null);
		}
	}

	/// <summary>
	/// A content type has to be registered before EditValueProviderService.Create
	/// will accept it. When the environment editor was restored without its two
	/// ContentTypeDefinition exports, opening the editor threw
	/// ArgumentOutOfRangeException -- the code compiled and the part composed, and
	/// it still failed the moment a user clicked the button.
	/// </summary>
	[Theory]
	[InlineData("EnvironmentVariableKey")]
	[InlineData("EnvironmentVariableValue")]
	[InlineData("StaticFieldsWindow")]
	public void Content_type_is_registered(string name) {
		RequirePublishedHost();
		var registered = PublishedHost.Instance.Catalog.Parts
			.SelectMany(p => p.ExportDefinitions)
			.Select(e => e.Value)
			.Where(e => e.ContractName.Contains("ContentTypeDefinition", StringComparison.Ordinal))
			.SelectMany(e => e.Metadata.Values)
			.OfType<string>()
			.ToHashSet(StringComparer.Ordinal);

		Assert.True(registered.Contains(name),
			$"no ContentTypeDefinition named '{name}' is exported. Anything calling " +
			"EditValueProviderService.Create with it will throw at the moment of use.");
	}

	/// <summary>
	/// Guards the loader itself. If assembly loading silently degraded, every other
	/// test here would pass against an almost-empty catalog and prove nothing.
	/// </summary>
	[Fact]
	public void Catalog_is_populated() {
		RequirePublishedHost();
		var host = PublishedHost.Instance;
		Assert.True(host.Assemblies.Count >= 10,
			$"only {host.Assemblies.Count} assemblies loaded from {host.BinDirectory}");
		Assert.True(host.Catalog.Parts.Count >= 500,
			$"only {host.Catalog.Parts.Count} MEF parts discovered; the loader is probably broken, " +
			"which would make the other assertions in this file meaningless");
	}

	/// <summary>
	/// Matches on the contract's simple name as well as its full name, so that
	/// moving a type to another namespace upstream shows up as a compile-time or
	/// review problem rather than as this file quietly asserting against a
	/// contract nothing exports and "finding" a regression that is not there.
	/// </summary>
	static List<ComposedPart> PartsExporting(ComposableCatalog catalog, string contractTypeFullName) {
		var simpleName = contractTypeFullName[(contractTypeFullName.LastIndexOf('.') + 1)..];
		return CompositionConfiguration.Create(catalog).Parts
			.Where(p => p.Definition.ExportDefinitions.Any(e =>
				e.Value.ContractName == contractTypeFullName ||
				e.Value.ContractName.EndsWith("." + simpleName, StringComparison.Ordinal)))
			.ToList();
	}
}
