using System.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The gateway used to carry its own hardcoded per-tool deadline table, which is how a
/// successful 10 s attach once got abandoned by an 8 s caller deadline. The extension now advertises its
/// bounds and the gateway derives deadlines from them; these tests are what keeps the two from drifting
/// apart again.</summary>
public sealed class ToolCatalogTests {
	static string RepoRoot {
		get {
			for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
				if(File.Exists(Path.Combine(directory.FullName,"AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName,"dgSpy.Gateway"))) return directory.FullName;
			throw new InvalidOperationException("Could not locate the dgSpy repository root.");
		}
	}
	[Fact]
	public void GatewayStartupKeepsStableTokenAndCliSerializesStarts() {
		var gateway=File.ReadAllText(Path.Combine(RepoRoot,"dgSpy.Gateway","Program.cs"));
		var cli=File.ReadAllText(Path.Combine(RepoRoot,"dgSpy.Cli","Program.cs"));
		Assert.Contains("EnsureState(tokenFile",gateway,StringComparison.Ordinal);
		Assert.DoesNotContain("File.WriteAllText(tokenFile, token)",gateway,StringComparison.Ordinal);
		Assert.Contains("Local\\dgSpy.Gateway.Start.Semaphore",cli,StringComparison.Ordinal);
		Assert.Contains("new Semaphore(1,1",cli,StringComparison.Ordinal);
		Assert.DoesNotContain("new Mutex(false",cli,StringComparison.Ordinal);
		Assert.Contains("if(await HealthyAsync()) return",cli,StringComparison.Ordinal);
	}

	[Fact]
	public void StdioDiscoveryStaysInTheCliUntilARealCallNeedsTheGateway() {
		var cli=File.ReadAllText(Path.Combine(RepoRoot,"dgSpy.Cli","Program.cs"));
		Assert.Contains("if(method==\"initialize\")",cli,StringComparison.Ordinal);
		Assert.Contains("if(method==\"tools/list\")",cli,StringComparison.Ordinal);
		Assert.Contains("if(method==\"resources/list\")",cli,StringComparison.Ordinal);
		Assert.Contains("startup ??= StartGatewayBehindMcpAsync()",cli,StringComparison.Ordinal);
	}
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

	[Theory]
	[InlineData("run_atomic_action")]
	[InlineData("get_atomic_action_status")]
	[InlineData("cancel_atomic_action")]
	public void Atomic_action_operations_and_results_are_explicitly_versioned(string operation) {
		var capability=CapabilityCatalog.Operations.Single(value=>value.Operation==operation);
		Assert.Equal(1,capability.OperationVersion); Assert.Equal(1,capability.ResultSchemaVersion);
		var tool=ProtocolJson.ToNode(ToolCatalog.All.Single(value=>Name(value)==operation))!.AsObject();
		Assert.Equal(1,(int?)tool["inputSchema"]?["properties"]?["operation_version"]?["const"]);
		Assert.Contains("operation_version",ProtocolJson.FromNode<string[]>(tool["inputSchema"]?["required"]) ?? Array.Empty<string>());
	}

	// T08c.
	[Fact]
	public void Cancelling_an_atomic_action_demands_no_execution_version_it_could_not_hold() {
		var cancel=ProtocolJson.ToNode(ToolCatalog.All.Single(value=>Name(value)=="cancel_atomic_action"))!.AsObject();
		var required=ProtocolJson.FromNode<string[]>(cancel["inputSchema"]?["required"]) ?? Array.Empty<string>();
		// Measured: execution_version did not move during any observed run_atomic_action, and a value the
		// run stamps onto its own response only reaches the caller once the action is over - so the argument
		// could never have guarded a cancellation. It must be gone from the schema, not merely optional.
		Assert.DoesNotContain("expected_execution_version",required);
		Assert.Null(cancel["inputSchema"]?["properties"]?["expected_execution_version"]);
		Assert.Contains("operation_version",required);
		Assert.Contains("session_id",required);
		Assert.Contains("action_id",required);
		Assert.False(MutationGuards.RequiresVersionGuard("cancel_atomic_action"));
		// The exemption is exactly one operation wide: every other mutation still gets its guard injected.
		Assert.True(MutationGuards.RequiresVersionGuard("run_atomic_action"));
		Assert.True(MutationGuards.RequiresVersionGuard("continue"));
	}

	[Fact]
	public void Starting_an_atomic_action_is_registered_versioned_and_keeps_its_own_guards() {
		var start=CapabilityCatalog.Operations.Single(value=>value.Operation=="start_atomic_action");
		Assert.True(start.MutatesSession);
		Assert.Equal(1,start.OperationVersion);
		// Its bound is the validate-and-schedule cost, not the action's, because it returns after
		// registration - before lease acquisition and before breakpoint binding.
		Assert.True(start.MaxDurationMs<CapabilityCatalog.BoundMs("run_atomic_action"));
		Assert.True(MutationGuards.RequiresStop("start_atomic_action"));
		Assert.False(MutationGuards.RequiresStop("cancel_atomic_action"));
		var tool=ProtocolJson.ToNode(ToolCatalog.All.Single(value=>Name(value)=="start_atomic_action"))!.AsObject();
		var required=ProtocolJson.FromNode<string[]>(tool["inputSchema"]?["required"]) ?? Array.Empty<string>();
		Assert.Contains("expected_execution_version",required);
		Assert.Contains("expected_stop_id",required);
		// run_atomic_action stays as the blocking version-1 compatibility operation; the asynchronous shape
		// is a new operation beside it, not a silent change of meaning under the same version.
		Assert.Equal(1,CapabilityCatalog.Operations.Single(value=>value.Operation=="run_atomic_action").OperationVersion);
	}

	[Fact]
	public void Host_unavailable_recovery_directs_local_clients_to_the_launcher() {
		var guidance=ToolCatalog.ErrorGuidance("host_unavailable");
		Assert.Equal("launch_local_host",guidance.Tool);
		Assert.Contains("launch_local_host",guidance.Recovery);
	}

	[Fact]
	public void Deadline_recovery_never_blindly_retries_a_mutation() {
		var guidance=ToolCatalog.ErrorGuidance("deadline_exceeded");
		Assert.Contains("assume it may have applied",guidance.Recovery,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("get_session_state",guidance.Recovery,StringComparison.Ordinal);
		Assert.Contains("read-only",guidance.Recovery,StringComparison.OrdinalIgnoreCase);
		Assert.Equal("get_session_state",guidance.Tool);
	}

	[Fact]
	public void Il_breakpoint_description_matches_the_sequence_point_snap_policy() {
		var tool=ToolCatalog.All.Single(t=>Name(t)=="set_il_breakpoint");
		var description=Description(tool);
		Assert.Contains("at or after",description,StringComparison.Ordinal);
		Assert.Contains("final preceding",description,StringComparison.Ordinal);
		Assert.DoesNotContain("retried at method entry",description,StringComparison.OrdinalIgnoreCase);
		var snap=InputProperties(tool)["snap_to_sequence_point"];
		var snapDescription=(string)snap.GetType().GetProperty("description")!.GetValue(snap)!;
		Assert.Contains("at or after",snapDescription,StringComparison.Ordinal);
		Assert.Contains("final preceding",snapDescription,StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("list_threads")]
	[InlineData("invoke_method")]
	public void Unsafe_point_entry_tools_name_the_composed_run_to_workflows(string name) {
		var description=Description(ToolCatalog.All.Single(t=>Name(t)==name));
		Assert.Contains("run_to_method",description,StringComparison.Ordinal);
		Assert.Contains("run_to_location",description,StringComparison.Ordinal);
		Assert.Contains("drive",description,StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Measured_blind_run_decision_points_distinguish_composed_arrival_from_manual_or_injected_execution() {
		var threads=Description(ToolCatalog.All.Single(t=>Name(t)=="list_threads"));
		Assert.Contains("recovery",threads,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("manually assembling",threads,StringComparison.OrdinalIgnoreCase);

		var assignment=Description(ToolCatalog.All.Single(t=>Name(t)=="set_value"));
		Assert.Contains("then use run_to_method or run_to_location",assignment,StringComparison.Ordinal);
		Assert.Contains("do not manually assemble",assignment,StringComparison.OrdinalIgnoreCase);

		var invocation=Description(ToolCatalog.All.Single(t=>Name(t)=="invoke_method"));
		Assert.Contains("not natural-control-flow arrival",invocation,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("breakpoints inside the invoked call are not serviced",invocation,StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("run_to_method")]
	[InlineData("run_to_location")]
	public void Run_to_tools_define_timeout_as_no_arrival_with_the_target_running(string name) {
		var description=Description(ToolCatalog.All.Single(t=>Name(t)==name));
		Assert.Contains("wait.timed_out",description,StringComparison.Ordinal);
		Assert.Contains("no arrival",description,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("target is running again",description,StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("evaluate")]
	[InlineData("invoke_method")]
	[InlineData("get_members")]
	public void Frame_context_entry_tools_explain_module_scoped_compilation(string name) {
		var description=Description(ToolCatalog.All.Single(t=>Name(t)==name));
		Assert.Contains("module",description,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("frame",description,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("get_callstack",description,StringComparison.Ordinal);
	}

	[Fact]
	public void Callstack_description_exposes_frame_module_selection() {
		var description=Description(ToolCatalog.All.Single(t=>Name(t)=="get_callstack"));
		Assert.Contains("module",description,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("frame_index",description,StringComparison.Ordinal);
		Assert.Contains("fully qualified",description,StringComparison.OrdinalIgnoreCase);
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

	/// <summary>With ~97 tools and a per-schema loading cost, an agent does not browse the catalog — it
	/// reaches for the obvious tool and finds everything else through that one's description. A
	/// capability nothing points at is therefore invisible, and invisible is worse than absent: an agent
	/// that could not find conditional breakpoints hand-computed the loop iteration instead and reported
	/// an answer, believed verified, that was wrong by one iteration. These assertions are on the
	/// pointers, not the prose, so a rewrite may reword them but not drop them.</summary>
	[Theory]
	// The entry point for "stop at one specific iteration". Both breakpoint-setting tools must name the
	// tool that attaches the condition; neither takes one itself.
	[InlineData("set_breakpoint","update_breakpoint")]
	[InlineData("set_il_breakpoint","update_breakpoint")]
	// A step loop that re-reads state each time is what the composite already does in one call.
	[InlineData("step_into","step_and_inspect")]
	[InlineData("step_over","step_and_inspect")]
	[InlineData("step_out","step_and_inspect")]
	// Resume-and-wait, composed.
	[InlineData("continue","run_to_method")]
	// The blocking form of the same stream.
	[InlineData("get_output","wait_for_output")]
	// Confirming a removal needs the name-scoped read; without it the only view is thousands of
	// framework defaults, and a caller cannot prove its own cleanup happened.
	[InlineData("remove_exception_policy","list_exception_policies")]
	// Clearing every breakpoint takes the human's hand-set ones with it.
	[InlineData("clear_breakpoints","export_breakpoints")]
	public void Tools_an_agent_reaches_for_first_name_the_capability_reached_through_them(string tool,string referenced) =>
		Assert.Contains(referenced,Description(ToolCatalog.All.Single(t=>Name(t)==tool)),StringComparison.Ordinal);

	/// <summary>The two exception list tools report the same entries, so they must narrow a lookup the
	/// same way. They used to disagree, which is half of why this pair read as two separate features
	/// with two vocabularies rather than one thing with two views.</summary>
	[Fact]
	public void Both_exception_list_tools_can_be_scoped_to_a_single_entry() {
		foreach(var name in new[]{"list_exception_policies","list_exception_breakpoints"}) {
			var properties=InputProperties(ToolCatalog.All.Single(t=>Name(t)==name));
			Assert.True(properties.ContainsKey("name"),$"{name} cannot be scoped to one entry.");
			Assert.True(properties.ContainsKey("category"),$"{name} cannot be scoped to one category.");
		}
	}

	[Fact]
	public void Multi_target_lifecycle_tools_expose_process_selectors() {
		foreach(var name in new[]{"pause","continue","detach","terminate"}) {
			var properties=InputProperties(ToolCatalog.All.Single(t=>Name(t)==name));
			Assert.True(properties.ContainsKey("process_id"));
		}
	}

	[Fact]
	public void HookLab_initialization_owns_injection_and_install_requires_no_carrier() {
		var initialize=ToolCatalog.All.Single(tool=>Name(tool)=="initialize_hooklab");
		Assert.Equal(new[]{"expected_execution_version","host_id","process_id","session_id"},InputProperties(initialize).Keys.OrderBy(value=>value,StringComparer.Ordinal));
		var install=ToolCatalog.All.Single(tool=>Name(tool)=="install_hook");
		var properties=InputProperties(install);
		Assert.DoesNotContain("arrival_module_id",properties.Keys);
		Assert.DoesNotContain("arrival_method_token",properties.Keys);
		Assert.DoesNotContain("arrival_il_offset",properties.Keys);
		Assert.DoesNotContain("expected_stop_id",properties.Keys);
	}

	[Fact]
	public void Every_exact_module_tool_exposes_the_opaque_identity() {
		foreach(var name in new[]{"run_to_method","run_to_location","set_il_breakpoint","set_instruction_pointer","list_types","list_members","get_il","get_csharp","set_breakpoint","find_references","find_implementations","get_metadata","analyze_symbol","get_raw_module"}) {
			var tool=ToolCatalog.All.Single(value=>Name(value)==name);
			Assert.True(InputProperties(tool).ContainsKey("module_id"),$"{name} cannot consume the exact module identity returned by discovery.");
			var wire=ProtocolJson.ToNode(tool)!.AsObject();
			var required=ProtocolJson.FromNode<string[]>(wire["inputSchema"]?["required"]) ?? Array.Empty<string>();
			Assert.DoesNotContain("module",required);
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
		Assert.NotNull(ToolCatalog.ReadResource("dgspy://guide/workflows"));
		Assert.Null(ToolCatalog.ReadResource("dgspy://guide/missing"));
		var serialized=System.Text.Json.JsonSerializer.Serialize(ToolCatalog.All.Single(tool=>Name(tool)=="uninstall_local_deployment"));
		Assert.Contains("\"destructiveHint\":true",serialized,StringComparison.Ordinal);
	}

	[Fact]
	public void Shared_instructions_are_not_systemically_repeated_across_tool_descriptions() {
		var prefixed=ToolCatalog.All.Where(tool=>Description(tool).StartsWith(ToolCatalog.Instructions,StringComparison.Ordinal)).ToArray();
		Assert.Equal("get_started",Name(Assert.Single(prefixed)));

		var resource=System.Text.Json.JsonSerializer.Serialize(ToolCatalog.ReadResource("dgspy://guide/getting-started"));
		Assert.Contains(ToolCatalog.Instructions,resource,StringComparison.Ordinal);
	}

	[Fact]
	public void Workflow_help_schema_and_resource_render_the_authoritative_catalog() {
		var help=ToolCatalog.All.Single(tool=>Name(tool)=="get_workflow_help");
		var topic=InputProperties(help)["topic"];
		var advertised=(string[])topic.GetType().GetProperty("enum")!.GetValue(topic)!;
		Assert.Equal(WorkflowCatalog.Topics.Select(item=>item.Topic),advertised);

		var resource=System.Text.Json.JsonSerializer.Serialize(ToolCatalog.ReadResource("dgspy://guide/workflows"));
		foreach(var workflow in WorkflowCatalog.Topics) {
			Assert.Contains($"\\u0022topic\\u0022:\\u0022{workflow.Topic}\\u0022",resource,StringComparison.Ordinal);
			foreach(var tool in workflow.Tools) Assert.Contains(tool,resource,StringComparison.Ordinal);
		}
	}
}
