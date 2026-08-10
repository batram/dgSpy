using System;
using System.Threading;
using System.Threading.Tasks;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>T09 adds the managed-payload implementation through this boundary.</summary>
	public interface IAtomicAction {
		string Kind { get; }
		/// <summary>
		/// The operation that resolves an ambiguous or potentially side-effecting outcome for this action
		/// kind, or null to use the state machine's own status operation. An action that leaves something
		/// behind in the target - a loaded payload, an endpoint, a discovery record - is reconciled by
		/// looking at that thing, not by re-reading the action record, so it names its own operation.
		/// The state machine still decides <em>whether</em> reconciliation is needed; the action only
		/// supplies the name.
		/// </summary>
		string? ReconciliationOperation { get; }
		Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken cancellationToken);
		Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken cancellationToken);
		/// <summary>
		/// Undo whatever this action put into the target, inside the lease's bounded cleanup window. Called
		/// exactly once whenever <see cref="ExecuteAsync"/> was entered - including on a failed action, a
		/// failed verification, a timeout, a disconnect and an external interruption - so a rollback is not
		/// a corner case reachable only from the success path. Its reported outcome is merged into
		/// <c>cleanup_outcome</c> by severity, so a successful rollback cannot mask a failed breakpoint
		/// release and a failed rollback cannot be hidden by a clean one.
		/// </summary>
		Task<AtomicActionCleanup> CleanupAsync(AtomicActionCleanupContext context,CancellationToken cancellationToken);
	}

	public sealed class AtomicActionContext {
		public AtomicActionContext(AtomicActionRequest request,AtomicActionStop stop,AtomicActionSlot slot,string auditId) { Request=request; Stop=stop; Slot=slot; AuditId=auditId; }
		public AtomicActionRequest Request { get; }
		public AtomicActionStop Stop { get; }
		public AtomicActionSlot Slot { get; }
		/// <summary>The one audit id this invocation is reported under. An action that audits its own
		/// mutations correlates them with this value rather than minting a second, unrelated id.</summary>
		public string AuditId { get; }
	}

	/// <summary>What the state machine knows about the action when it asks the action to clean up.</summary>
	public sealed class AtomicActionCleanupContext {
		public AtomicActionCleanupContext(AtomicActionContext action,ActionOutcome actionOutcome,InterruptionReason interruptionReason,bool actionMayHaveExecuted,DateTime windowEndsUtc) {
			Action=action; ActionOutcome=actionOutcome; InterruptionReason=interruptionReason; ActionMayHaveExecuted=actionMayHaveExecuted; WindowEndsUtc=windowEndsUtc;
		}
		public AtomicActionContext Action { get; }
		public ActionOutcome ActionOutcome { get; }
		public InterruptionReason InterruptionReason { get; }
		public bool ActionMayHaveExecuted { get; }
		/// <summary>The instant the lease's cleanup window ends. Work past it is cancelled, and the action
		/// loses its authorization to mutate the target, so bound the rollback inside it.</summary>
		public DateTime WindowEndsUtc { get; }
		/// <summary>True when the action did not reach a verified success, which is the default condition
		/// for rolling back rather than keeping what the action installed.</summary>
		public bool RollbackRecommended => ActionOutcome!=ActionOutcome.completed || InterruptionReason!=InterruptionReason.none;
	}

	public sealed class AtomicActionCleanup {
		/// <summary>An action with nothing to undo reports <c>not_required</c>, which never downgrades what
		/// the state machine's own cleanup found.</summary>
		public CleanupOutcome Outcome { get; set; }=CleanupOutcome.not_required;
		public string? Evidence { get; set; }
		public string? Error { get; set; }
		/// <summary>Overrides the reconciliation operation when this particular cleanup left something that
		/// a different operation resolves.</summary>
		public string? ReconciliationOperation { get; set; }
	}

	public interface IAtomicActionBreakpoint : IDisposable {
		Guid OwnerToken { get; }
		string? BindError { get; }
		Task<bool> WaitBoundAsync(CancellationToken cancellationToken);
		Task ReleaseAsync(CancellationToken cancellationToken);
	}

	public interface IAtomicActionHost {
		long CaptureEventCursor();
		PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot);
		Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> hit,CancellationToken cancellationToken);
		/// <summary>The host marshals to the debugger thread and passes its one synchronous engine call to authorize.</summary>
		Task ContinueAsync(Action<Action> authorize,CancellationToken cancellationToken);
		Task<AtomicActionStop> WaitForOwnedStopAsync(Guid ownerToken,long afterEventCursor,CancellationToken cancellationToken);
		Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? stop,CancellationToken cancellationToken);
		Task ReleaseTemporaryHandlesAsync(CancellationToken cancellationToken);
		Task ResumeAsync(Action<Action> authorize,CancellationToken cancellationToken);
		Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken cancellationToken);
	}
}
