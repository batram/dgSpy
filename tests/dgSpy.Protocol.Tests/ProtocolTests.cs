using System.Linq;
using System.Reflection;
using dgSpy.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Protocol.Tests;

public class RpcContractTests {
	[Fact]
	public void Request_round_trips_with_snake_case_wire_names() {
		var request = new RpcRequest { Operation = "attach", RequestId = "abc", HostId="host-a", AuthenticationToken="secret" };
		request.Arguments["program_id"] = "1234:guid:CLR v4.0.30319";

		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(request));

		Assert.Equal("attach", (string?)wire["operation"]);
		Assert.Equal("abc", (string?)wire["request_id"]);
		Assert.Equal(ProtocolVersion.Current, (int?)wire["version"]);
		Assert.Equal("host-a",(string?)wire["host_id"]);
		Assert.Equal("secret",(string?)wire["authentication_token"]);
		Assert.Equal("1234:guid:CLR v4.0.30319", (string?)wire["arguments"]!["program_id"]);
	}

	[Fact]
	public void Failure_carries_a_structured_code_and_no_result() {
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(RpcResponse.Failure("r1", "stale_handle", "gone")));

		Assert.Equal("stale_handle", (string?)wire["error"]!["code"]);
		Assert.Equal("gone", (string?)wire["error"]!["message"]);
		Assert.Null(wire["result"]);
	}

	[Fact]
	public void Success_omits_the_error_member_entirely() {
		// An agent must be able to branch on the presence of "error" alone.
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(RpcResponse.Success("r1", new Handshake())));

		Assert.Null(wire["error"]);
		Assert.NotNull(wire["result"]);
	}

	[Fact]
	public void Deadline_survives_a_round_trip_as_utc() {
		var deadline = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
		var json = ProtocolJson.Serialize(new RpcRequest { DeadlineUtc = deadline });

		var restored = ProtocolJson.Deserialize<RpcRequest>(json)!;

		Assert.Equal(deadline, restored.DeadlineUtc!.Value.ToUniversalTime());
	}

	[Fact]
	public void Event_results_preserve_normalized_stop_and_cursor_metadata() {
		var result=new WaitResult {
			OldestEventId=9, OldestAvailableCursor=8, LastEventId=11, Truncated=true,
			Events=new[] { new DebugEvent {
				EventId=11, Kind="stopped", StateVersion=4, LifecycleVersion=2, ExecutionVersion=3, BreakpointsVersion=5, StopId="stop-a", ProcessId=42, ThreadId="42:7",
				StopReason="breakpoint", BreakpointId=3, Module="Target.exe", MethodToken=0x06000001, IlOffset=12,
			} },
		};

		var wire=ProtocolJson.ParseObject(ProtocolJson.Serialize(result));

		Assert.True((bool?)wire["truncated"]);
		Assert.Equal(8,(long?)wire["oldest_available_cursor"]);
		Assert.Equal("breakpoint",(string?)wire["events"]![0]!["stop_reason"]);
		Assert.Equal(3,(int?)wire["events"]![0]!["breakpoint_id"]);
		Assert.Equal(12u,(uint?)wire["events"]![0]!["il_offset"]);
		Assert.Equal(2,(long?)wire["events"]![0]!["lifecycle_version"]);
		Assert.Equal(3,(long?)wire["events"]![0]!["execution_version"]);
		Assert.Equal(5,(long?)wire["events"]![0]!["breakpoints_version"]);
		Assert.Equal("stop-a",(string?)wire["events"]![0]!["stop_id"]);
	}
}

public class IdentityContractTests {
	[Fact]
	public void Frame_exposes_the_fields_set_il_breakpoint_needs() {
		// The formatted name is display only; module + token + offset is the durable identity, and it
		// must survive serialization intact or agents cannot set a breakpoint on a frame they see.
		var frame = new FrameInfo {
			Name = "Int32 Program.Tick(Int32)",
			Module = @"C:\t\Milestone1Target.exe",
			ModuleName = "Milestone1Target.exe",
			MethodToken = 100663298,
			IlOffset = 18,
		};

		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(frame));

		Assert.Equal(@"C:\t\Milestone1Target.exe", (string?)wire["module"]);
		Assert.Equal(100663298u, (uint?)wire["method_token"]);
		Assert.Equal(18u, (uint?)wire["il_offset"]);
	}

	[Fact]
	public void Program_reports_runtime_guid_separately_from_kind_guid() {
		// .NET Framework and Unity share one runtime *kind* GUID. Without the runtime GUID an agent
		// cannot tell the two supported engines apart.
		var program = new ProgramInfo {
			CommandLine = @"dotnet C:\apps\worker.dll --queue jobs",
			RuntimeGuid = "1b1e3f4e-0000-0000-0000-000000000001",
			RuntimeKindGuid = "03cfde68-877e-4dd7-9a14-5c100b37a01a",
		};

		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(program));

		Assert.False(wire.ContainsKey("runtime_id"));
		Assert.Equal(@"dotnet C:\apps\worker.dll --queue jobs",(string?)wire["command_line"]);
		Assert.NotEqual((string?)wire["runtime_kind_guid"], (string?)wire["runtime_guid"]);
	}

	[Fact]
	public void Thread_and_frame_selection_have_explicit_wire_identity() {
		var thread = ProtocolJson.ParseObject(ProtocolJson.Serialize(new ThreadInfo {
			ThreadId = "1234:99", ProcessId = 1234, OsThreadId = 99, ManagedThreadId = 7, HasManagedFrames = true,
		}));
		var frame = ProtocolJson.ParseObject(ProtocolJson.Serialize(new FrameInfo {
			FrameId = "session:5:1234:99:2", ThreadId = "1234:99", FrameIndex = 2,
		}));

		Assert.Equal("1234:99", (string?)thread["thread_id"]);
		Assert.Equal(99ul, (ulong?)thread["os_thread_id"]);
		Assert.Equal(7ul, (ulong?)thread["managed_thread_id"]);
		Assert.True((bool?)thread["has_managed_frames"]);
		Assert.Equal("1234:99", (string?)frame["thread_id"]);
		Assert.Equal(2, (int?)frame["frame_index"]);
	}

	[Fact]
	public void Remove_breakpoint_result_names_the_exact_removed_id() {
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(new RemoveBreakpointResult {
			BreakpointId = 42, Removed = true, StateVersion = 9,
		}));

		Assert.Equal(42, (int?)wire["breakpoint_id"]);
		Assert.True((bool?)wire["removed"]);
	}

	[Fact]
	public void Session_summary_reports_whether_detaching_is_safe() {
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(new SessionSummary { CanDetachWithoutTerminating = true }));

		Assert.True((bool?)wire["can_detach_without_terminating"]);
	}

	[Fact]
	public void Detach_result_distinguishes_detached_from_terminated_and_reports_remaining_session() {
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(new DetachResult { Detached = false, Terminated = true, ProcessId = 42, SessionActive = true }));

		Assert.False((bool?)wire["detached"]);
		Assert.True((bool?)wire["terminated"]);
		Assert.Equal(42,(int?)wire["process_id"]);
		Assert.True((bool?)wire["session_active"]);
	}

	[Fact]
	public void Terminal_event_preserves_process_exit_details() {
		var wire=ProtocolJson.ToObject(new DebugEvent { EventId=9,Kind="session_exited",StateVersion=12,Terminal=true,ProcessId=4242,ExitCode=23,Reason="target_exited" });

		Assert.True((bool?)wire["terminal"]);
		Assert.Equal(4242,(int?)wire["process_id"]);
		Assert.Equal(23,(int?)wire["exit_code"]);
		Assert.Equal("target_exited",(string?)wire["reason"]);
	}

	[Fact]
	public void Program_reports_provider_names_a_caller_can_pass_back() {
		// attach_provider used to carry the runtime GUID, which is not something list_programs accepts.
		var wire = ProtocolJson.ParseObject(ProtocolJson.Serialize(new ProgramInfo { AttachProviders = new[] { "DotNetFramework" } }));

		Assert.Equal("DotNetFramework", (string?)wire["attach_providers"]![0]);
	}
}

public class CapabilityContractTests {
	[Fact]
	public void Every_operation_has_a_unique_name_and_a_positive_bound() {
		var operations = CapabilityCatalog.Operations;

		Assert.Equal(operations.Length, operations.Select(o => o.Operation).Distinct().Count());
		Assert.All(operations, o => Assert.True(o.MaxDurationMs > 0, $"{o.Operation} has no bound."));
	}

	[Fact]
	public void The_two_supported_engines_advertise_their_incompatible_breakpoint_rules() {
		// The one difference that silently breaks callers: CorDebug binds a breakpoint at any IL offset,
		// Mono rejects anything that is not a sequence point. Advertised, not assumed.
		var cordebug = CapabilityCatalog.Engines.Single(e => e.Engine == "cordebug");
		var unity = CapabilityCatalog.Engines.Single(e => e.Engine == "unity");

		Assert.True(cordebug.ArbitraryIlOffsetBreakpoints);
		Assert.False(cordebug.SequencePointBreakpointsOnly);
		Assert.False(unity.ArbitraryIlOffsetBreakpoints);
		Assert.True(unity.SequencePointBreakpointsOnly);
		Assert.True(cordebug.Discoverable);
		// Unity's endpoint targets emit no beacon, so no provider can enumerate them.
		Assert.False(unity.Discoverable);
		Assert.Equal(new[] { "attach_endpoint" }, unity.Acquisition);
	}

	[Fact]
	public void Cancellation_is_advertised_as_the_limitation_it_is() =>
		// dnSpy cannot abort a queued dispatcher callback or a started evaluation. Saying so beats
		// letting a caller infer that a deadline unwinds the debugger.
		Assert.False(CapabilityCatalog.Limits.CancelsInFlightWork);

	[Fact]
	public void Capabilities_and_host_info_round_trip_with_snake_case_wire_names() {
		var capabilities = ProtocolJson.ParseObject(ProtocolJson.Serialize(CapabilityCatalog.Describe("0.1.0")));
		var host = ProtocolJson.ParseObject(ProtocolJson.Serialize(new HostInfo { HostId = CapabilityCatalog.HostId, MachineName = "TESTBOX", ConnectionState="degraded",DispatcherState="degraded",DispatcherFaultCount=2,LastDispatcherFault="injected",EvaluationQueueState="busy",EvaluationPending=1 }));

		Assert.Equal("local", (string?)capabilities["host_id"]);
		Assert.Equal(ProtocolVersion.Current, (int?)capabilities["protocol_version"]);
		Assert.Equal(1, (int?)capabilities["limits"]!["max_concurrent_sessions"]);
		Assert.Equal("attach_endpoint", (string?)capabilities["engines"]![1]!["acquisition"]![0]);
		Assert.Equal("TESTBOX", (string?)host["machine_name"]);
		Assert.Equal("degraded", (string?)host["connection_state"]);
		Assert.Equal(2L,(long?)host["dispatcher_fault_count"]);
		Assert.Equal("injected",(string?)host["last_dispatcher_fault"]);
		Assert.Equal("busy",(string?)host["evaluation_queue_state"]);
	}

	[Fact]
	public void Runtime_capabilities_report_the_endpoint_host_identity() {
		var capabilities=CapabilityCatalog.Describe("0.1.0","host-a");

		Assert.Equal("host-a",capabilities.HostId);
	}

	[Fact]
	public void Every_declared_event_kind_is_listed_in_All() {
		// The realistic drift is adding a const and forgetting the array: the extension would then emit a
		// kind that get_capabilities never advertises and the kinds filter rejects as unknown.
		var declared = typeof(EventKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(f => f.IsLiteral && f.FieldType == typeof(string))
			.Select(f => (string)f.GetRawConstantValue()!)
			.ToArray();

		Assert.Equal(declared.OrderBy(k => k), EventKinds.All.OrderBy(k => k));
		Assert.Equal(EventKinds.All.Length, EventKinds.All.Distinct().Count());
	}

	[Fact]
	public void The_event_kind_vocabulary_is_advertised_and_checkable() {
		var capabilities = ProtocolJson.ParseObject(ProtocolJson.Serialize(CapabilityCatalog.Describe("0.1.0")));

		// wait_for_stop filters on exactly this kind, so a caller must be able to discover it.
		Assert.Contains(EventKinds.Stopped, ProtocolJson.FromNode<string[]>(capabilities["event_kinds"]!)!);
		Assert.Contains(StopReasons.Breakpoint, ProtocolJson.FromNode<string[]>(capabilities["stop_reasons"]!)!);
		Assert.True(EventKinds.IsKnown(EventKinds.BreakpointHit));
		// The near miss that used to produce a clean empty wait instead of an error.
		Assert.False(EventKinds.IsKnown("breakpoint"));
	}

	[Fact]
	public void Phase4_vocabularies_are_advertised_and_rejectable() {
		var capabilities = ProtocolJson.ParseObject(ProtocolJson.Serialize(CapabilityCatalog.Describe("0.1.0")));

		Assert.Equal(new[] { "into", "over", "out" }, ProtocolJson.FromNode<string[]>(capabilities["step_kinds"]!));
		Assert.Contains("when_changed", ProtocolJson.FromNode<string[]>(capabilities["condition_kinds"]!)!);
		Assert.Contains("multiple_of", ProtocolJson.FromNode<string[]>(capabilities["hit_count_kinds"]!)!);
		// dnSpy calls it GreaterThanOrEquals; the wire name says what it does without the caller
		// having to know dnSpy's enum.
		Assert.Contains("at_least", HitCountKinds.All);
		Assert.False(StepKinds.IsKnown("step_into"));
		Assert.False(HitCountKinds.IsKnown("greater_than_or_equals"));
	}

	[Fact]
	public void Every_phase4_operation_is_bounded_and_marked_for_mutation() {
		foreach (var name in new[] { "update_breakpoint", "set_exception_breakpoint", "step_into", "step_over", "step_out" }) {
			var op = CapabilityCatalog.Operations.Single(o => o.Operation == name);
			Assert.True(op.MaxDurationMs > 0, $"{name} has no bound");
			// Each of these changes debugger state the caller can observe afterwards, so a client that
			// serializes mutations has to be able to tell.
			Assert.True(op.MutatesSession, $"{name} is not marked as mutating");
		}
		Assert.False(CapabilityCatalog.Operations.Single(o => o.Operation == "list_exception_breakpoints").MutatesSession);
	}

	[Fact]
	public void An_unavailable_value_is_distinguishable_from_null_on_the_wire() {
		var nullReference = ProtocolJson.ParseObject(ProtocolJson.Serialize(new EvaluatedValue { Name = "s", Display = "null", Value = null, HasRawValue = true }));
		var optimizedAway = ProtocolJson.ParseObject(ProtocolJson.Serialize(new EvaluatedValue { Name = "s", Error = "Optimized away", HasRawValue = false }));

		// Both omit `value`, so has_raw_value is the only thing separating "this is null" from "the
		// runtime cannot tell you". A caller that conflates them reports a bug that is not there.
		Assert.Null(nullReference["value"]);
		Assert.Null(optimizedAway["value"]);
		Assert.True((bool?)nullReference["has_raw_value"]);
		Assert.False((bool?)optimizedAway["has_raw_value"]);
		Assert.Equal("Optimized away", (string?)optimizedAway["error"]);
	}

	[Fact]
	public void Phase5_operations_are_bounded_and_only_set_value_mutates() {
		foreach (var name in new[] { "evaluate", "get_members", "set_value", "get_exception", "add_watch", "list_watches", "remove_watch", "list_modules" })
			Assert.True(CapabilityCatalog.BoundMs(name) > 0, $"{name} has no bound");

		// Reading must not be marked as mutating, or a client that serializes mutations pointlessly
		// serializes every inspection too. set_value always executes in the target, so it does mutate.
		Assert.True(CapabilityCatalog.Operations.Single(o => o.Operation == "set_value").MutatesSession);
		foreach (var name in new[] { "evaluate", "get_members", "get_exception", "list_watches", "list_modules" })
			Assert.False(CapabilityCatalog.Operations.Single(o => o.Operation == name).MutatesSession, $"{name} claims to mutate");
	}

	[Fact]
	public void Phase6_operations_are_bounded_read_only_and_round_trip() {
		foreach (var name in new[] { "search_text", "find_references", "find_implementations", "get_metadata", "get_raw_module" }) {
			Assert.True(CapabilityCatalog.BoundMs(name) > 0, $"{name} has no bound");
			Assert.False(CapabilityCatalog.Operations.Single(o => o.Operation == name).MutatesSession, $"{name} claims to mutate");
		}
		var chunk=ProtocolJson.ParseObject(ProtocolJson.Serialize(new RawModuleChunk { Module="a.dll",Offset=4,Count=2,TotalSize=10,Truncated=true,Sha256="abc",DataBase64="AAE=" }));
		Assert.Equal(10,(int?)chunk["total_size"]);
		Assert.Equal("AAE=",(string?)chunk["data_base64"]);
		var search=ProtocolJson.ParseObject(ProtocolJson.Serialize(new TextSearchResult { ScannedMethods=200,ScanTruncated=true }));
		Assert.Equal(200,(int?)search["scanned_methods"]);
		Assert.True((bool?)search["scan_truncated"]);
	}

	[Fact]
	public void Phase7_capabilities_separate_inspection_from_audited_mutation() {
		var mutating=new[] { "invoke_method","create_object","write_memory","set_instruction_pointer" };
		var readOnly=new[] { "read_memory","get_disassembly","get_registers" };
		foreach (var name in mutating) Assert.True(CapabilityCatalog.Operations.Single(o=>o.Operation==name).MutatesSession,$"{name} must be labeled mutating");
		foreach (var name in readOnly) Assert.False(CapabilityCatalog.Operations.Single(o=>o.Operation==name).MutatesSession,$"{name} must remain read-only");
		Assert.All(mutating.Concat(readOnly),name=>Assert.True(CapabilityCatalog.BoundMs(name)>0,$"{name} has no bound"));

		var capabilities=ProtocolJson.ParseObject(ProtocolJson.Serialize(CapabilityCatalog.Describe("0.1.0")));
		Assert.True((bool?)capabilities["engines"]![0]!["memory_access"]);
		Assert.False((bool?)capabilities["engines"]![1]!["native_disassembly"]);
		Assert.True((bool?)capabilities["engines"]![0]!["registers"]);
		Assert.False((bool?)capabilities["engines"]![1]!["registers"]);
		Assert.Equal(10000,(int?)capabilities["limits"]!["max_evaluation_timeout_ms"]);
	}

	[Fact]
	public void Phase7_mutation_and_memory_results_are_explicit_on_the_wire() {
		var mutation=ProtocolJson.ParseObject(ProtocolJson.Serialize(new MutationResult { Completed=true,AuditId="audit-1",Capability="method_invocation" }));
		Assert.True((bool?)mutation["causes_side_effects"]);
		Assert.Equal("audit-1",(string?)mutation["audit_id"]);
		var memory=ProtocolJson.ParseObject(ProtocolJson.Serialize(new MemoryResult { Address=16,Length=2,DataBase64="AAE=" }));
		Assert.Equal("memory_access",(string?)memory["capability"]);
		Assert.Equal("AAE=",(string?)memory["data_base64"]);
	}

	[Fact]
	public void A_step_result_round_trips_with_snake_case_wire_names() {
		var step = ProtocolJson.ParseObject(ProtocolJson.Serialize(new StepResult {
			SessionId = "s", ThreadId = "100:200", StepKind = StepKinds.Over, CursorEventId = 42, Completed = false,
		}));

		Assert.Equal("over", (string?)step["step_kind"]);
		Assert.Equal(42, (long?)step["cursor_event_id"]);
		// An unfinished step must not look like a failed one: completed is present and false, error absent.
		Assert.False((bool?)step["completed"]);
		Assert.Null(step["error"]);
	}

	[Fact]
	public void Phase8_operations_are_bounded_and_permissions_are_machine_readable() {
		var mutating=new[]{"create_object_id","release_object_id","set_module_breakpoint","update_module_breakpoint","remove_module_breakpoint","import_breakpoints","set_exception_policy","remove_exception_policy","restore_exception_defaults","write_value_export"};
		var readOnly=new[]{"list_object_ids","evaluate_object_id","get_autos","get_output","wait_for_output","list_module_breakpoints","export_breakpoints","list_exception_categories","list_exception_policies","get_value_export","analyze_symbol"};
		Assert.All(mutating,name=>Assert.True(CapabilityCatalog.Operations.Single(o=>o.Operation==name).MutatesSession,name));
		Assert.All(readOnly,name=>Assert.False(CapabilityCatalog.Operations.Single(o=>o.Operation==name).MutatesSession,name));
		Assert.All(mutating.Concat(readOnly),name=>Assert.True(CapabilityCatalog.BoundMs(name)>0,name));
		var capabilities=ProtocolJson.ToObject(CapabilityCatalog.Describe("0.1.0"));
		Assert.True((bool?)capabilities["engines"]![0]!["object_ids"]);
		Assert.Equal(16*1024*1024,(int?)capabilities["limits"]!["max_value_export_bytes"]);
	}

	[Fact]
	public void Phase8_documents_and_chunks_round_trip_without_display_parsing() {
		var document=new BreakpointDocument { Code=new[]{new BreakpointInfo { Module="a.dll",MethodToken=0x06000001,IlOffset=2 }},Modules=new[]{new ModuleBreakpointInfo { ModuleName="Plugin*",IsLoaded=true }},ExceptionTotal=12,ExceptionTruncated=true };
		var json=ProtocolJson.ToObject(document);
		Assert.Equal("dgspy.breakpoints",(string?)json["format"]);
		Assert.Equal(0x06000001u,(uint?)json["code"]![0]!["method_token"]);
		Assert.Equal(12,(int?)json["exception_total"]);
		Assert.True((bool?)json["exception_truncated"]);
		var analysis=ProtocolJson.ToObject(new AnalysisResult { ScannedMethods=1000,ScanTruncated=true,Truncated=true });
		Assert.Equal(1000,(int?)analysis["scanned_methods"]);
		Assert.True((bool?)analysis["scan_truncated"]);
		var chunk=ProtocolJson.ToObject(new ValueExportChunk { TotalSize=4,Count=2,Sha256="abc",DataBase64="AAE=" });
		Assert.Equal("abc",(string?)chunk["sha256"]);
		Assert.Equal("AAE=",(string?)chunk["data_base64"]);
	}
}
