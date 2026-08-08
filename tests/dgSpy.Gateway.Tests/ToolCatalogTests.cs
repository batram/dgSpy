using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The gateway used to carry its own hardcoded per-tool deadline table, which is how a
/// successful 10 s attach once got abandoned by an 8 s caller deadline. The extension now advertises its
/// bounds and the gateway derives deadlines from them; these tests are what keeps the two from drifting
/// apart again.</summary>
public sealed class ToolCatalogTests {
	static readonly HashSet<string> GatewayOperations=new(StringComparer.Ordinal) { "get_started","doctor","get_workflow_help","get_local_deployment","launch_local_host","rollback_local_deployment","uninstall_local_deployment","create_remote_host_package","get_remote_host_readiness","revoke_remote_host","step_and_inspect","trace_calls","run_to_method","run_to_location","list_hosts","get_session_controller","claim_session","release_session" };
	static readonly HashSet<string> UnroutedGatewayOperations=new(StringComparer.Ordinal) { "get_started","doctor","get_workflow_help","get_local_deployment","launch_local_host","rollback_local_deployment","uninstall_local_deployment","create_remote_host_package","list_hosts" };
	static string Name(object tool) => (string)tool.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)!.GetValue(tool)!;
	static string Description(object tool) => (string)tool.GetType().GetProperty("description", BindingFlags.Public | BindingFlags.Instance)!.GetValue(tool)!;
	static IReadOnlyDictionary<string,object> InputProperties(object tool) {
		var schema=tool.GetType().GetProperty("inputSchema",BindingFlags.Public|BindingFlags.Instance)!.GetValue(tool)!;
		return (IReadOnlyDictionary<string,object>)schema.GetType().GetProperty("properties",BindingFlags.Public|BindingFlags.Instance)!.GetValue(schema)!;
	}
	public static TheoryData<string> ToolNames {
		get { var data=new TheoryData<string>(); foreach (var tool in ToolCatalog.All) data.Add(Name(tool)); return data; }
	}

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_advertised_tool_is_an_operation_the_extension_implements(string tool) =>
		Assert.True(GatewayOperations.Contains(tool) || CapabilityCatalog.IsKnownOperation(tool), $"tools/list advertises '{tool}', which is neither a Gateway operation nor in CapabilityCatalog.Operations.");

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_gateway_deadline_outlasts_the_extension_bound(string tool) {
		if (GatewayOperations.Contains(tool)) return;
		var bound=CapabilityCatalog.BoundMs(tool);

		Assert.True(ToolCatalog.DeadlineSeconds(tool)*1000 > bound, $"'{tool}' deadline {ToolCatalog.DeadlineSeconds(tool)}s does not outlast its {bound}ms bound.");
	}

	[Fact]
	public void An_unknown_tool_still_gets_a_usable_deadline() => Assert.Equal(8, ToolCatalog.DeadlineSeconds("not_a_tool"));

	[Fact]
	public void Host_unavailable_recovery_directs_local_clients_to_the_launcher() {
		var guidance=ToolCatalog.ErrorGuidance("host_unavailable");
		Assert.Equal("launch_local_host",guidance.Tool);
		Assert.Contains("launch_local_host",guidance.Recovery);
	}

	[Fact]
	public void Deadline_recovery_recommends_narrowing_the_query() {
		var guidance=ToolCatalog.ErrorGuidance("deadline_exceeded");
		Assert.Contains("Narrow",guidance.Recovery);
		Assert.NotEqual("doctor",guidance.Tool);
	}

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

	/// <summary>An agent that passed allow_func_eval:true to call a pure static method was refused with
	/// "This expression causes side effects", concluded the override flag did not work, and computed the
	/// answer by hand — wrongly. The flag was honored; the side-effects gate is a separate one and is
	/// checked first. Nothing in the tool text said so, so keep it said.</summary>
	[Fact]
	public void Evaluate_says_that_a_method_call_needs_both_gates_not_just_allow_func_eval() {
		var tool=ToolCatalog.All.Single(t=>Name(t)=="evaluate");
		var description=Description(tool);
		Assert.Contains("allow_side_effects",description,StringComparison.Ordinal);
		Assert.Contains("BOTH",description,StringComparison.Ordinal);
		var properties=InputProperties(tool);
		var funcEval=(string)properties["allow_func_eval"].GetType().GetProperty("description")!.GetValue(properties["allow_func_eval"])!;
		var sideEffects=(string)properties["allow_side_effects"].GetType().GetProperty("description")!.GetValue(properties["allow_side_effects"])!;
		Assert.Contains("does NOT imply allow_side_effects",funcEval,StringComparison.Ordinal);
		Assert.Contains("allow_func_eval",sideEffects,StringComparison.Ordinal);
	}

	/// <summary>wait_for_stop's cursor is caller-supplied and reads are non-destructive, so a bare call
	/// replays retained history. That is defensible — losing a stop is worse than repeating one — but only
	/// if the tool says it, and names the field to pass next. An agent polling it blind re-processed old
	/// stops and had to discover after_event_id by trial and error.</summary>
	[Theory]
	[InlineData("wait_for_stop")]
	[InlineData("wait_for_event")]
	[InlineData("get_events")]
	public void Event_cursor_tools_state_that_the_caller_owns_the_cursor(string name) {
		var tool=ToolCatalog.All.Single(t=>Name(t)==name);
		Assert.Contains("last_event_id",Description(tool),StringComparison.Ordinal);
		var cursor=InputProperties(tool)["after_event_id"];
		var description=(string)cursor.GetType().GetProperty("description")!.GetValue(cursor)!;
		Assert.Contains("last_event_id",description,StringComparison.Ordinal);
		Assert.Contains("Default 0",description,StringComparison.Ordinal);
	}

	[Fact]
	public void Multi_target_lifecycle_tools_expose_process_selectors() {
		foreach(var name in new[]{"pause","continue","detach","terminate"}) {
			var properties=InputProperties(ToolCatalog.All.Single(t=>Name(t)==name));
			Assert.True(properties.ContainsKey("process_id"));
		}
	}

	[Fact]
	public void Every_extension_tool_accepts_host_routing_and_list_hosts_does_not() {
		foreach (var tool in ToolCatalog.All) {
			var name=Name(tool);
			var properties=InputProperties(tool);
			if (UnroutedGatewayOperations.Contains(name)) {
				if(name=="create_remote_host_package") Assert.True(properties.ContainsKey("host_id"));
				else Assert.False(properties.ContainsKey("host_id"));
			}
			else {
				Assert.True(properties.TryGetValue("host_id",out var host));
				Assert.Equal("string",host.GetType().GetProperty("type")!.GetValue(host));
			}
		}
	}

	[Fact]
	public void Tool_schemas_survive_the_actual_http_serializer() {
		var json=System.Text.Json.JsonSerializer.Serialize(new { tools=ToolCatalog.All });
		using var document=System.Text.Json.JsonDocument.Parse(json);
		var tools=document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
		var hostInfo=tools.Single(tool=>tool.GetProperty("name").GetString()=="get_host_info");
		var listHosts=tools.Single(tool=>tool.GetProperty("name").GetString()=="list_hosts");

		Assert.Equal("string",hostInfo.GetProperty("inputSchema").GetProperty("properties").GetProperty("host_id").GetProperty("type").GetString());
		Assert.False(listHosts.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("host_id",out _));
	}

	[Fact]
	public void Onboarding_and_deployment_tools_are_discoverable_and_annotated() {
		var names=ToolCatalog.All.Select(Name).ToHashSet(StringComparer.Ordinal);
		foreach(var name in new[]{"get_started","doctor","get_workflow_help","get_local_deployment","launch_local_host","rollback_local_deployment","uninstall_local_deployment","create_remote_host_package","get_remote_host_readiness","revoke_remote_host","step_and_inspect","trace_calls","run_to_method","run_to_location"}) Assert.Contains(name,names);
		Assert.DoesNotContain("plan_local_deployment",names); Assert.DoesNotContain("deploy_local_host",names); Assert.DoesNotContain("plan_remote_host_package",names);
		Assert.Contains("get_started",ToolCatalog.Instructions,StringComparison.Ordinal);
		Assert.NotNull(ToolCatalog.ReadResource("dgspy://guide/getting-started"));
		Assert.Null(ToolCatalog.ReadResource("dgspy://guide/missing"));
		var serialized=System.Text.Json.JsonSerializer.Serialize(ToolCatalog.All.Single(tool=>Name(tool)=="uninstall_local_deployment"));
		Assert.Contains("\"destructiveHint\":true",serialized,StringComparison.Ordinal);
	}
}
