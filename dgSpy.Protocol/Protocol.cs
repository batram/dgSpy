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
		[JsonProperty("attach_provider")] public string AttachProvider { get; set; }="";
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
	}
	public sealed class DebugEvent { [JsonProperty("event_id")] public long EventId { get; set; } [JsonProperty("kind")] public string Kind { get; set; }=""; [JsonProperty("state_version")] public long StateVersion { get; set; } [JsonProperty("timestamp_utc")] public DateTime TimestampUtc { get; set; }=DateTime.UtcNow; }
	public sealed class WaitResult { [JsonProperty("events")] public DebugEvent[] Events { get; set; }=Array.Empty<DebugEvent>(); [JsonProperty("timed_out")] public bool TimedOut { get; set; } [JsonProperty("oldest_event_id")] public long OldestEventId { get; set; } }
	public sealed class FrameInfo {
		[JsonProperty("frame_id")] public string FrameId { get; set; }="";
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
}
