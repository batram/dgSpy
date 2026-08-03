using System;
using System.Linq;
using Newtonsoft.Json;

namespace dgSpy.Protocol {
	/// <summary>One supported debugger engine and the behavior that is genuinely specific to it.
	/// Advertised rather than assumed: CorDebug and Mono/Unity differ in ways a caller cannot guess,
	/// most sharply in what counts as a legal breakpoint location.</summary>
	public sealed class EngineCapabilities {
		[JsonProperty("engine")] public string Engine { get; set; }="";
		[JsonProperty("display_name")] public string DisplayName { get; set; }="";
		/// <summary>How a session on this engine is acquired, eg. "list_programs+attach".</summary>
		[JsonProperty("acquisition")] public string[] Acquisition { get; set; }=Array.Empty<string>();
		[JsonProperty("discoverable")] public bool Discoverable { get; set; }
		[JsonProperty("arbitrary_il_offset_breakpoints")] public bool ArbitraryIlOffsetBreakpoints { get; set; }
		[JsonProperty("sequence_point_breakpoints_only")] public bool SequencePointBreakpointsOnly { get; set; }
		[JsonProperty("detach_without_terminating")] public bool DetachWithoutTerminating { get; set; }
		[JsonProperty("notes")] public string Notes { get; set; }="";
	}
	/// <summary>The extension's own upper bound for one operation. The gateway derives its deadline from
	/// this instead of guessing: a gateway deadline shorter than the inner bound abandons work that was
	/// about to succeed.</summary>
	public sealed class OperationBound {
		[JsonProperty("operation")] public string Operation { get; set; }="";
		[JsonProperty("max_duration_ms")] public int MaxDurationMs { get; set; }
		[JsonProperty("mutates_session")] public bool MutatesSession { get; set; }
	}
	public sealed class CapabilityLimits {
		[JsonProperty("max_frames")] public int MaxFrames { get; set; }
		[JsonProperty("max_wait_timeout_ms")] public int MaxWaitTimeoutMs { get; set; }
		[JsonProperty("max_connection_timeout_ms")] public int MaxConnectionTimeoutMs { get; set; }
		[JsonProperty("max_concurrent_sessions")] public int MaxConcurrentSessions { get; set; }
		/// <summary>False, and deliberately advertised. An expired deadline abandons the wait and reports
		/// deadline_exceeded once, but dnSpy exposes no way to cancel a queued dispatcher callback or a
		/// started evaluation, so that work still runs to completion.</summary>
		[JsonProperty("cancels_in_flight_work")] public bool CancelsInFlightWork { get; set; }
	}
	public sealed class CapabilityInfo {
		[JsonProperty("host_id")] public string HostId { get; set; }="";
		[JsonProperty("protocol_version")] public int ProtocolVersion { get; set; }=dgSpy.Protocol.ProtocolVersion.Current;
		[JsonProperty("extension_version")] public string ExtensionVersion { get; set; }="";
		[JsonProperty("operations")] public OperationBound[] Operations { get; set; }=Array.Empty<OperationBound>();
		[JsonProperty("engines")] public EngineCapabilities[] Engines { get; set; }=Array.Empty<EngineCapabilities>();
		[JsonProperty("limits")] public CapabilityLimits Limits { get; set; }=new CapabilityLimits();
	}
	public sealed class HostInfo {
		/// <summary>Milestone 1 exposes a single implicit host. host_id routing arrives with Phase 9; the
		/// field exists now so callers can carry it from the start.</summary>
		[JsonProperty("host_id")] public string HostId { get; set; }="";
		[JsonProperty("display_name")] public string DisplayName { get; set; }="";
		[JsonProperty("machine_name")] public string MachineName { get; set; }="";
		[JsonProperty("dnspy_version")] public string DnSpyVersion { get; set; }="";
		[JsonProperty("dgspy_version")] public string DgSpyVersion { get; set; }="";
		[JsonProperty("protocol_version")] public int ProtocolVersion { get; set; }=dgSpy.Protocol.ProtocolVersion.Current;
		[JsonProperty("operating_system")] public string OperatingSystem { get; set; }="";
		[JsonProperty("architecture")] public string Architecture { get; set; }="";
		[JsonProperty("dnspy_process_id")] public int DnSpyProcessId { get; set; }
		[JsonProperty("connection_state")] public string ConnectionState { get; set; }="connected";
		[JsonProperty("engines")] public string[] Engines { get; set; }=Array.Empty<string>();
		/// <summary>How this endpoint authenticates callers. Milestone 1: none, loopback-only.</summary>
		[JsonProperty("authentication")] public string Authentication { get; set; }="";
		[JsonProperty("session_id", NullValueHandling=NullValueHandling.Ignore)] public string? SessionId { get; set; }
	}
	/// <summary>The static half of the capability contract, shared by the extension that serves it and the
	/// gateway that has to respect it. It lives in the protocol assembly precisely so the two cannot
	/// drift: the gateway's per-tool deadline is computed from <see cref="BoundMs"/>, not guessed.</summary>
	public static class CapabilityCatalog {
		public const string HostId = "local";
		static OperationBound Op(string operation,int maxDurationMs,bool mutates=false) => new OperationBound { Operation=operation, MaxDurationMs=maxDurationMs, MutatesSession=mutates };
		/// <summary>Every operation the extension dispatches, with the extension's own worst case. The
		/// values come from the waits in RpcHost: attach waits up to 10 s for the engine to enumerate
		/// threads, attach_endpoint waits the Mono connection timeout (capped at 5 min) plus 5 s, the
		/// breakpoint and frame paths wait 3 s per bounded step, and wait_for_stop clamps its own timeout.</summary>
		public static readonly OperationBound[] Operations = {
			Op("ping",5000),
			Op("get_host_info",5000),
			Op("get_capabilities",5000),
			Op("list_programs",20000),
			Op("attach",12000,mutates:true),
			Op("attach_endpoint",305000,mutates:true),
			Op("launch",15000,mutates:true),
			Op("get_session_state",5000),
			Op("list_sessions",5000),
			Op("detach",12000,mutates:true),
			Op("terminate",12000,mutates:true),
			Op("restart",15000,mutates:true),
			Op("pause",12000,mutates:true),
			Op("continue",12000,mutates:true),
			Op("set_il_breakpoint",10000,mutates:true),
			Op("list_breakpoints",5000),
			Op("remove_breakpoint",5000,mutates:true),
			Op("clear_breakpoints",5000,mutates:true),
			Op("wait_for_stop",12000),
			Op("get_events",5000),
			Op("wait_for_event",12000),
			Op("get_stop_reason",5000),
			Op("list_threads",8000),
			Op("get_callstack",20000),
			Op("get_frame",20000),
		};
		public static readonly EngineCapabilities[] Engines = {
			new EngineCapabilities {
				Engine="cordebug", DisplayName=".NET Framework CorDebug (CLR v4.0.30319)",
				Acquisition=new[]{"list_programs+attach"}, Discoverable=true,
				ArbitraryIlOffsetBreakpoints=true, SequencePointBreakpointsOnly=false, DetachWithoutTerminating=true,
				Notes="Accepts a breakpoint at any IL offset. Closing dnSpy with a session attached terminates the target; detach instead.",
			},
			new EngineCapabilities {
				Engine="unity", DisplayName="Mono/Unity soft debugger",
				Acquisition=new[]{"attach_endpoint"}, Discoverable=false,
				ArbitraryIlOffsetBreakpoints=false, SequencePointBreakpointsOnly=true, DetachWithoutTerminating=true,
				Notes="A target launched with an explicit --debugger-agent endpoint emits no discovery beacon, so only attach_endpoint reaches it, and the endpoint accepts one connection per launch. Breakpoints bind only at sequence points.",
			},
		};
		public static readonly CapabilityLimits Limits = new CapabilityLimits {
			MaxFrames=100, MaxWaitTimeoutMs=10000, MaxConnectionTimeoutMs=300000, MaxConcurrentSessions=1, CancelsInFlightWork=false,
		};
		public static bool IsKnownOperation(string operation) => Operations.Any(o=>o.Operation==operation);
		/// <summary>The extension's upper bound for an operation, or 0 when it is not a known operation.</summary>
		public static int BoundMs(string operation) => Operations.FirstOrDefault(o=>o.Operation==operation)?.MaxDurationMs ?? 0;
		public static CapabilityInfo Describe(string extensionVersion) => new CapabilityInfo {
			HostId=HostId, ExtensionVersion=extensionVersion, Operations=Operations, Engines=Engines, Limits=Limits,
		};
	}
}
