using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	public enum AtomicActionResumePolicy { preserve_stop, resume }
	public enum AtomicActionInterruptionPolicy { cancel_on_disconnect, complete_on_disconnect }
	public enum PatchedTargetState { unknown, not_patched, patched }

	/// <summary>
	/// Where an asynchronous action has got to. Monotonic: a record's phase only ever moves forward, so a
	/// poller can compare two readings and never see the action go backwards.
	///
	/// <para><c>queued</c> is the phase <c>start_atomic_action</c> returns in. That boundary is the whole
	/// point of the asynchronous shape: returning after *arming* would still block the caller's single host
	/// connection while lease acquisition or breakpoint binding hung, which moves the uncancellable window
	/// rather than removing it. Cancellation is accepted in every phase but <c>terminal</c>, including
	/// <c>queued</c> and <c>arming</c>.</para>
	/// </summary>
	public enum AtomicActionPhase { queued, arming, armed, running, executing, verifying, cleaning_up, terminal }

	public sealed class AtomicActionRequest {
		[JsonPropertyName("schema_version")]
		public int SchemaVersion { get; set; }=1;
		[JsonPropertyName("action_id")]
		public string ActionId { get; set; }="";
		[JsonPropertyName("action_name")]
		public string ActionName { get; set; }="";
		[JsonPropertyName("process_id")]
		public int ProcessId { get; set; }
		[JsonPropertyName("runtime_id")]
		public string RuntimeId { get; set; }="";
		[JsonPropertyName("app_domain_id")]
		public string AppDomainId { get; set; }="";
		[JsonPropertyName("module")]
		public string Module { get; set; }="";
		[JsonPropertyName("method_token")]
		public uint MethodToken { get; set; }
		[JsonPropertyName("il_offset")]
		public uint IlOffset { get; set; }
		[JsonPropertyName("nearby_offsets")]
		public uint[] NearbyOffsets { get; set; }=Array.Empty<uint>();
		[JsonPropertyName("deadline_utc")]
		public DateTime DeadlineUtc { get; set; }
		[JsonPropertyName("resume_policy"),JsonConverter(typeof(JsonStringEnumConverter))]
		public AtomicActionResumePolicy ResumePolicy { get; set; }
		[JsonPropertyName("disconnect_policy"),JsonConverter(typeof(JsonStringEnumConverter))]
		public AtomicActionInterruptionPolicy DisconnectPolicy { get; set; }
	}

	public sealed class AtomicActionSlot {
		public AtomicActionSlot(string module,uint methodToken,uint ilOffset) { Module=module; MethodToken=methodToken; IlOffset=ilOffset; }
		[JsonPropertyName("module")] public string Module { get; }
		[JsonPropertyName("method_token")] public uint MethodToken { get; }
		[JsonPropertyName("il_offset")] public uint IlOffset { get; }
	}

	public sealed class AtomicActionExecution {
		public bool Completed { get; set; }
		public bool MayHaveExecuted { get; set; }
		public string? Evidence { get; set; }
		public string? Error { get; set; }
		/// <summary>The id the action's own audited mutation was written to the debugger log under, so the
		/// audited line can be correlated with the reported action.</summary>
		public string? MutationAuditId { get; set; }
	}

	public sealed class AtomicActionVerification {
		public bool Verified { get; set; }
		public string? Evidence { get; set; }
		public string? Error { get; set; }
	}

	public sealed class AtomicActionFinalState {
		[JsonPropertyName("session_active")] public bool SessionActive { get; set; }
		[JsonPropertyName("process_active")] public bool ProcessActive { get; set; }
		[JsonPropertyName("is_running")] public bool IsRunning { get; set; }
		[JsonPropertyName("is_paused")] public bool IsPaused { get; set; }
		[JsonPropertyName("stop_id")] public string? StopId { get; set; }
	}

	public sealed class AtomicActionResult {
		[JsonPropertyName("schema_version")]
		public int SchemaVersion { get; set; }=1;
		[JsonIgnore]
		public AtomicActionStatus Status { get; set; }=null!;
		[JsonPropertyName("action_outcome"),JsonConverter(typeof(JsonStringEnumConverter))] public ActionOutcome ActionOutcome=>Status.ActionOutcome;
		[JsonPropertyName("interruption_reason"),JsonConverter(typeof(JsonStringEnumConverter))] public InterruptionReason InterruptionReason=>Status.InterruptionReason;
		[JsonPropertyName("cleanup_outcome"),JsonConverter(typeof(JsonStringEnumConverter))] public CleanupOutcome CleanupOutcome=>Status.CleanupOutcome;
		[JsonPropertyName("action_may_have_executed")] public bool ActionMayHaveExecuted=>Status.ActionMayHaveExecuted;
		[JsonPropertyName("audit_id")] public string AuditId=>Status.AuditId;
		[JsonPropertyName("reconciliation_operation")] public string? ReconciliationOperation=>Status.ReconciliationOperation;
		[JsonPropertyName("requested_slot")]
		public AtomicActionSlot? RequestedSlot { get; set; }
		[JsonPropertyName("used_slot")]
		public AtomicActionSlot? UsedSlot { get; set; }
		[JsonPropertyName("verification_evidence")]
		public string? VerificationEvidence { get; set; }
		/// <summary>What the action reported about undoing itself. Null when the action was never entered.</summary>
		[JsonPropertyName("cleanup_evidence")]
		public string? CleanupEvidence { get; set; }
		/// <summary>The audit id of the action's own mutation in the debugger output log. <c>audit_id</c>
		/// identifies the invocation; this identifies the audited line inside it.</summary>
		[JsonPropertyName("mutation_audit_id")]
		public string? MutationAuditId { get; set; }
		/// <summary>The deadline actually enforced, which is the requested one clamped to the operation's
		/// bound. Reported so a clamp is never silent.</summary>
		[JsonPropertyName("effective_deadline_utc")]
		public DateTime EffectiveDeadlineUtc { get; set; }
		[JsonPropertyName("patched_target_detection"),JsonConverter(typeof(JsonStringEnumConverter))]
		public PatchedTargetState PatchedTargetDetection { get; set; }
		/// <summary>What the evaluability preflight found at the owned stop. <c>none</c> is a preflight, not
		/// a promise - see <see cref="AtomicActionEvaluationBlocker"/>. Left at <c>none</c> when the run
		/// never reached a stop, which is why <c>action_outcome</c> and not this field says whether the
		/// target was reached.</summary>
		[JsonPropertyName("evaluation_blocker"),JsonConverter(typeof(JsonStringEnumConverter))]
		public AtomicActionEvaluationBlocker EvaluationBlocker { get; set; }
		[JsonPropertyName("evaluation_probe_stage"),JsonConverter(typeof(JsonStringEnumConverter))]
		public AtomicActionEvaluationProbeStage EvaluationProbeStage { get; set; }
		/// <summary>A stable exception category, never a serialized exception and never a stack trace.</summary>
		[JsonPropertyName("evaluation_probe_error_category")]
		public string? EvaluationProbeErrorCategory { get; set; }
		/// <summary>A bounded, redacted detail - an HRESULT for a COM failure, otherwise a length-capped
		/// message with every quoted run replaced, because that is where an evaluator puts the expression
		/// and the target value it was working on.</summary>
		[JsonPropertyName("evaluation_probe_error")]
		public string? EvaluationProbeError { get; set; }
		[JsonPropertyName("error")]
		public string? Error { get; set; }
		[JsonPropertyName("final_debugger_state")]
		public AtomicActionFinalState FinalDebuggerState { get; set; }=new AtomicActionFinalState();
	}

	public sealed class AtomicActionStop {
		public AtomicActionStop(string runtimeId,string appDomainId,int processId,string threadId,string module,uint methodToken,uint ilOffset,AtomicActionEvaluationProbe evaluation) {
			RuntimeId=runtimeId; AppDomainId=appDomainId; ProcessId=processId; ThreadId=threadId; Module=module;
			MethodToken=methodToken; IlOffset=ilOffset; Evaluation=evaluation ?? throw new ArgumentNullException(nameof(evaluation));
		}
		public string RuntimeId { get; }
		public string AppDomainId { get; }
		public int ProcessId { get; }
		public string ThreadId { get; }
		public string Module { get; }
		public uint MethodToken { get; }
		public uint IlOffset { get; }
		/// <summary>The evaluability preflight's verdict, carried whole rather than collapsed to a bool: the
		/// terminal result has to name the real blocker, and a failed probe has to be distinguishable from a
		/// clean one.</summary>
		public AtomicActionEvaluationProbe Evaluation { get; }
		public bool Evaluable => Evaluation.Evaluable;
	}

	public sealed class AtomicActionInterruptedException : OperationCanceledException {
		public AtomicActionInterruptedException(InterruptionReason reason,string message,bool actionMayHaveExecuted=false) : base(message) { Reason=reason; ActionMayHaveExecuted=actionMayHaveExecuted; }
		public InterruptionReason Reason { get; }
		public bool ActionMayHaveExecuted { get; }
	}
}
