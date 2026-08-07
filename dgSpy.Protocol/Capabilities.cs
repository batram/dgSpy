using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace dgSpy.Protocol {
	/// <summary>One supported debugger engine and the behavior that is genuinely specific to it.
	/// Advertised rather than assumed: CorDebug and Mono/Unity differ in ways a caller cannot guess,
	/// most sharply in what counts as a legal breakpoint location.</summary>
	public sealed class EngineCapabilities {
		[JsonPropertyName("engine")] public string Engine { get; set; }="";
		[JsonPropertyName("display_name")] public string DisplayName { get; set; }="";
		/// <summary>How a session on this engine is acquired, eg. "list_programs+attach".</summary>
		[JsonPropertyName("acquisition")] public string[] Acquisition { get; set; }=Array.Empty<string>();
		[JsonPropertyName("discoverable")] public bool Discoverable { get; set; }
		[JsonPropertyName("arbitrary_il_offset_breakpoints")] public bool ArbitraryIlOffsetBreakpoints { get; set; }
		[JsonPropertyName("sequence_point_breakpoints_only")] public bool SequencePointBreakpointsOnly { get; set; }
		[JsonPropertyName("detach_without_terminating")] public bool DetachWithoutTerminating { get; set; }
		[JsonPropertyName("method_invocation")] public bool MethodInvocation { get; set; }
		[JsonPropertyName("object_construction")] public bool ObjectConstruction { get; set; }
		[JsonPropertyName("memory_access")] public bool MemoryAccess { get; set; }
		[JsonPropertyName("native_disassembly")] public bool NativeDisassembly { get; set; }
		[JsonPropertyName("registers")] public bool Registers { get; set; }
		[JsonPropertyName("set_instruction_pointer")] public bool SetInstructionPointer { get; set; }
		[JsonPropertyName("abort_function_evaluation")] public bool AbortFunctionEvaluation { get; set; }
		[JsonPropertyName("object_ids")] public bool ObjectIds { get; set; }
		[JsonPropertyName("exception_modes")] public string[] ExceptionModes { get; set; }=Array.Empty<string>();
		[JsonPropertyName("notes")] public string Notes { get; set; }="";
	}
	/// <summary>The extension's own upper bound for one operation. The gateway derives its deadline from
	/// this instead of guessing: a gateway deadline shorter than the inner bound abandons work that was
	/// about to succeed.</summary>
	public sealed class OperationBound {
		[JsonPropertyName("operation")] public string Operation { get; set; }="";
		[JsonPropertyName("max_duration_ms")] public int MaxDurationMs { get; set; }
		[JsonPropertyName("mutates_session")] public bool MutatesSession { get; set; }
	}
	public sealed class CapabilityLimits {
		[JsonPropertyName("max_frames")] public int MaxFrames { get; set; }
		[JsonPropertyName("max_wait_timeout_ms")] public int MaxWaitTimeoutMs { get; set; }
		[JsonPropertyName("max_connection_timeout_ms")] public int MaxConnectionTimeoutMs { get; set; }
		[JsonPropertyName("max_concurrent_sessions")] public int MaxConcurrentSessions { get; set; }
		/// <summary>False, and deliberately advertised. An expired deadline abandons the wait and reports
		/// deadline_exceeded once, but dnSpy exposes no way to cancel a queued dispatcher callback or a
		/// started evaluation, so that work still runs to completion.</summary>
		[JsonPropertyName("cancels_in_flight_work")] public bool CancelsInFlightWork { get; set; }
		[JsonPropertyName("max_evaluation_timeout_ms")] public int MaxEvaluationTimeoutMs { get; set; }
		[JsonPropertyName("max_value_export_bytes")] public int MaxValueExportBytes { get; set; }
	}
	public sealed class CapabilityInfo {
		[JsonPropertyName("host_id")] public string HostId { get; set; }="";
		[JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }=dgSpy.Protocol.ProtocolVersion.Current;
		[JsonPropertyName("extension_version")] public string ExtensionVersion { get; set; }="";
		[JsonPropertyName("operations")] public OperationBound[] Operations { get; set; }=Array.Empty<OperationBound>();
		[JsonPropertyName("engines")] public EngineCapabilities[] Engines { get; set; }=Array.Empty<EngineCapabilities>();
		[JsonPropertyName("limits")] public CapabilityLimits Limits { get; set; }=new CapabilityLimits();
		/// <summary>Every value the normalized event stream can produce in <c>kind</c>, and therefore every
		/// value the <c>kinds</c> filter accepts.</summary>
		[JsonPropertyName("event_kinds")] public string[] EventKinds { get; set; }=Array.Empty<string>();
		/// <summary>Every value a <c>stopped</c> event can carry in <c>stop_reason</c>.</summary>
		[JsonPropertyName("stop_reasons")] public string[] StopReasons { get; set; }=Array.Empty<string>();
		/// <summary><c>step_into</c>, <c>step_over</c> and <c>step_out</c> accept these.</summary>
		[JsonPropertyName("step_kinds")] public string[] StepKinds { get; set; }=Array.Empty<string>();
		/// <summary>Values <c>update_breakpoint</c> accepts for <c>condition_kind</c>.</summary>
		[JsonPropertyName("condition_kinds")] public string[] ConditionKinds { get; set; }=Array.Empty<string>();
		/// <summary>Values <c>update_breakpoint</c> accepts for <c>hit_count_kind</c>.</summary>
		[JsonPropertyName("hit_count_kinds")] public string[] HitCountKinds { get; set; }=Array.Empty<string>();
	}
	public sealed class HostInfo {
		/// <summary>Stable identity of the extension endpoint. It is generated once per installation or
		/// supplied explicitly for a managed host.</summary>
		[JsonPropertyName("host_id")] public string HostId { get; set; }="";
		[JsonPropertyName("display_name")] public string DisplayName { get; set; }="";
		[JsonPropertyName("machine_name")] public string MachineName { get; set; }="";
		[JsonPropertyName("dnspy_version")] public string DnSpyVersion { get; set; }="";
		[JsonPropertyName("dgspy_version")] public string DgSpyVersion { get; set; }="";
		/// <summary>SHA-256 of the extension assembly this process actually loaded. It is the only field
		/// that identifies running code rather than a number someone typed: a stale deployment reports a
		/// stale hash here while every hand-maintained version string still looks current. Compare it
		/// against the packaged assembly to prove which build answered the call.</summary>
		[JsonPropertyName("extension_sha256")] public string ExtensionSha256 { get; set; }="";
		/// <summary>Where that assembly was loaded from, so a mismatch names the tree to replace.</summary>
		[JsonPropertyName("extension_path")] public string ExtensionPath { get; set; }="";
		/// <summary>The same build identity a human reads off the dnSpy title bar, as
		/// "yyyy-MM-dd HH:mm (commit)" in the host's local time. Use it to confirm the running build is the
		/// one just compiled; use <see cref="ExtensionSha256"/> when an exact identity is needed.</summary>
		[JsonPropertyName("build_label")] public string BuildLabel { get; set; }="";
		/// <summary>When the loaded extension assembly was written, for comparisons that need an ordering
		/// rather than a label.</summary>
		[JsonPropertyName("build_time_utc"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DateTime? BuildTimeUtc { get; set; }
		/// <summary>Short commit the running build was packaged from, suffixed "-dirty" when the tree had
		/// uncommitted changes. Absent for a build that was never packaged, which has no commit to name.</summary>
		[JsonPropertyName("build_commit"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? BuildCommit { get; set; }
		/// <summary>How many cached assemblies were evicted because they described a different build than
		/// the one running. Non-zero means a target was rebuilt while this dnSpy stayed up; the symbols are
		/// correct because they were re-read, but it is the signal that this host is not freshly started.</summary>
		[JsonPropertyName("stale_module_documents_dropped")] public long StaleModuleDocumentsDropped { get; set; }
		[JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; }=dgSpy.Protocol.ProtocolVersion.Current;
		[JsonPropertyName("operating_system")] public string OperatingSystem { get; set; }="";
		[JsonPropertyName("architecture")] public string Architecture { get; set; }="";
		[JsonPropertyName("dnspy_process_id")] public int DnSpyProcessId { get; set; }
		[JsonPropertyName("connection_state")] public string ConnectionState { get; set; }="connected";
		[JsonPropertyName("dispatcher_state")] public string DispatcherState { get; set; }="healthy";
		[JsonPropertyName("dispatcher_fault_count")] public long DispatcherFaultCount { get; set; }
		[JsonPropertyName("last_dispatcher_fault_utc"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DateTime? LastDispatcherFaultUtc { get; set; }
		[JsonPropertyName("last_dispatcher_fault"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? LastDispatcherFault { get; set; }
		[JsonPropertyName("evaluation_queue_state")] public string EvaluationQueueState { get; set; }="idle";
		[JsonPropertyName("evaluation_active_since_utc"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DateTime? EvaluationActiveSinceUtc { get; set; }
		[JsonPropertyName("evaluation_pending")] public int EvaluationPending { get; set; }
		[JsonPropertyName("engines")] public string[] Engines { get; set; }=Array.Empty<string>();
		/// <summary>How this endpoint authenticates gateway RPC callers.</summary>
		[JsonPropertyName("authentication")] public string Authentication { get; set; }="";
		[JsonPropertyName("session_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? SessionId { get; set; }
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
	/// <summary>Caller-supplied vocabularies for Phase 4. Same rule as <see cref="EventKinds"/>: a value a
	/// caller has to type is worthless unless it is advertised and an unrecognized one is rejected.</summary>
	public static class StepKinds {
		public const string Into = "into";
		public const string Over = "over";
		public const string Out = "out";
		public static readonly string[] All = { Into, Over, Out };
		public static bool IsKnown(string kind) => Array.IndexOf(All,kind)>=0;
	}
	public static class BreakpointConditionKinds {
		/// <summary>Stop when the expression evaluates true.</summary>
		public const string IsTrue = "is_true";
		/// <summary>Stop when the expression's value differs from the previous hit.</summary>
		public const string WhenChanged = "when_changed";
		public static readonly string[] All = { IsTrue, WhenChanged };
		public static bool IsKnown(string kind) => Array.IndexOf(All,kind)>=0;
	}
	public static class HitCountKinds {
		/// <summary>Stop on exactly the nth hit.</summary>
		public const string Equals = "equals";
		/// <summary>Stop on every nth hit.</summary>
		public const string MultipleOf = "multiple_of";
		/// <summary>Stop on the nth hit and every hit after it. dnSpy calls this GreaterThanOrEquals.</summary>
		public const string AtLeast = "at_least";
		public static readonly string[] All = { Equals, MultipleOf, AtLeast };
		public static bool IsKnown(string kind) => Array.IndexOf(All,kind)>=0;
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
			// Phase 4. A step is issued and acknowledged; the stop arrives on the event stream, so the
			// bound covers issuing it plus a short grace for a step that lands immediately.
			Op("update_breakpoint",10000,mutates:true),
			Op("set_exception_breakpoint",5000,mutates:true),
			Op("list_exception_breakpoints",5000),
			Op("step_into",15000,mutates:true),
			Op("step_over",15000,mutates:true),
			Op("step_out",15000,mutates:true),
			// Phase 5. Evaluation runs on its own thread, not the dispatcher, and with func-eval off it is
			// milliseconds. The bound is the frame-capture waits plus room for a func-eval the caller
			// opted into; set_value always executes in the target, so it gets the same headroom.
			Op("evaluate",20000),
			Op("get_members",20000),
			Op("set_value",20000,mutates:true),
			Op("get_exception",20000),
			Op("add_watch",5000),
			Op("list_watches",20000),
			Op("remove_watch",5000),
			Op("list_modules",8000),
			// Phase 6. Metadata loads lazily and decompilation is CPU-bound over an arbitrarily large
			// method, so these get the widest bounds in the table. They run on the evaluation queue, not
			// the dispatcher, so a slow one delays other evaluations but never event delivery.
			Op("list_documents",30000),
			Op("list_types",30000),
			Op("list_members",30000),
			// The discovery entry point. Its work bound is max_scan rather than the clock, but a scan of
			// every type in a Unity process is still the longest read-only walk on this surface.
			Op("search",120000),
			Op("search_symbols",60000),
			Op("get_il",30000),
			Op("get_csharp",60000),
			Op("search_text",120000),
			Op("find_references",60000),
			Op("find_implementations",60000),
			Op("get_metadata",30000),
			Op("get_raw_module",60000),
			Op("set_breakpoint",30000,mutates:true),
			// Phase 7. Invocation is deliberately a separate, always-side-effecting surface. The hard
			// func-eval deadline is passed into dnSpy's evaluation context rather than merely timing out
			// the RPC wait. Raw memory is capped by the tool schema and low-level operations are bounded.
			Op("invoke_method",12000,mutates:true),
			Op("create_object",12000,mutates:true),
			Op("read_memory",8000),
			Op("write_memory",8000,mutates:true),
			Op("get_disassembly",12000),
			Op("get_registers",8000),
			Op("set_instruction_pointer",12000,mutates:true),
			// Phase 8 debugger-completeness operations.
			Op("create_object_id",20000,mutates:true), Op("list_object_ids",5000), Op("evaluate_object_id",20000), Op("release_object_id",5000,mutates:true),
			Op("get_autos",20000), Op("get_output",5000), Op("wait_for_output",12000),
			Op("set_module_breakpoint",5000,mutates:true), Op("list_module_breakpoints",5000), Op("update_module_breakpoint",5000,mutates:true), Op("remove_module_breakpoint",5000,mutates:true),
			Op("export_breakpoints",10000), Op("import_breakpoints",30000,mutates:true),
			Op("list_exception_categories",5000), Op("list_exception_policies",10000), Op("set_exception_policy",5000,mutates:true), Op("remove_exception_policy",5000,mutates:true), Op("restore_exception_defaults",5000,mutates:true),
			Op("get_value_export",20000), Op("write_value_export",20000,mutates:true), Op("analyze_symbol",60000),
		};
		public static readonly EngineCapabilities[] Engines = {
			new EngineCapabilities {
				Engine="cordebug", DisplayName=".NET Framework CorDebug (CLR v4.0.30319)",
				Acquisition=new[]{"list_programs+attach"}, Discoverable=true,
				ArbitraryIlOffsetBreakpoints=true, SequencePointBreakpointsOnly=false, DetachWithoutTerminating=true,
				MethodInvocation=true,ObjectConstruction=true,MemoryAccess=true,NativeDisassembly=true,Registers=false,SetInstructionPointer=true,AbortFunctionEvaluation=true,ObjectIds=true,ExceptionModes=new[]{"thrown","unhandled"},
				Notes="Accepts a breakpoint at any IL offset. Closing dnSpy with a session attached terminates the target; detach instead.",
			},
			new EngineCapabilities {
				Engine="unity", DisplayName="Mono/Unity soft debugger",
				Acquisition=new[]{"attach_endpoint"}, Discoverable=false,
				ArbitraryIlOffsetBreakpoints=false, SequencePointBreakpointsOnly=true, DetachWithoutTerminating=true,
				MethodInvocation=true,ObjectConstruction=true,MemoryAccess=true,NativeDisassembly=false,Registers=false,SetInstructionPointer=true,AbortFunctionEvaluation=true,ObjectIds=true,ExceptionModes=new[]{"thrown","unhandled"},
				Notes="A target launched with an explicit --debugger-agent endpoint emits no discovery beacon, so only attach_endpoint reaches it, and the endpoint accepts one connection per launch. Breakpoints bind only at sequence points.",
			},
		};
		public static readonly CapabilityLimits Limits = new CapabilityLimits {
			MaxFrames=100, MaxWaitTimeoutMs=10000, MaxConnectionTimeoutMs=300000, MaxConcurrentSessions=1, CancelsInFlightWork=false, MaxEvaluationTimeoutMs=10000, MaxValueExportBytes=16*1024*1024,
		};
		public static bool IsKnownOperation(string operation) => Operations.Any(o=>o.Operation==operation);
		/// <summary>The extension's upper bound for an operation, or 0 when it is not a known operation.</summary>
		public static int BoundMs(string operation) => Operations.FirstOrDefault(o=>o.Operation==operation)?.MaxDurationMs ?? 0;
		public static CapabilityInfo Describe(string extensionVersion,string? hostId=null) => new CapabilityInfo {
			HostId=hostId ?? HostId, ExtensionVersion=extensionVersion, Operations=Operations, Engines=Engines, Limits=Limits,
			EventKinds=dgSpy.Protocol.EventKinds.All, StopReasons=dgSpy.Protocol.StopReasons.All,
			StepKinds=dgSpy.Protocol.StepKinds.All, ConditionKinds=dgSpy.Protocol.BreakpointConditionKinds.All,
			HitCountKinds=dgSpy.Protocol.HitCountKinds.All,
		};
	}
}
