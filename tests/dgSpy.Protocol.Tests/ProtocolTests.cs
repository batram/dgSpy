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
}
