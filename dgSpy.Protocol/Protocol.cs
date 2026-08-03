using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dgSpy.Protocol {
	public static class ProtocolVersion { public const int Current = 1; }
	public sealed class RpcRequest {
		[JsonProperty("version")] public int Version { get; set; } = ProtocolVersion.Current;
		[JsonProperty("request_id")] public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
		[JsonProperty("operation")] public string Operation { get; set; } = "";
		[JsonProperty("deadline_utc")] public DateTime? DeadlineUtc { get; set; }
		[JsonProperty("arguments")] public JObject Arguments { get; set; } = new JObject();
	}
	public sealed class RpcResponse {
		[JsonProperty("version")] public int Version { get; set; } = ProtocolVersion.Current;
		[JsonProperty("request_id")] public string RequestId { get; set; } = "";
		[JsonProperty("result", NullValueHandling=NullValueHandling.Ignore)] public object? Result { get; set; }
		[JsonProperty("error", NullValueHandling=NullValueHandling.Ignore)] public RpcError? Error { get; set; }
		public static RpcResponse Success(string id, object value) => new RpcResponse { RequestId=id, Result=value };
		public static RpcResponse Failure(string id, string code, string message) => new RpcResponse { RequestId=id, Error=new RpcError { Code=code, Message=message } };
	}
	public sealed class RpcError { [JsonProperty("code")] public string Code { get; set; }="internal_error"; [JsonProperty("message")] public string Message { get; set; }=""; }
	public sealed class Handshake { [JsonProperty("protocol_version")] public int ProtocolVersion { get; set; }=Protocol.ProtocolVersion.Current; [JsonProperty("extension_version")] public string ExtensionVersion { get; set; }="0.1.0"; }
	public sealed class ProgramInfo {
		[JsonProperty("program_id")] public string ProgramId { get; set; }=""; [JsonProperty("pid")] public int ProcessId { get; set; }
		[JsonProperty("executable")] public string Executable { get; set; }=""; [JsonProperty("title")] public string Title { get; set; }="";
		[JsonProperty("architecture")] public string Architecture { get; set; }="";
		/// <summary>Engine discriminator, eg. "CLR v4.0.30319". RuntimeId itself has no string form —
		/// it implements only Equals/GetHashCode — so this is composed from typed fields.</summary>
		[JsonProperty("runtime_id")] public string RuntimeId { get; set; }="";
		[JsonProperty("runtime_name")] public string RuntimeName { get; set; }="";
		/// <summary>Distinguishes .NET Framework from Unity/Mono. Both share one runtime *kind* GUID,
		/// so only this tells the engines apart.</summary>
		[JsonProperty("runtime_guid")] public string RuntimeGuid { get; set; }="";
		[JsonProperty("runtime_kind_guid")] public string RuntimeKindGuid { get; set; }="";
		/// <summary>dnSpy attach-provider names that can produce this entry, ready to pass back as
		/// <c>provider_names</c>. Empty when the runtime has no known provider — an entry reached through
		/// attach_endpoint has none, because no provider ever enumerated it.</summary>
		[JsonProperty("attach_providers")] public string[] AttachProviders { get; set; }=Array.Empty<string>();
	}
	public sealed class SessionSummary {
		[JsonProperty("session_id")] public string SessionId { get; set; }=""; [JsonProperty("state")] public string State { get; set; }="";
		[JsonProperty("program_id")] public string ProgramId { get; set; }=""; [JsonProperty("state_version")] public long StateVersion { get; set; }
		[JsonProperty("last_event_id")] public long LastEventId { get; set; } [JsonProperty("process_ids")] public int[] ProcessIds { get; set; }=Array.Empty<int>();
		[JsonProperty("can_detach_without_terminating")] public bool CanDetachWithoutTerminating { get; set; }
	}
	public sealed class DetachResult {
		[JsonProperty("session_id")] public string SessionId { get; set; }=""; [JsonProperty("detached")] public bool Detached { get; set; }
		[JsonProperty("terminated")] public bool Terminated { get; set; } [JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	public sealed class SessionState {
		[JsonProperty("session_id")] public string SessionId { get; set; }=""; [JsonProperty("state")] public string State { get; set; }="running";
		[JsonProperty("state_version")] public long StateVersion { get; set; } [JsonProperty("last_event_id")] public long LastEventId { get; set; }
		[JsonProperty("process_ids")] public int[] ProcessIds { get; set; }=Array.Empty<int>();
		/// <summary>Why the session is <c>faulted</c>. dnSpy's own connect-failure text when it produced
		/// one, otherwise a deadline description. Absent for every other state.</summary>
		[JsonProperty("fault_message", NullValueHandling=NullValueHandling.Ignore)] public string? FaultMessage { get; set; }
		[JsonProperty("exit_code", NullValueHandling=NullValueHandling.Ignore)] public int? ExitCode { get; set; }
		[JsonProperty("terminal_reason", NullValueHandling=NullValueHandling.Ignore)] public string? TerminalReason { get; set; }
	}
	public sealed class DebugEvent {
		[JsonProperty("event_id")] public long EventId { get; set; }
		[JsonProperty("kind")] public string Kind { get; set; }="";
		[JsonProperty("state_version")] public long StateVersion { get; set; }
		[JsonProperty("timestamp_utc")] public DateTime TimestampUtc { get; set; }=DateTime.UtcNow;
		[JsonProperty("terminal")] public bool Terminal { get; set; }
		[JsonProperty("process_id", NullValueHandling=NullValueHandling.Ignore)] public int? ProcessId { get; set; }
		[JsonProperty("runtime_guid", NullValueHandling=NullValueHandling.Ignore)] public string? RuntimeGuid { get; set; }
		[JsonProperty("runtime_name", NullValueHandling=NullValueHandling.Ignore)] public string? RuntimeName { get; set; }
		[JsonProperty("thread_id", NullValueHandling=NullValueHandling.Ignore)] public string? ThreadId { get; set; }
		[JsonProperty("breakpoint_id", NullValueHandling=NullValueHandling.Ignore)] public int? BreakpointId { get; set; }
		[JsonProperty("module", NullValueHandling=NullValueHandling.Ignore)] public string? Module { get; set; }
		[JsonProperty("method_token", NullValueHandling=NullValueHandling.Ignore)] public uint? MethodToken { get; set; }
		[JsonProperty("il_offset", NullValueHandling=NullValueHandling.Ignore)] public uint? IlOffset { get; set; }
		[JsonProperty("stop_reason", NullValueHandling=NullValueHandling.Ignore)] public string? StopReason { get; set; }
		[JsonProperty("exception_id", NullValueHandling=NullValueHandling.Ignore)] public string? ExceptionId { get; set; }
		[JsonProperty("exception_message", NullValueHandling=NullValueHandling.Ignore)] public string? ExceptionMessage { get; set; }
		[JsonProperty("exception_first_chance", NullValueHandling=NullValueHandling.Ignore)] public bool? ExceptionFirstChance { get; set; }
		[JsonProperty("exception_unhandled", NullValueHandling=NullValueHandling.Ignore)] public bool? ExceptionUnhandled { get; set; }
		[JsonProperty("error", NullValueHandling=NullValueHandling.Ignore)] public string? Error { get; set; }
		[JsonProperty("exit_code", NullValueHandling=NullValueHandling.Ignore)] public int? ExitCode { get; set; }
		[JsonProperty("reason", NullValueHandling=NullValueHandling.Ignore)] public string? Reason { get; set; }
	}
	public class EventResult {
		[JsonProperty("events")] public DebugEvent[] Events { get; set; }=Array.Empty<DebugEvent>();
		[JsonProperty("oldest_event_id")] public long OldestEventId { get; set; }
		[JsonProperty("oldest_available_cursor")] public long OldestAvailableCursor { get; set; }
		[JsonProperty("last_event_id")] public long LastEventId { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
	}
	public sealed class WaitResult : EventResult { [JsonProperty("timed_out")] public bool TimedOut { get; set; } }
	public sealed class ThreadInfo {
		[JsonProperty("thread_id")] public string ThreadId { get; set; }="";
		[JsonProperty("process_id")] public int ProcessId { get; set; }
		[JsonProperty("os_thread_id")] public ulong OsThreadId { get; set; }
		[JsonProperty("managed_thread_id", NullValueHandling=NullValueHandling.Ignore)] public ulong? ManagedThreadId { get; set; }
		[JsonProperty("name")] public string Name { get; set; }="";
		[JsonProperty("kind")] public string Kind { get; set; }="";
		[JsonProperty("is_main")] public bool IsMain { get; set; }
		[JsonProperty("is_current")] public bool IsCurrent { get; set; }
		/// <summary>Present only when frame availability is already known. list_threads deliberately does
		/// not probe every thread: a Unity thread can exit during GET_FRAME_INFO and older Mono runtimes
		/// can omit the reply. Select a thread with get_callstack to discover its frames safely.</summary>
		[JsonProperty("has_managed_frames", NullValueHandling=NullValueHandling.Ignore)] public bool? HasManagedFrames { get; set; }
		[JsonProperty("suspended_count")] public int SuspendedCount { get; set; }
		[JsonProperty("states")] public string[] States { get; set; }=Array.Empty<string>();
	}
	public sealed class FrameInfo {
		[JsonProperty("frame_id")] public string FrameId { get; set; }="";
		[JsonProperty("thread_id")] public string ThreadId { get; set; }="";
		[JsonProperty("frame_index")] public int FrameIndex { get; set; }
		/// <summary>Formatted frame, eg. "Milestone1Target.Program.Tick(int)". Display only.</summary>
		[JsonProperty("name")] public string Name { get; set; }="";
		/// <summary>Module filename. Together with method_token and il_offset this is the durable
		/// identity: pass these three straight to set_il_breakpoint.</summary>
		[JsonProperty("module")] public string Module { get; set; }="";
		[JsonProperty("module_name")] public string ModuleName { get; set; }="";
		[JsonProperty("method_token")] public uint MethodToken { get; set; }
		[JsonProperty("il_offset")] public uint IlOffset { get; set; }
		/// <summary>Locals that have a raw scalar. Objects are omitted; ask for <c>include: ["locals"]</c>
		/// on <c>get_frame</c> to get everything, or <c>get_members</c> to expand one.</summary>
		[JsonProperty("locals")] public IReadOnlyList<PrimitiveValue> Locals { get; set; }=Array.Empty<PrimitiveValue>();
		/// <summary>Populated only when <c>get_frame</c> is called with <c>include</c>. Unlike
		/// <see cref="Locals"/> this holds every requested value, objects included.</summary>
		[JsonProperty("values", NullValueHandling=NullValueHandling.Ignore)] public EvaluatedValue[]? Values { get; set; }
	}
	public sealed class PrimitiveValue { [JsonProperty("name")] public string Name { get; set; }=""; [JsonProperty("type")] public string Type { get; set; }=""; [JsonProperty("value")] public object? Value { get; set; } }
	public sealed class BreakpointInfo {
		[JsonProperty("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonProperty("module")] public string Module { get; set; }=""; [JsonProperty("method_token")] public uint MethodToken { get; set; }
		[JsonProperty("il_offset")] public uint IlOffset { get; set; }
		/// <summary>The offset the caller asked for. Differs from <see cref="IlOffset"/> only when
		/// <see cref="Snapped"/> is true.</summary>
		[JsonProperty("requested_il_offset")] public uint RequestedIlOffset { get; set; }
		/// <summary>True when the breakpoint moved to a different offset because the engine refused the
		/// requested one. The breakpoint will stop somewhere other than where it was asked to.</summary>
		[JsonProperty("snapped")] public bool Snapped { get; set; }
		[JsonProperty("enabled")] public bool Enabled { get; set; }
		/// <summary>True only when the engine actually created the breakpoint. False with
		/// <c>severity: "error"</c> means it will never be hit — on Mono, usually because the offset is
		/// not a sequence point. False with no error means it is pending, eg. the module is not loaded.</summary>
		[JsonProperty("bound")] public bool Bound { get; set; }
		[JsonProperty("bound_count")] public int BoundCount { get; set; }
		/// <summary><c>none</c>, <c>warning</c> or <c>error</c>.</summary>
		[JsonProperty("severity")] public string Severity { get; set; }="none";
		[JsonProperty("message", NullValueHandling=NullValueHandling.Ignore)] public string? Message { get; set; }
		[JsonProperty("warning", NullValueHandling=NullValueHandling.Ignore)] public string? Warning { get; set; }
		[JsonProperty("session_id", NullValueHandling=NullValueHandling.Ignore)] public string? SessionId { get; set; }
		/// <summary>Event cursor taken immediately before the breakpoint existed. Pass this as
		/// <c>after_event_id</c> to <c>wait_for_stop</c>. A cursor read after setting a breakpoint on a
		/// hot method has already missed the first hit, and the wait then times out on a breakpoint that
		/// is working perfectly. Only <c>set_il_breakpoint</c> returns it: for a breakpoint that already
		/// existed there is no meaningful "just before", and emitting 0 would invite a caller to replay
		/// the whole event log.</summary>
		[JsonProperty("cursor_event_id", NullValueHandling=NullValueHandling.Ignore)] public long? CursorEventId { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
		/// <summary>Condition expression, evaluated in the target when the breakpoint is reached. Absent
		/// when the breakpoint is unconditional.</summary>
		[JsonProperty("condition", NullValueHandling=NullValueHandling.Ignore)] public string? Condition { get; set; }
		/// <summary><c>is_true</c> or <c>when_changed</c>. See <c>BreakpointConditionKinds</c>.</summary>
		[JsonProperty("condition_kind", NullValueHandling=NullValueHandling.Ignore)] public string? ConditionKind { get; set; }
		[JsonProperty("hit_count", NullValueHandling=NullValueHandling.Ignore)] public int? HitCount { get; set; }
		/// <summary><c>equals</c>, <c>multiple_of</c> or <c>at_least</c>. See <c>HitCountKinds</c>.</summary>
		[JsonProperty("hit_count_kind", NullValueHandling=NullValueHandling.Ignore)] public string? HitCountKind { get; set; }
		/// <summary>Trace message printed when the breakpoint is reached.</summary>
		[JsonProperty("trace_message", NullValueHandling=NullValueHandling.Ignore)] public string? TraceMessage { get; set; }
		/// <summary>True when a tracepoint prints and keeps running instead of stopping. A tracepoint that
		/// does not stop produces no <c>stopped</c> event, so <c>wait_for_stop</c> will never see it.</summary>
		[JsonProperty("trace_continue", NullValueHandling=NullValueHandling.Ignore)] public bool? TraceContinue { get; set; }
	}
	/// <summary>Result of a step request. The step itself completes asynchronously: the stop arrives on
	/// the event stream, exactly like a breakpoint hit.</summary>
	public sealed class StepResult {
		[JsonProperty("session_id")] public string SessionId { get; set; }="";
		[JsonProperty("thread_id")] public string ThreadId { get; set; }="";
		/// <summary><c>into</c>, <c>over</c> or <c>out</c>.</summary>
		[JsonProperty("step_kind")] public string StepKind { get; set; }="";
		/// <summary>Event cursor taken before the step was issued, for the same reason
		/// <c>set_il_breakpoint</c> returns one: a short step completes before a follow-up state read
		/// returns, and a cursor taken afterwards has already missed the stop.</summary>
		[JsonProperty("cursor_event_id")] public long CursorEventId { get; set; }
		/// <summary>True when the step completed before this call returned. False is not a failure — wait
		/// for the <c>stopped</c> event with <c>stop_reason: "step"</c> from <c>cursor_event_id</c>.</summary>
		[JsonProperty("completed")] public bool Completed { get; set; }
		/// <summary>The engine's own reason when the step failed, eg. stepping out of the outermost frame.</summary>
		[JsonProperty("error", NullValueHandling=NullValueHandling.Ignore)] public string? Error { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	/// <summary>One exception category's stop settings.</summary>
	public sealed class ExceptionBreakpointInfo {
		/// <summary>dnSpy's exception category, eg. <c>DotNet</c>.</summary>
		[JsonProperty("category")] public string Category { get; set; }="";
		/// <summary>Fully qualified exception type name, or absent for the category's default setting
		/// that governs every exception it does not name.</summary>
		[JsonProperty("name", NullValueHandling=NullValueHandling.Ignore)] public string? Name { get; set; }
		[JsonProperty("stop_first_chance")] public bool StopFirstChance { get; set; }
		[JsonProperty("stop_second_chance")] public bool StopSecondChance { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	/// <summary>One evaluated expression, member, or watch. Raw value and display text are separate
	/// fields on purpose: an agent that needs to compare or compute wants the scalar, one that needs to
	/// show something wants dnSpy's formatting, and collapsing them forces every caller to parse display
	/// text back into a value.</summary>
	public sealed class EvaluatedValue {
		/// <summary>Expression that produces this value again, including for a member reached by
		/// expansion. This is what makes depth the caller's to control: pass a member's expression back
		/// to get_members to go one level deeper.</summary>
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("name")] public string Name { get; set; }="";
		[JsonProperty("type")] public string Type { get; set; }="";
		/// <summary>dnSpy's formatted text, eg. <c>{Milestone1Target.Program}</c>. Display only.</summary>
		[JsonProperty("display")] public string Display { get; set; }="";
		/// <summary>The raw scalar when there is one. Absent for objects and for values the runtime
		/// cannot supply.</summary>
		[JsonProperty("value", NullValueHandling=NullValueHandling.Ignore)] public object? Value { get; set; }
		/// <summary>Distinguishes a value of <c>null</c> from no value at all. A null reference has a raw
		/// value of null and <c>has_raw_value: true</c>; an optimized-away or unavailable local has
		/// <c>has_raw_value: false</c> and usually an <c>error</c> saying which.</summary>
		[JsonProperty("has_raw_value")] public bool HasRawValue { get; set; }
		[JsonProperty("error", NullValueHandling=NullValueHandling.Ignore)] public string? Error { get; set; }
		[JsonProperty("read_only")] public bool ReadOnly { get; set; }
		/// <summary>True when reading this value ran target code, eg. a property getter.</summary>
		[JsonProperty("causes_side_effects")] public bool CausesSideEffects { get; set; }
		/// <summary>Null when dnSpy does not know without evaluating.</summary>
		[JsonProperty("has_children", NullValueHandling=NullValueHandling.Ignore)] public bool? HasChildren { get; set; }
	}
	/// <summary>One level of an object's members, paged. Expansion is never recursive: a cyclic object
	/// graph would be unbounded, and the caller cannot cancel a walk it did not ask for.</summary>
	public sealed class MemberList {
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("members")] public EvaluatedValue[] Members { get; set; }=Array.Empty<EvaluatedValue>();
		[JsonProperty("total")] public long Total { get; set; }
		[JsonProperty("offset")] public int Offset { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
		[JsonProperty("session_id", NullValueHandling=NullValueHandling.Ignore)] public string? SessionId { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	public sealed class AssignmentResult {
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("assigned")] public bool Assigned { get; set; }
		/// <summary>The value read back after a successful assignment.</summary>
		[JsonProperty("value", NullValueHandling=NullValueHandling.Ignore)] public EvaluatedValue? Value { get; set; }
		[JsonProperty("error", NullValueHandling=NullValueHandling.Ignore)] public string? Error { get; set; }
		/// <summary>True when the expression did not compile, which means no target code ran. False with
		/// an error means the target may already have been touched.</summary>
		[JsonProperty("compiler_error", NullValueHandling=NullValueHandling.Ignore)] public bool? CompilerError { get; set; }
		[JsonProperty("session_id", NullValueHandling=NullValueHandling.Ignore)] public string? SessionId { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	/// <summary>A stored expression, re-evaluated on demand. Deliberately not a retained value handle:
	/// a handle goes stale on the next resume, an expression does not.</summary>
	public sealed class WatchInfo {
		[JsonProperty("watch_id")] public int WatchId { get; set; }
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("value", NullValueHandling=NullValueHandling.Ignore)] public EvaluatedValue? Value { get; set; }
	}
	public sealed class WatchRemovalResult {
		[JsonProperty("watch_id")] public int WatchId { get; set; }
		[JsonProperty("removed")] public bool Removed { get; set; }
	}
	public sealed class ModuleInfo {
		[JsonProperty("name")] public string Name { get; set; }="";
		[JsonProperty("filename")] public string Filename { get; set; }="";
		[JsonProperty("process_id")] public int ProcessId { get; set; }
		[JsonProperty("runtime_guid")] public string RuntimeGuid { get; set; }="";
		[JsonProperty("is_dynamic")] public bool IsDynamic { get; set; }
		[JsonProperty("is_in_memory")] public bool IsInMemory { get; set; }
		[JsonProperty("is_optimized", NullValueHandling=NullValueHandling.Ignore)] public bool? IsOptimized { get; set; }
		[JsonProperty("order")] public int Order { get; set; }
		[JsonProperty("address")] public ulong Address { get; set; }
		[JsonProperty("size")] public uint Size { get; set; }
		[JsonProperty("version", NullValueHandling=NullValueHandling.Ignore)] public string? Version { get; set; }
		/// <summary>False for a module <c>set_il_breakpoint</c> cannot address, because it takes a module
		/// path and this module has none. Reported per module rather than left to be discovered from a
		/// breakpoint that never binds. Phase 6 owns in-memory module identity.</summary>
		[JsonProperty("can_set_breakpoint")] public bool CanSetBreakpoint { get; set; }
	}
	/// <summary>Bounded listing of exception stop settings. Bounded on purpose: dnSpy stops on second
	/// chance for essentially every .NET exception it knows, so an unfiltered listing is thousands of
	/// entries that are identical on every machine.</summary>
	public sealed class ExceptionBreakpointList {
		[JsonProperty("entries")] public ExceptionBreakpointInfo[] Entries { get; set; }=Array.Empty<ExceptionBreakpointInfo>();
		/// <summary>How many matched before <c>max_results</c> was applied.</summary>
		[JsonProperty("total")] public int Total { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
		/// <summary>False by default. When false the listing is the set someone deliberately configured to
		/// break on throw, rather than dnSpy's stock second-chance defaults.</summary>
		[JsonProperty("included_second_chance")] public bool IncludedSecondChance { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	public sealed class ClearBreakpointsResult {
		[JsonProperty("removed")] public int Removed { get; set; } [JsonProperty("state_version")] public long StateVersion { get; set; }
	}
	public sealed class RemoveBreakpointResult {
		[JsonProperty("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonProperty("removed")] public bool Removed { get; set; }
		[JsonProperty("state_version")] public long StateVersion { get; set; }
	}
}
