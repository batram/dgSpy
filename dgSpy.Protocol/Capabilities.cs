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
		/// <summary>Every value the normalized event stream can produce in <c>kind</c>, and therefore every
		/// value the <c>kinds</c> filter accepts.</summary>
		[JsonProperty("event_kinds")] public string[] EventKinds { get; set; }=Array.Empty<string>();
		/// <summary>Every value a <c>stopped</c> event can carry in <c>stop_reason</c>.</summary>
		[JsonProperty("stop_reasons")] public string[] StopReasons { get; set; }=Array.Empty<string>();
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
	/// <summary>The complete vocabulary of the normalized event stream. These are constants rather than
	/// literals at the call sites on purpose: <c>kinds</c> is a caller-supplied filter, and a kind that
	/// exists in the extension but not in this list would silently match nothing — a working breakpoint
	/// that appears never to fire. Every <c>Record</c> call site names a member of this class, so a new
	/// kind cannot ship without becoming filterable and advertised in the same edit.</summary>
	public static class EventKinds {
		// Session and lifecycle, recorded by dgSpy itself.
		public const string SessionStarted = "session_started";
		public const string SessionEnded = "session_ended";
		public const string Attached = "attached";
		public const string AttachFailed = "attach_failed";
		public const string Detached = "detached";
		public const string Restarted = "restarted";
		public const string Continued = "continued";
		public const string Terminated = "terminated";
		public const string RestartProcessExited = "restart_process_exited";
		public const string SessionExited = "session_exited";
		// Debugger messages, normalized from DbgMessageEventArgs.
		public const string ProcessCreated = "process_created";
		public const string RuntimeCreated = "runtime_created";
		public const string RuntimeExited = "runtime_exited";
		public const string ModuleLoaded = "module_loaded";
		public const string ModuleUnloaded = "module_unloaded";
		public const string ThreadCreated = "thread_created";
		public const string ThreadExited = "thread_exited";
		public const string ExceptionThrown = "exception_thrown";
		public const string BreakpointHit = "breakpoint_hit";
		public const string StepCompleted = "step_completed";
		public const string EntryPoint = "entry_point";
		public const string ProgramBreak = "program_break";
		public const string Break = "break";
		/// <summary>The synthesized whole-process stop. This, not the raw debugger message, is what
		/// <c>wait_for_stop</c> waits on and what carries <c>stop_reason</c>.</summary>
		public const string Stopped = "stopped";
		public static readonly string[] All = {
			SessionStarted, SessionEnded, Attached, AttachFailed, Detached, Restarted, Continued,
			Terminated, RestartProcessExited, SessionExited, ProcessCreated, RuntimeCreated, RuntimeExited,
			ModuleLoaded, ModuleUnloaded, ThreadCreated, ThreadExited, ExceptionThrown, BreakpointHit,
			StepCompleted, EntryPoint, ProgramBreak, Break, Stopped,
		};
		public static bool IsKnown(string kind) => Array.IndexOf(All,kind)>=0;
	}
	/// <summary>What a <see cref="EventKinds.Stopped"/> event reports in <c>stop_reason</c>.</summary>
	public static class StopReasons {
		public const string Breakpoint = "breakpoint";
		public const string Exception = "exception";
		public const string Step = "step";
		public const string EntryPoint = "entry_point";
		public const string ProgramBreak = "program_break";
		public const string Pause = "pause";
		/// <summary>The process paused with no break message dgSpy recognizes. Not an error — a Unity pause
		/// can arrive this way — but the caller cannot infer why it stopped.</summary>
		public const string Unknown = "unknown";
		public static readonly string[] All = { Breakpoint, Exception, Step, EntryPoint, ProgramBreak, Pause, Unknown };
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
			EventKinds=dgSpy.Protocol.EventKinds.All, StopReasons=dgSpy.Protocol.StopReasons.All,
		};
	}
}
