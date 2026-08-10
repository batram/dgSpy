using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	public enum AtomicActionResumePolicy { preserve_stop, resume }
	public enum AtomicActionInterruptionPolicy { cancel_on_disconnect, complete_on_disconnect }
	public enum PatchedTargetState { unknown, not_patched, patched }

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
		[JsonPropertyName("patched_target_detection"),JsonConverter(typeof(JsonStringEnumConverter))]
		public PatchedTargetState PatchedTargetDetection { get; set; }
		[JsonPropertyName("error")]
		public string? Error { get; set; }
		[JsonPropertyName("final_debugger_state")]
		public AtomicActionFinalState FinalDebuggerState { get; set; }=new AtomicActionFinalState();
	}

	public sealed class AtomicActionStop {
		public AtomicActionStop(string runtimeId,string appDomainId,int processId,string threadId,string module,uint methodToken,uint ilOffset,bool evaluable) {
			RuntimeId=runtimeId; AppDomainId=appDomainId; ProcessId=processId; ThreadId=threadId; Module=module;
			MethodToken=methodToken; IlOffset=ilOffset; Evaluable=evaluable;
		}
		public string RuntimeId { get; }
		public string AppDomainId { get; }
		public int ProcessId { get; }
		public string ThreadId { get; }
		public string Module { get; }
		public uint MethodToken { get; }
		public uint IlOffset { get; }
		public bool Evaluable { get; }
	}

	public sealed class AtomicActionInterruptedException : OperationCanceledException {
		public AtomicActionInterruptedException(InterruptionReason reason,string message,bool actionMayHaveExecuted=false) : base(message) { Reason=reason; ActionMayHaveExecuted=actionMayHaveExecuted; }
		public InterruptionReason Reason { get; }
		public bool ActionMayHaveExecuted { get; }
	}
}
