using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace dgSpy.Protocol {
	public static class ProtocolVersion { public const int Current = 2; }
	public sealed class RpcRequest {
		[JsonPropertyName("version")] public int Version { get; set; } = ProtocolVersion.Current;
		[JsonPropertyName("request_id")] public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
		[JsonPropertyName("host_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? HostId { get; set; }
		[JsonPropertyName("authentication_token"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? AuthenticationToken { get; set; }
		[JsonPropertyName("operation")] public string Operation { get; set; } = "";
		[JsonPropertyName("deadline_utc")] public DateTime? DeadlineUtc { get; set; }
		[JsonPropertyName("arguments")] public JsonObject Arguments { get; set; } = new JsonObject();
	}
	public sealed class RpcResponse {
		[JsonPropertyName("version")] public int Version { get; set; } = ProtocolVersion.Current;
		[JsonPropertyName("request_id")] public string RequestId { get; set; } = "";
		[JsonPropertyName("result"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public object? Result { get; set; }
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public RpcError? Error { get; set; }
		public static RpcResponse Success(string id, object value) => new RpcResponse { RequestId=id, Result=value };
		public static RpcResponse Failure(string id, string code, string message) => new RpcResponse { RequestId=id, Error=new RpcError { Code=code, Message=message } };
	}
	public sealed class RpcError { [JsonPropertyName("code")] public string Code { get; set; }="internal_error"; [JsonPropertyName("message")] public string Message { get; set; }=""; }
	public sealed class Handshake {
		[JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }=Protocol.ProtocolVersion.Current;
		[JsonPropertyName("extension_version")] public string ExtensionVersion { get; set; }="0.1.0";
		[JsonPropertyName("host_id")] public string HostId { get; set; }="";
	}
	public sealed class HostRegistration {
		[JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }=Protocol.ProtocolVersion.Current;
		[JsonPropertyName("host_id")] public string HostId { get; set; }="";
	}
	public sealed class ProgramInfo {
		[JsonPropertyName("program_id")] public string ProgramId { get; set; }=""; [JsonPropertyName("pid")] public int ProcessId { get; set; }
		[JsonPropertyName("executable")] public string Executable { get; set; }=""; [JsonPropertyName("title")] public string Title { get; set; }="";
		[JsonPropertyName("command_line")] public string CommandLine { get; set; }="";
		[JsonPropertyName("architecture")] public string Architecture { get; set; }="";
		/// <summary>Engine discriminator, eg. "CLR v4.0.30319".</summary>
		[JsonPropertyName("runtime_name")] public string RuntimeName { get; set; }="";
		/// <summary>Distinguishes .NET Framework from Unity/Mono. Both share one runtime *kind* GUID,
		/// so only this tells the engines apart.</summary>
		[JsonPropertyName("runtime_guid")] public string RuntimeGuid { get; set; }="";
		[JsonPropertyName("runtime_kind_guid")] public string RuntimeKindGuid { get; set; }="";
		/// <summary>dnSpy attach-provider names that can produce this entry, ready to pass back as
		/// <c>provider_names</c>. Empty when the runtime has no known provider — an entry reached through
		/// attach_endpoint has none, because no provider ever enumerated it.</summary>
		[JsonPropertyName("attach_providers")] public string[] AttachProviders { get; set; }=Array.Empty<string>();
	}
	public sealed class SessionSummary {
		[JsonPropertyName("session_id")] public string SessionId { get; set; }=""; [JsonPropertyName("state")] public string State { get; set; }="";
		[JsonPropertyName("program_id")] public string ProgramId { get; set; }=""; [JsonPropertyName("state_version")] public long StateVersion { get; set; }
		[JsonPropertyName("last_event_id")] public long LastEventId { get; set; } [JsonPropertyName("process_ids")] public int[] ProcessIds { get; set; }=Array.Empty<int>();
		[JsonPropertyName("lifecycle_version")] public long LifecycleVersion { get; set; } [JsonPropertyName("execution_version")] public long ExecutionVersion { get; set; }
		[JsonPropertyName("breakpoints_version")] public long BreakpointsVersion { get; set; } [JsonPropertyName("stop_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? StopId { get; set; }
		[JsonPropertyName("can_detach_without_terminating")] public bool CanDetachWithoutTerminating { get; set; }
	}
	public sealed class DetachResult {
		[JsonPropertyName("session_id")] public string SessionId { get; set; }=""; [JsonPropertyName("detached")] public bool Detached { get; set; }
		[JsonPropertyName("terminated")] public bool Terminated { get; set; } [JsonPropertyName("state_version")] public long StateVersion { get; set; }
		[JsonPropertyName("process_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ProcessId { get; set; }
		[JsonPropertyName("session_active")] public bool SessionActive { get; set; }
		[JsonPropertyName("lifecycle_version")] public long LifecycleVersion { get; set; }
	}
	public sealed class SessionState {
		[JsonPropertyName("session_id")] public string SessionId { get; set; }=""; [JsonPropertyName("state")] public string State { get; set; }="running";
		[JsonPropertyName("state_version")] public long StateVersion { get; set; } [JsonPropertyName("last_event_id")] public long LastEventId { get; set; }
		[JsonPropertyName("process_ids")] public int[] ProcessIds { get; set; }=Array.Empty<int>();
		[JsonPropertyName("lifecycle_version")] public long LifecycleVersion { get; set; }
		[JsonPropertyName("execution_version")] public long ExecutionVersion { get; set; }
		[JsonPropertyName("breakpoints_version")] public long BreakpointsVersion { get; set; }
		[JsonPropertyName("stop_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? StopId { get; set; }
		/// <summary>Why the session is <c>faulted</c>. dnSpy's own connect-failure text when it produced
		/// one, otherwise a deadline description. Absent for every other state.</summary>
		[JsonPropertyName("fault_message"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? FaultMessage { get; set; }
		[JsonPropertyName("exit_code"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ExitCode { get; set; }
		[JsonPropertyName("terminal_reason"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? TerminalReason { get; set; }
	}
	public sealed class DebugEvent {
		[JsonPropertyName("event_id")] public long EventId { get; set; }
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
		[JsonPropertyName("lifecycle_version")] public long LifecycleVersion { get; set; }
		[JsonPropertyName("execution_version")] public long ExecutionVersion { get; set; }
		[JsonPropertyName("breakpoints_version")] public long BreakpointsVersion { get; set; }
		[JsonPropertyName("stop_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? StopId { get; set; }
		[JsonPropertyName("timestamp_utc")] public DateTime TimestampUtc { get; set; }=DateTime.UtcNow;
		[JsonPropertyName("terminal")] public bool Terminal { get; set; }
		[JsonPropertyName("process_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ProcessId { get; set; }
		[JsonPropertyName("runtime_guid"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeGuid { get; set; }
		[JsonPropertyName("runtime_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeName { get; set; }
		[JsonPropertyName("thread_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ThreadId { get; set; }
		[JsonPropertyName("breakpoint_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? BreakpointId { get; set; }
		[JsonPropertyName("module"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Module { get; set; }
		[JsonPropertyName("method_token"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public uint? MethodToken { get; set; }
		[JsonPropertyName("il_offset"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public uint? IlOffset { get; set; }
		[JsonPropertyName("stop_reason"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? StopReason { get; set; }
		[JsonPropertyName("exception_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ExceptionId { get; set; }
		[JsonPropertyName("exception_message"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ExceptionMessage { get; set; }
		[JsonPropertyName("exception_first_chance"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? ExceptionFirstChance { get; set; }
		[JsonPropertyName("exception_unhandled"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? ExceptionUnhandled { get; set; }
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
		[JsonPropertyName("exit_code"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ExitCode { get; set; }
		[JsonPropertyName("reason"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; set; }
	}
	public class EventResult {
		[JsonPropertyName("events")] public DebugEvent[] Events { get; set; }=Array.Empty<DebugEvent>();
		[JsonPropertyName("oldest_event_id")] public long OldestEventId { get; set; }
		[JsonPropertyName("oldest_available_cursor")] public long OldestAvailableCursor { get; set; }
		[JsonPropertyName("last_event_id")] public long LastEventId { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
	}
	public sealed class WaitResult : EventResult { [JsonPropertyName("timed_out")] public bool TimedOut { get; set; } }
	public sealed class ThreadInfo {
		[JsonPropertyName("thread_id")] public string ThreadId { get; set; }="";
		[JsonPropertyName("process_id")] public int ProcessId { get; set; }
		[JsonPropertyName("os_thread_id")] public ulong OsThreadId { get; set; }
		[JsonPropertyName("managed_thread_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? ManagedThreadId { get; set; }
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("is_main")] public bool IsMain { get; set; }
		[JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
		/// <summary>Present only when frame availability is already known. list_threads deliberately does
		/// not probe every thread: a Unity thread can exit during GET_FRAME_INFO and older Mono runtimes
		/// can omit the reply. Select a thread with get_callstack to discover its frames safely.</summary>
		[JsonPropertyName("has_managed_frames"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? HasManagedFrames { get; set; }
		[JsonPropertyName("suspended_count")] public int SuspendedCount { get; set; }
		[JsonPropertyName("states")] public string[] States { get; set; }=Array.Empty<string>();
		/// <summary>Whether a func-eval issued against this thread can run right now. Present only when the
		/// caller asked list_threads to probe for it; absent means not measured, never "no". `states` cannot
		/// substitute for this — CorDebug publishes UnsafePoint there and Mono publishes nothing at all.</summary>
		[JsonPropertyName("can_evaluate"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? CanEvaluate { get; set; }
		/// <summary>Why <see cref="CanEvaluate"/> is false: <c>no_frames</c> (nothing to evaluate against),
		/// <c>native_frame</c> (the innermost frame is native, so there is no managed context to call from),
		/// or <c>unsafe_point</c> (frames are fine but the engine parked the thread where a func-eval cannot
		/// start). Reported in the order the engine itself checks them.</summary>
		[JsonPropertyName("evaluate_blocked_reason"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? EvaluateBlockedReason { get; set; }
	}
	public sealed class FrameInfo {
		[JsonPropertyName("frame_id")] public string FrameId { get; set; }="";
		[JsonPropertyName("thread_id")] public string ThreadId { get; set; }="";
		[JsonPropertyName("frame_index")] public int FrameIndex { get; set; }
		/// <summary>Formatted frame, eg. "Milestone1Target.Program.Tick(int)". Display only.</summary>
		[JsonPropertyName("name")] public string Name { get; set; }="";
		/// <summary>Module filename. Together with method_token and il_offset this is the durable
		/// identity: pass these three straight to set_il_breakpoint.</summary>
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("module_name")] public string ModuleName { get; set; }="";
		[JsonPropertyName("method_token")] public uint MethodToken { get; set; }
		[JsonPropertyName("il_offset")] public uint IlOffset { get; set; }
		/// <summary>Locals that have a raw scalar. Objects are omitted; ask for <c>include: ["locals"]</c>
		/// on <c>get_frame</c> to get everything, or <c>get_members</c> to expand one.</summary>
		[JsonPropertyName("locals")] public IReadOnlyList<PrimitiveValue> Locals { get; set; }=Array.Empty<PrimitiveValue>();
		/// <summary>Populated only when <c>get_frame</c> is called with <c>include</c>. Unlike
		/// <see cref="Locals"/> this holds every requested value, objects included.</summary>
		[JsonPropertyName("values"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public EvaluatedValue[]? Values { get; set; }
	}
	public sealed class PrimitiveValue { [JsonPropertyName("name")] public string Name { get; set; }=""; [JsonPropertyName("type")] public string Type { get; set; }=""; [JsonPropertyName("value")] public object? Value { get; set; } }
	public sealed class BreakpointInfo {
		[JsonPropertyName("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonPropertyName("module")] public string Module { get; set; }=""; [JsonPropertyName("method_token")] public uint MethodToken { get; set; }
		[JsonPropertyName("il_offset")] public uint IlOffset { get; set; }
		/// <summary>The offset the caller asked for. Differs from <see cref="IlOffset"/> only when
		/// <see cref="Snapped"/> is true.</summary>
		[JsonPropertyName("requested_il_offset")] public uint RequestedIlOffset { get; set; }
		/// <summary>True when the breakpoint moved to a different offset because the engine refused the
		/// requested one. The breakpoint will stop somewhere other than where it was asked to.</summary>
		[JsonPropertyName("snapped")] public bool Snapped { get; set; }
		[JsonPropertyName("enabled")] public bool Enabled { get; set; }
		/// <summary>True only when the engine actually created the breakpoint. False with
		/// <c>severity: "error"</c> means it will never be hit — on Mono, usually because the offset is
		/// not a sequence point. False with no error means it is pending, eg. the module is not loaded.</summary>
		[JsonPropertyName("bound")] public bool Bound { get; set; }
		[JsonPropertyName("bound_count")] public int BoundCount { get; set; }
		/// <summary><c>none</c>, <c>warning</c> or <c>error</c>.</summary>
		[JsonPropertyName("severity")] public string Severity { get; set; }="none";
		[JsonPropertyName("message"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Message { get; set; }
		[JsonPropertyName("warning"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Warning { get; set; }
		[JsonPropertyName("session_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; set; }
		/// <summary>Event cursor taken immediately before the breakpoint existed. Pass this as
		/// <c>after_event_id</c> to <c>wait_for_stop</c>. A cursor read after setting a breakpoint on a
		/// hot method has already missed the first hit, and the wait then times out on a breakpoint that
		/// is working perfectly. Only <c>set_il_breakpoint</c> returns it: for a breakpoint that already
		/// existed there is no meaningful "just before", and emitting 0 would invite a caller to replay
		/// the whole event log.</summary>
		[JsonPropertyName("cursor_event_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public long? CursorEventId { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
		/// <summary>Condition expression, evaluated in the target when the breakpoint is reached. Absent
		/// when the breakpoint is unconditional.</summary>
		[JsonPropertyName("condition"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Condition { get; set; }
		/// <summary><c>is_true</c> or <c>when_changed</c>. See <c>BreakpointConditionKinds</c>.</summary>
		[JsonPropertyName("condition_kind"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ConditionKind { get; set; }
		[JsonPropertyName("hit_count"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? HitCount { get; set; }
		/// <summary><c>equals</c>, <c>multiple_of</c> or <c>at_least</c>. See <c>HitCountKinds</c>.</summary>
		[JsonPropertyName("hit_count_kind"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? HitCountKind { get; set; }
		/// <summary>Trace message printed when the breakpoint is reached.</summary>
		[JsonPropertyName("trace_message"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? TraceMessage { get; set; }
		/// <summary>True when a tracepoint prints and keeps running instead of stopping. A tracepoint that
		/// does not stop produces no <c>stopped</c> event, so <c>wait_for_stop</c> will never see it.</summary>
		[JsonPropertyName("trace_continue"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? TraceContinue { get; set; }
	}
	/// <summary>Result of a step request. The step itself completes asynchronously: the stop arrives on
	/// the event stream, exactly like a breakpoint hit.</summary>
	public sealed class StepResult {
		[JsonPropertyName("session_id")] public string SessionId { get; set; }="";
		[JsonPropertyName("thread_id")] public string ThreadId { get; set; }="";
		/// <summary><c>into</c>, <c>over</c> or <c>out</c>.</summary>
		[JsonPropertyName("step_kind")] public string StepKind { get; set; }="";
		/// <summary>Event cursor taken before the step was issued, for the same reason
		/// <c>set_il_breakpoint</c> returns one: a short step completes before a follow-up state read
		/// returns, and a cursor taken afterwards has already missed the stop.</summary>
		[JsonPropertyName("cursor_event_id")] public long CursorEventId { get; set; }
		/// <summary>True when the step completed before this call returned. False is not a failure — wait
		/// for the <c>stopped</c> event with <c>stop_reason: "step"</c> from <c>cursor_event_id</c>.</summary>
		[JsonPropertyName("completed")] public bool Completed { get; set; }
		/// <summary>The engine's own reason when the step failed, eg. stepping out of the outermost frame.</summary>
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	/// <summary>One exception category's stop settings.</summary>
	public sealed class ExceptionBreakpointInfo {
		/// <summary>dnSpy's exception category, eg. <c>DotNet</c>.</summary>
		[JsonPropertyName("category")] public string Category { get; set; }="";
		/// <summary>Fully qualified exception type name, or absent for the category's default setting
		/// that governs every exception it does not name.</summary>
		[JsonPropertyName("name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
		[JsonPropertyName("stop_first_chance")] public bool StopFirstChance { get; set; }
		[JsonPropertyName("stop_second_chance")] public bool StopSecondChance { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	/// <summary>One evaluated expression, member, or watch. Raw value and display text are separate
	/// fields on purpose: an agent that needs to compare or compute wants the scalar, one that needs to
	/// show something wants dnSpy's formatting, and collapsing them forces every caller to parse display
	/// text back into a value.</summary>
	public sealed class EvaluatedValue {
		/// <summary>Expression that produces this value again, including for a member reached by
		/// expansion. This is what makes depth the caller's to control: pass a member's expression back
		/// to get_members to go one level deeper.
		/// <para>Empty means this row cannot be addressed by any expression, so it is a stop rather than a
		/// retry. Two causes. Compiler-generated members - auto-property backing fields, async and iterator
		/// state machine fields, lambda display-class fields - have metadata names
		/// (<c>&lt;Foo&gt;k__BackingField</c>) containing characters no identifier may contain in either
		/// language. Grouping rows such as <c>Static members</c> carry a type name, and a bare type is not
		/// an expression; its members remain reachable by naming them directly
		/// (<c>Some.Type.SomeStaticField</c>). In both cases the row's name, type and value are still
		/// accurate; only drilling deeper from that row is impossible.</para></summary>
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("type")] public string Type { get; set; }="";
		/// <summary>dnSpy's formatted text, eg. <c>{Milestone1Target.Program}</c>. Display only.</summary>
		[JsonPropertyName("display")] public string Display { get; set; }="";
		/// <summary>The raw scalar when there is one. Absent for objects and for values the runtime
		/// cannot supply.</summary>
		[JsonPropertyName("value"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public object? Value { get; set; }
		/// <summary>Distinguishes a value of <c>null</c> from no value at all. A null reference has a raw
		/// value of null and <c>has_raw_value: true</c>; an optimized-away or unavailable local has
		/// <c>has_raw_value: false</c> and usually an <c>error</c> saying which.</summary>
		[JsonPropertyName("has_raw_value")] public bool HasRawValue { get; set; }
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
		[JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
		/// <summary>True when reading this value ran target code, eg. a property getter.</summary>
		[JsonPropertyName("causes_side_effects")] public bool CausesSideEffects { get; set; }
		/// <summary>Null when dnSpy does not know without evaluating.</summary>
		[JsonPropertyName("has_children"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? HasChildren { get; set; }
	}
	/// <summary>One level of an object's members, paged. Expansion is never recursive: a cyclic object
	/// graph would be unbounded, and the caller cannot cancel a walk it did not ask for.</summary>
	public sealed class MemberList {
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("members")] public EvaluatedValue[] Members { get; set; }=Array.Empty<EvaluatedValue>();
		[JsonPropertyName("total")] public long Total { get; set; }
		[JsonPropertyName("offset")] public int Offset { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[JsonPropertyName("session_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	public sealed class AssignmentResult {
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("assigned")] public bool Assigned { get; set; }
		/// <summary>The value read back after a successful assignment.</summary>
		[JsonPropertyName("value"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public EvaluatedValue? Value { get; set; }
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
		/// <summary>True when the expression did not compile, which means no target code ran. False with
		/// an error means the target may already have been touched.</summary>
		[JsonPropertyName("compiler_error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? CompilerError { get; set; }
		/// <summary>What to do about <see cref="Error"/>, when dgSpy recognizes it. Present only for errors
		/// whose stock dnSpy text names the wrong remedy.</summary>
		[JsonPropertyName("recovery"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Recovery { get; set; }
		[JsonPropertyName("session_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	public sealed class MemoryResult {
		[JsonPropertyName("address")] public ulong Address { get; set; }
		[JsonPropertyName("length")] public int Length { get; set; }
		[JsonPropertyName("data_base64"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DataBase64 { get; set; }
		[JsonPropertyName("written")] public bool Written { get; set; }
		[JsonPropertyName("causes_side_effects")] public bool CausesSideEffects { get; set; }
		[JsonPropertyName("capability")] public string Capability { get; set; }="memory_access";
	}
	public sealed class MutationResult {
		[JsonPropertyName("completed")] public bool Completed { get; set; }
		[JsonPropertyName("causes_side_effects")] public bool CausesSideEffects { get; set; }=true;
		[JsonPropertyName("audit_id")] public string AuditId { get; set; }="";
		[JsonPropertyName("value"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public EvaluatedValue? Value { get; set; }
		[JsonPropertyName("error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Error { get; set; }
		/// <summary>True when the expression did not compile, so nothing ran in the target and the fix is
		/// local to the expression. False with an error means it compiled and the engine refused to execute
		/// it — the same call can succeed elsewhere. Mirrors set_value's field of the same name; without it
		/// a caller mistake and a transient engine refusal are indistinguishable.</summary>
		[JsonPropertyName("compiler_error"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? CompilerError { get; set; }
		/// <summary>What to do about <see cref="Error"/>, when dgSpy recognizes it.</summary>
		[JsonPropertyName("recovery"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Recovery { get; set; }
		[JsonPropertyName("capability")] public string Capability { get; set; }="";
	}
	/// <summary>A stored expression, re-evaluated on demand. Deliberately not a retained value handle:
	/// a handle goes stale on the next resume, an expression does not.</summary>
	public sealed class WatchInfo {
		[JsonPropertyName("watch_id")] public int WatchId { get; set; }
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("value"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public EvaluatedValue? Value { get; set; }
	}
	public sealed class WatchRemovalResult {
		[JsonPropertyName("watch_id")] public int WatchId { get; set; }
		[JsonPropertyName("removed")] public bool Removed { get; set; }
	}
	public sealed class ModuleInfo {
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("filename")] public string Filename { get; set; }="";
		[JsonPropertyName("process_id")] public int ProcessId { get; set; }
		[JsonPropertyName("runtime_guid")] public string RuntimeGuid { get; set; }="";
		[JsonPropertyName("is_dynamic")] public bool IsDynamic { get; set; }
		[JsonPropertyName("is_in_memory")] public bool IsInMemory { get; set; }
		[JsonPropertyName("is_optimized"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? IsOptimized { get; set; }
		[JsonPropertyName("order")] public int Order { get; set; }
		[JsonPropertyName("address")] public ulong Address { get; set; }
		[JsonPropertyName("size")] public uint Size { get; set; }
		[JsonPropertyName("version"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Version { get; set; }
		/// <summary>False for a module <c>set_il_breakpoint</c> cannot address, because it takes a module
		/// path and this module has none. Reported per module rather than left to be discovered from a
		/// breakpoint that never binds. Phase 6 owns in-memory module identity.</summary>
		[JsonPropertyName("can_set_breakpoint")] public bool CanSetBreakpoint { get; set; }
	}
	/// <summary>A type or member, identified the way a breakpoint takes it. Display text is never the
	/// identity: module plus token is, so a caller never has to parse a name back into one.</summary>
	public sealed class SymbolInfo {
		/// <summary><c>type</c>, <c>method</c>, <c>field</c>, <c>property</c> or <c>event</c>.</summary>
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("module")] public string Module { get; set; }="";
		/// <summary>Metadata token. For a method this is exactly what <c>set_il_breakpoint</c> takes.</summary>
		[JsonPropertyName("method_token")] public uint MethodToken { get; set; }
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("full_name")] public string FullName { get; set; }="";
		[JsonPropertyName("declaring_type"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DeclaringType { get; set; }
		[JsonPropertyName("namespace"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Namespace { get; set; }
	}
	public sealed class SymbolList {
		[JsonPropertyName("symbols")] public SymbolInfo[] Symbols { get; set; }=Array.Empty<SymbolInfo>();
		[JsonPropertyName("total")] public int Total { get; set; }
		[JsonPropertyName("offset")] public int Offset { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
	}
	public sealed class DocumentInfo {
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("filename")] public string Filename { get; set; }="";
		[JsonPropertyName("process_id")] public int ProcessId { get; set; }
		[JsonPropertyName("is_dynamic")] public bool IsDynamic { get; set; }
		[JsonPropertyName("is_in_memory")] public bool IsInMemory { get; set; }
		/// <summary>False for a module whose metadata dnSpy cannot load. Reported rather than the module
		/// being omitted, because a silently missing module makes a type that exists look like it does not.</summary>
		[JsonPropertyName("has_metadata")] public bool HasMetadata { get; set; }
		[JsonPropertyName("assembly_full_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? AssemblyFullName { get; set; }
		[JsonPropertyName("type_count"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? TypeCount { get; set; }
	}
	public sealed class IlInstruction {
		[JsonPropertyName("offset")] public uint Offset { get; set; }
		[JsonPropertyName("opcode")] public string OpCode { get; set; }="";
		[JsonPropertyName("operand"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Operand { get; set; }
		/// <summary>True when a breakpoint may sit here on Mono. Mono rejects every other offset with
		/// <c>NO_SEQ_POINT_AT_IL_OFFSET</c>, which dnSpy turns into a silently unbound breakpoint;
		/// CorDebug accepts any offset.</summary>
		[JsonPropertyName("is_sequence_point")] public bool IsSequencePoint { get; set; }
		[JsonPropertyName("line"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? Line { get; set; }
	}
	public sealed class MethodBodyInfo {
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("method_token")] public uint MethodToken { get; set; }
		[JsonPropertyName("full_name")] public string FullName { get; set; }="";
		[JsonPropertyName("declaring_type"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DeclaringType { get; set; }
		[JsonPropertyName("max_stack")] public ushort MaxStack { get; set; }
		[JsonPropertyName("code_size")] public uint CodeSize { get; set; }
		[JsonPropertyName("local_count")] public int LocalCount { get; set; }
		[JsonPropertyName("exception_handler_count")] public int ExceptionHandlerCount { get; set; }
		/// <summary>False means no PDB was available — <em>not</em> that there are no legal breakpoint
		/// offsets. Those are very different answers for a Mono caller.</summary>
		[JsonPropertyName("has_sequence_points")] public bool HasSequencePoints { get; set; }
		[JsonPropertyName("instructions")] public IlInstruction[] Instructions { get; set; }=Array.Empty<IlInstruction>();
	}
	public sealed class DecompiledCode {
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("language")] public string Language { get; set; }="";
		[JsonPropertyName("code")] public string Code { get; set; }="";
	}
	public sealed class TextSearchHit {
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("type")] public string Type { get; set; }="";
		[JsonPropertyName("method_token")] public uint MethodToken { get; set; }
		[JsonPropertyName("method")] public string Method { get; set; }="";
		[JsonPropertyName("line")] public int Line { get; set; }
		[JsonPropertyName("text")] public string Text { get; set; }="";
	}
	public sealed class TextSearchResult {
		[JsonPropertyName("hits")] public TextSearchHit[] Hits { get; set; }=Array.Empty<TextSearchHit>();
		[JsonPropertyName("total")] public int Total { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[JsonPropertyName("scanned_methods")] public int ScannedMethods { get; set; }
		[JsonPropertyName("scan_truncated")] public bool ScanTruncated { get; set; }
	}
	/// <summary>One <c>search</c> hit. Every identifier here is round-trippable by design: <c>full_name</c>
	/// is one of the strings the matcher itself tests, so passing it back as <c>pattern</c> re-finds this
	/// symbol; <c>declaring_type</c> is what <c>list_members</c> and <c>get_csharp</c> take for
	/// <c>type</c>; <c>token</c> plus <c>module</c> is what <c>get_il</c>, <c>find_references</c> and
	/// <c>set_il_breakpoint</c> take. Nothing here needs parsing.</summary>
	public sealed class SearchHit {
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("module_path"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ModulePath { get; set; }
		/// <summary>False means the module is open in dnSpy's Assembly Explorer but is not loaded in the
		/// debug session, so the session-scoped tools will answer <c>module_not_found</c> for it.</summary>
		[JsonPropertyName("in_session")] public bool InSession { get; set; }
		/// <summary>Metadata token, 0 for a hit with no token of its own (a namespace, for example).</summary>
		[JsonPropertyName("token")] public uint Token { get; set; }
		[JsonPropertyName("name")] public string Name { get; set; }="";
		[JsonPropertyName("full_name")] public string FullName { get; set; }="";
		[JsonPropertyName("declaring_type"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? DeclaringType { get; set; }
		[JsonPropertyName("namespace"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Namespace { get; set; }
		/// <summary>The GUI Search window's Location column: the declaring type, or the namespace.</summary>
		[JsonPropertyName("location"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Location { get; set; }
		/// <summary>Why this matched, when the name is not the answer: the IL instruction for a literal,
		/// or the parameter or local whose name matched.</summary>
		[JsonPropertyName("match_context"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? MatchContext { get; set; }
	}
	public sealed class SearchResults {
		[JsonPropertyName("hits")] public SearchHit[] Hits { get; set; }=Array.Empty<SearchHit>();
		[JsonPropertyName("total")] public int Total { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		/// <summary>Symbol slots inspected by this call. Work, not results.</summary>
		[JsonPropertyName("scanned")] public int Scanned { get; set; }
		/// <summary>True when <c>max_scan</c> stopped the walk before the scope was exhausted. Resume by
		/// passing <c>next_scan_offset</c> back as <c>scan_offset</c>.</summary>
		[JsonPropertyName("scan_truncated")] public bool ScanTruncated { get; set; }
		[JsonPropertyName("next_scan_offset")] public int NextScanOffset { get; set; }
		/// <summary>Modules the walk covered, in the order it covered them. Traversal order is stable, which
		/// is what makes <c>next_scan_offset</c> mean the same thing on the next call.</summary>
		[JsonPropertyName("modules_searched")] public string[] ModulesSearched { get; set; }=Array.Empty<string>();
	}
	public sealed class MetadataInfo {
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("assembly_full_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? AssemblyFullName { get; set; }
		[JsonPropertyName("mvid")] public string Mvid { get; set; }="";
		[JsonPropertyName("runtime_version")] public string RuntimeVersion { get; set; }="";
		[JsonPropertyName("type_count")] public int TypeCount { get; set; }
		[JsonPropertyName("method_count")] public int MethodCount { get; set; }
		[JsonPropertyName("field_count")] public int FieldCount { get; set; }
		[JsonPropertyName("assembly_reference_count")] public int AssemblyReferenceCount { get; set; }
		[JsonPropertyName("table_row_counts")] public Dictionary<string,int> TableRowCounts { get; set; }=new Dictionary<string,int>();
		[JsonPropertyName("token"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public uint? Token { get; set; }
		[JsonPropertyName("token_kind"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? TokenKind { get; set; }
		[JsonPropertyName("token_full_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? TokenFullName { get; set; }
	}
	public sealed class RawModuleChunk {
		[JsonPropertyName("module")] public string Module { get; set; }="";
		[JsonPropertyName("offset")] public int Offset { get; set; }
		[JsonPropertyName("count")] public int Count { get; set; }
		[JsonPropertyName("total_size")] public int TotalSize { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[JsonPropertyName("sha256")] public string Sha256 { get; set; }="";
		[JsonPropertyName("data_base64")] public string DataBase64 { get; set; }="";
	}
	/// <summary>Bounded listing of exception stop settings. Bounded on purpose: dnSpy stops on second
	/// chance for essentially every .NET exception it knows, so an unfiltered listing is thousands of
	/// entries that are identical on every machine.</summary>
	public sealed class ExceptionBreakpointList {
		[JsonPropertyName("entries")] public ExceptionBreakpointInfo[] Entries { get; set; }=Array.Empty<ExceptionBreakpointInfo>();
		/// <summary>How many matched before <c>max_results</c> was applied.</summary>
		[JsonPropertyName("total")] public int Total { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		/// <summary>False by default. When false the listing is the set someone deliberately configured to
		/// break on throw, rather than dnSpy's stock second-chance defaults.</summary>
		[JsonPropertyName("included_second_chance")] public bool IncludedSecondChance { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	public sealed class ClearBreakpointsResult {
		[JsonPropertyName("removed")] public int Removed { get; set; } [JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
	public sealed class RemoveBreakpointResult {
		[JsonPropertyName("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonPropertyName("removed")] public bool Removed { get; set; }
		[JsonPropertyName("state_version")] public long StateVersion { get; set; }
	}
}
