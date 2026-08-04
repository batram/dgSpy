using System.Linq;
using System.Reflection;
using dgSpy.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dgSpy.Protocol.Tests;

public class RpcContractTests {
	[Fact]
	public void Request_round_trips_with_snake_case_wire_names() {
		var request = new RpcRequest { Operation = "attach", RequestId = "abc" };
		request.Arguments["program_id"] = "1234:guid:CLR v4.0.30319";

		var wire = JObject.Parse(JsonConvert.SerializeObject(request));

		Assert.Equal("attach", (string?)wire["operation"]);
		Assert.Equal("abc", (string?)wire["request_id"]);
		Assert.Equal(ProtocolVersion.Current, (int?)wire["version"]);
		Assert.Equal("1234:guid:CLR v4.0.30319", (string?)wire["arguments"]!["program_id"]);
	}

	[Fact]
	public void Failure_carries_a_structured_code_and_no_result() {
		var wire = JObject.Parse(JsonConvert.SerializeObject(RpcResponse.Failure("r1", "stale_handle", "gone")));

		Assert.Equal("stale_handle", (string?)wire["error"]!["code"]);
		Assert.Equal("gone", (string?)wire["error"]!["message"]);
		Assert.Null(wire["result"]);
	}

	[Fact]
	public void Success_omits_the_error_member_entirely() {
		// An agent must be able to branch on the presence of "error" alone.
		var wire = JObject.Parse(JsonConvert.SerializeObject(RpcResponse.Success("r1", new Handshake())));

		Assert.Null(wire["error"]);
		Assert.NotNull(wire["result"]);
	}

	[Fact]
	public void Deadline_survives_a_round_trip_as_utc() {
		var deadline = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
		var json = JsonConvert.SerializeObject(new RpcRequest { DeadlineUtc = deadline });

		var restored = JsonConvert.DeserializeObject<RpcRequest>(json)!;

		Assert.Equal(deadline, restored.DeadlineUtc!.Value.ToUniversalTime());
	}

	[Fact]
	public void Event_results_preserve_normalized_stop_and_cursor_metadata() {
		var result=new WaitResult {
			OldestEventId=9, OldestAvailableCursor=8, LastEventId=11, Truncated=true,
			Events=new[] { new DebugEvent {
				EventId=11, Kind="stopped", StateVersion=4, ProcessId=42, ThreadId="42:7",
				StopReason="breakpoint", BreakpointId=3, Module="Target.exe", MethodToken=0x06000001, IlOffset=12,
			} },
		};

		var wire=JObject.Parse(JsonConvert.SerializeObject(result));

		Assert.True((bool?)wire["truncated"]);
		Assert.Equal(8,(long?)wire["oldest_available_cursor"]);
		Assert.Equal("breakpoint",(string?)wire["events"]![0]!["stop_reason"]);
		Assert.Equal(3,(int?)wire["events"]![0]!["breakpoint_id"]);
		Assert.Equal(12u,(uint?)wire["events"]![0]!["il_offset"]);
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

		var wire = JObject.Parse(JsonConvert.SerializeObject(frame));

		Assert.Equal(@"C:\t\Milestone1Target.exe", (string?)wire["module"]);
		Assert.Equal(100663298u, (uint?)wire["method_token"]);
		Assert.Equal(18u, (uint?)wire["il_offset"]);
	}

	[Fact]
	public void Program_reports_runtime_guid_separately_from_kind_guid() {
		// .NET Framework and Unity share one runtime *kind* GUID. Without the runtime GUID an agent
		// cannot tell the two supported engines apart.
		var program = new ProgramInfo {
			RuntimeGuid = "1b1e3f4e-0000-0000-0000-000000000001",
			RuntimeKindGuid = "03cfde68-877e-4dd7-9a14-5c100b37a01a",
		};

		var wire = JObject.Parse(JsonConvert.SerializeObject(program));

		Assert.NotEqual((string?)wire["runtime_kind_guid"], (string?)wire["runtime_guid"]);
	}

	[Fact]
	public void Thread_and_frame_selection_have_explicit_wire_identity() {
		var thread = JObject.Parse(JsonConvert.SerializeObject(new ThreadInfo {
			ThreadId = "1234:99", ProcessId = 1234, OsThreadId = 99, ManagedThreadId = 7, HasManagedFrames = true,
		}));
		var frame = JObject.Parse(JsonConvert.SerializeObject(new FrameInfo {
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
		var wire = JObject.Parse(JsonConvert.SerializeObject(new RemoveBreakpointResult {
			BreakpointId = 42, Removed = true, StateVersion = 9,
		}));

		Assert.Equal(42, (int?)wire["breakpoint_id"]);
		Assert.True((bool?)wire["removed"]);
	}

	[Fact]
	public void Session_summary_reports_whether_detaching_is_safe() {
		var wire = JObject.Parse(JsonConvert.SerializeObject(new SessionSummary { CanDetachWithoutTerminating = true }));

		Assert.True((bool?)wire["can_detach_without_terminating"]);
	}

	[Fact]
	public void Detach_result_distinguishes_detached_from_terminated() {
		var wire = JObject.Parse(JsonConvert.SerializeObject(new DetachResult { Detached = false, Terminated = true }));

		Assert.False((bool?)wire["detached"]);
		Assert.True((bool?)wire["terminated"]);
	}

	[Fact]
	public void Terminal_event_preserves_process_exit_details() {
		var wire=JObject.FromObject(new DebugEvent { EventId=9,Kind="session_exited",StateVersion=12,Terminal=true,ProcessId=4242,ExitCode=23,Reason="target_exited" });

		Assert.True((bool?)wire["terminal"]);
		Assert.Equal(4242,(int?)wire["process_id"]);
		Assert.Equal(23,(int?)wire["exit_code"]);
		Assert.Equal("target_exited",(string?)wire["reason"]);
	}

	[Fact]
	public void Program_reports_provider_names_a_caller_can_pass_back() {
		// attach_provider used to carry the runtime GUID, which is not something list_programs accepts.
		var wire = JObject.Parse(JsonConvert.SerializeObject(new ProgramInfo { AttachProviders = new[] { "DotNetFramework" } }));

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
		var capabilities = JObject.Parse(JsonConvert.SerializeObject(CapabilityCatalog.Describe("0.1.0")));
		var host = JObject.Parse(JsonConvert.SerializeObject(new HostInfo { HostId = CapabilityCatalog.HostId, MachineName = "TESTBOX" }));

		Assert.Equal("local", (string?)capabilities["host_id"]);
		Assert.Equal(ProtocolVersion.Current, (int?)capabilities["protocol_version"]);
		Assert.Equal(1, (int?)capabilities["limits"]!["max_concurrent_sessions"]);
		Assert.Equal("attach_endpoint", (string?)capabilities["engines"]![1]!["acquisition"]![0]);
		Assert.Equal("TESTBOX", (string?)host["machine_name"]);
		Assert.Equal("connected", (string?)host["connection_state"]);
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
		var capabilities = JObject.Parse(JsonConvert.SerializeObject(CapabilityCatalog.Describe("0.1.0")));

		// wait_for_stop filters on exactly this kind, so a caller must be able to discover it.
		Assert.Contains(EventKinds.Stopped, capabilities["event_kinds"]!.ToObject<string[]>()!);
		Assert.Contains(StopReasons.Breakpoint, capabilities["stop_reasons"]!.ToObject<string[]>()!);
		Assert.True(EventKinds.IsKnown(EventKinds.BreakpointHit));
		// The near miss that used to produce a clean empty wait instead of an error.
		Assert.False(EventKinds.IsKnown("breakpoint"));
	}

	[Fact]
	public void Phase4_vocabularies_are_advertised_and_rejectable() {
		var capabilities = JObject.Parse(JsonConvert.SerializeObject(CapabilityCatalog.Describe("0.1.0")));

		Assert.Equal(new[] { "into", "over", "out" }, capabilities["step_kinds"]!.ToObject<string[]>());
		Assert.Contains("when_changed", capabilities["condition_kinds"]!.ToObject<string[]>()!);
		Assert.Contains("multiple_of", capabilities["hit_count_kinds"]!.ToObject<string[]>()!);
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
		var nullReference = JObject.Parse(JsonConvert.SerializeObject(new EvaluatedValue { Name = "s", Display = "null", Value = null, HasRawValue = true }));
		var optimizedAway = JObject.Parse(JsonConvert.SerializeObject(new EvaluatedValue { Name = "s", Error = "Optimized away", HasRawValue = false }));

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
		var chunk=JObject.Parse(JsonConvert.SerializeObject(new RawModuleChunk { Module="a.dll",Offset=4,Count=2,TotalSize=10,Truncated=true,Sha256="abc",DataBase64="AAE=" }));
		Assert.Equal(10,(int?)chunk["total_size"]);
		Assert.Equal("AAE=",(string?)chunk["data_base64"]);
		var search=JObject.Parse(JsonConvert.SerializeObject(new TextSearchResult { ScannedMethods=200,ScanTruncated=true }));
		Assert.Equal(200,(int?)search["scanned_methods"]);
		Assert.True((bool?)search["scan_truncated"]);
	}

	[Fact]
	public void A_step_result_round_trips_with_snake_case_wire_names() {
		var step = JObject.Parse(JsonConvert.SerializeObject(new StepResult {
			SessionId = "s", ThreadId = "100:200", StepKind = StepKinds.Over, CursorEventId = 42, Completed = false,
		}));

		Assert.Equal("over", (string?)step["step_kind"]);
		Assert.Equal(42, (long?)step["cursor_event_id"]);
		// An unfinished step must not look like a failed one: completed is present and false, error absent.
		Assert.False((bool?)step["completed"]);
		Assert.Null(step["error"]);
	}
}
