using System.Reflection;
using System;
using System.Linq;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The gateway used to carry its own hardcoded per-tool deadline table, which is how a
/// successful 10 s attach once got abandoned by an 8 s caller deadline. The extension now advertises its
/// bounds and the gateway derives deadlines from them; these tests are what keeps the two from drifting
/// apart again.</summary>
public sealed class ToolCatalogTests {
	static string Name(object tool) => (string)tool.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)!.GetValue(tool)!;
	static string Description(object tool) => (string)tool.GetType().GetProperty("description", BindingFlags.Public | BindingFlags.Instance)!.GetValue(tool)!;
	public static TheoryData<string> ToolNames {
		get { var data=new TheoryData<string>(); foreach (var tool in ToolCatalog.All) data.Add(Name(tool)); return data; }
	}

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_advertised_tool_is_an_operation_the_extension_implements(string tool) =>
		Assert.True(CapabilityCatalog.IsKnownOperation(tool), $"tools/list advertises '{tool}', which is not in CapabilityCatalog.Operations.");

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_gateway_deadline_outlasts_the_extension_bound(string tool) {
		var bound=CapabilityCatalog.BoundMs(tool);

		Assert.True(ToolCatalog.DeadlineSeconds(tool)*1000 > bound, $"'{tool}' deadline {ToolCatalog.DeadlineSeconds(tool)}s does not outlast its {bound}ms bound.");
	}

	[Fact]
	public void An_unknown_tool_still_gets_a_usable_deadline() => Assert.Equal(8, ToolCatalog.DeadlineSeconds("not_a_tool"));

	[Fact]
	public void The_slowest_operation_keeps_its_full_connection_timeout() =>
		Assert.True(ToolCatalog.DeadlineSeconds("attach_endpoint")*1000 > CapabilityCatalog.Limits.MaxConnectionTimeoutMs,
			"attach_endpoint must outlast the largest connection timeout a caller can ask for.");

	[Fact]
	public void Phase7_side_effecting_tools_are_visibly_labeled() {
		foreach (var name in new[] { "invoke_method","create_object","write_memory","set_instruction_pointer" }) {
			var tool=ToolCatalog.All.Single(t=>Name(t)==name);
			Assert.Contains("SIDE EFFECTING",Description(tool));
			Assert.Contains("audit",Description(tool),StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public void Phase8_surface_is_complete_and_host_export_is_visibly_side_effecting() {
		var names=ToolCatalog.All.Select(Name).ToHashSet(StringComparer.Ordinal);
		foreach(var name in new[]{"create_object_id","list_object_ids","evaluate_object_id","release_object_id","get_autos","get_output","wait_for_output","set_module_breakpoint","list_module_breakpoints","update_module_breakpoint","remove_module_breakpoint","export_breakpoints","import_breakpoints","list_exception_categories","list_exception_policies","set_exception_policy","remove_exception_policy","restore_exception_defaults","get_value_export","write_value_export","analyze_symbol"}) Assert.Contains(name,names);
		Assert.Contains("SIDE EFFECTING",Description(ToolCatalog.All.Single(t=>Name(t)=="write_value_export")));
	}
}
