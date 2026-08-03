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
		[JsonProperty("locals")] public IReadOnlyList<PrimitiveValue> Locals { get; set; }=Array.Empty<PrimitiveValue>();
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
