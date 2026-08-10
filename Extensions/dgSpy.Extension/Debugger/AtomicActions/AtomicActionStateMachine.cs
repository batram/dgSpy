using System;
using System.Threading;
using System.Threading.Tasks;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	public sealed class AtomicActionStateMachine {
		public const string StatusOperation="get_atomic_action_status";
		public const string CancelOperation="cancel_atomic_action";
		/// <summary>Reserved out of the lease's cleanup window for the machine's own cleanup - the owned
		/// breakpoint release, the final resume and the final-state read - so an action that uses its whole
		/// rollback budget cannot leave the target paused with no authorization left to resume it.</summary>
		public static readonly TimeSpan MachineCleanupReserve=TimeSpan.FromSeconds(2);
		readonly ActionLeaseCoordinator leases;
		readonly IAtomicActionHost host;
		readonly TimeSpan actionCleanupBudget;

		public AtomicActionStateMachine(ActionLeaseCoordinator leases,IAtomicActionHost host,TimeSpan? actionCleanupBudget=null) {
			this.leases=leases ?? throw new ArgumentNullException(nameof(leases)); this.host=host ?? throw new ArgumentNullException(nameof(host));
			this.actionCleanupBudget=actionCleanupBudget ?? ActionLease.CleanupWindow-MachineCleanupReserve;
		}

		public async Task<AtomicActionResult> RunAsync(AtomicActionRequest request,IAtomicAction action,CancellationToken clientLifetime,CancellationToken cancellationToken) {
			Validate(request,action);
			var auditId=Guid.NewGuid().ToString("N");
			var result=new AtomicActionResult { RequestedSlot=new AtomicActionSlot(request.Module,request.MethodToken,request.IlOffset),EffectiveDeadlineUtc=request.DeadlineUtc.ToUniversalTime() };
			var actionOutcome=ActionOutcome.trigger_not_reached;
			var interruption=InterruptionReason.none;
			var cleanup=CleanupOutcome.not_required;
			var mayHaveExecuted=false;
			string? reconciliation=null;
			IAtomicActionBreakpoint? breakpoint=null;
			AtomicActionContext? context=null;
			using var deadline=new CancellationTokenSource(Remaining(request.DeadlineUtc));
			using var requested=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,cancellationToken);
			// Under complete_on_disconnect the client's lifetime is deliberately not linked, so a
			// disconnect must not be able to classify the run either - see the cancellation catch below.
			var clientLinked=request.DisconnectPolicy==AtomicActionInterruptionPolicy.cancel_on_disconnect;
			var leaseLifetime=clientLinked ? clientLifetime : CancellationToken.None;
			using var lease=leases.Acquire(request.ProcessId,request.ActionName,request.ActionId,request.DeadlineUtc,StatusOperation,CancelOperation,leaseLifetime);
			using var active=CancellationTokenSource.CreateLinkedTokenSource(requested.Token,lease.ExternalActionCancellation,leaseLifetime);
			try {
				var cursor=host.CaptureEventCursor();
				var slot=result.RequestedSlot!;
				result.PatchedTargetDetection=host.DetectPatchedTarget(slot);
				if(result.PatchedTargetDetection==PatchedTargetState.patched) { result.Error="The target method is patched; natural arrival at its original body is not observable (best-effort detection)."; return result; }
				breakpoint=await host.AddOwnedBreakpointAsync(slot,_=>{},active.Token).ConfigureAwait(false);
				if(!await breakpoint.WaitBoundAsync(active.Token).ConfigureAwait(false)) {
					if(request.NearbyOffsets.Length==0) { result.Error=breakpoint.BindError ?? "The owned breakpoint did not bind."; return result; }
					await breakpoint.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); breakpoint=null; cleanup=CleanupOutcome.completed;
					foreach(var offset in request.NearbyOffsets) {
						slot=new AtomicActionSlot(request.Module,request.MethodToken,offset);
						breakpoint=await host.AddOwnedBreakpointAsync(slot,_=>{},active.Token).ConfigureAwait(false);
						if(await breakpoint.WaitBoundAsync(active.Token).ConfigureAwait(false)) break;
						await breakpoint.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); breakpoint=null; cleanup=CleanupOutcome.completed;
					}
					if(breakpoint is null) { actionOutcome=ActionOutcome.nearby_slot_not_found; result.Error="No declared nearby slot bound."; return result; }
				}
				await host.ContinueAsync(mutation=>lease.ExecuteMutation(mutation),active.Token).ConfigureAwait(false);
				var stop=await host.WaitForOwnedStopAsync(breakpoint.OwnerToken,cursor,active.Token).ConfigureAwait(false);
				EnsureIdentity(request,stop);
				if(stop.MethodToken!=request.MethodToken || stop.IlOffset!=request.IlOffset || !NearbySlotSelector.SameModule(stop.Module,request.Module)) {
					if(request.NearbyOffsets.Length==0) { actionOutcome=ActionOutcome.nearby_slot_not_found; result.Error="The exact target was not reached and nearby-slot search was not allowed."; return result; }
					// The slot reported is the one execution actually bound, matched against the declared
					// offsets - never the first declared offset assumed to be where the stop landed.
					slot=await host.SelectNearbySlotAsync(request,stop,active.Token).ConfigureAwait(false);
					if(slot is null) { actionOutcome=ActionOutcome.nearby_slot_not_found; result.Error="The stop did not land on any declared nearby slot."; return result; }
				}
				result.UsedSlot=slot;
				if(!stop.Evaluable) { actionOutcome=ActionOutcome.reached_not_evaluable; result.Error="The target was reached at a CorDebug-unsafe point."; return result; }
				context=new AtomicActionContext(request,stop,slot,auditId);
				AtomicActionExecution execution;
				// Cancellation inside ExecuteAsync reports action_failed with may_have_executed, because the
				// action was in flight and its effect is unknown. Cancellation inside VerifyAsync leaves the
				// outcome at completed, because by then the action is known to have completed and only the
				// evidence for it is missing. The action outcome describes the action; the interruption
				// reason, set independently below, describes why the run ended.
				try { execution=await action.ExecuteAsync(context,active.Token).ConfigureAwait(false); }
				catch(AtomicActionInterruptedException ex) { interruption=ex.Reason; mayHaveExecuted=ex.ActionMayHaveExecuted; result.Error=ex.Message; return result; }
				catch(OperationCanceledException) { actionOutcome=ActionOutcome.action_failed; mayHaveExecuted=true; throw; }
				catch(Exception ex) { actionOutcome=ActionOutcome.action_failed; mayHaveExecuted=true; result.Error=ex.Message; return result; }
				mayHaveExecuted=execution.MayHaveExecuted || execution.Completed;
				result.MutationAuditId=execution.MutationAuditId;
				if(!execution.Completed) { actionOutcome=ActionOutcome.action_failed; result.Error=execution.Error; return result; }
				actionOutcome=ActionOutcome.completed;
				AtomicActionVerification verification;
				try { verification=await action.VerifyAsync(context,execution,active.Token).ConfigureAwait(false); }
				catch(OperationCanceledException) { throw; }
				catch(Exception ex) { actionOutcome=ActionOutcome.verification_failed; result.Error=ex.Message; return result; }
				result.VerificationEvidence=verification.Evidence ?? execution.Evidence;
				if(!verification.Verified) { actionOutcome=ActionOutcome.verification_failed; result.Error=verification.Error; }
			}
			catch(AtomicActionInterruptedException ex) { interruption=ex.Reason; mayHaveExecuted|=ex.ActionMayHaveExecuted; result.Error=ex.Message; }
			catch(RpcException ex) when(ex.Code=="dispatcher_unavailable") { interruption=InterruptionReason.dispatcher_degraded; result.Error=ex.Message; }
			catch(OperationCanceledException) {
				interruption=lease.ExternalActionCancellation.IsCancellationRequested ? InterruptionReason.external_debugger_action :
					clientLinked && clientLifetime.IsCancellationRequested ? InterruptionReason.client_disconnected :
					deadline.IsCancellationRequested ? InterruptionReason.timeout : InterruptionReason.cancelled;
				if(interruption==InterruptionReason.external_debugger_action) result.Error="External debugger action: "+(lease.ExternalActionOperation ?? "unknown");
			}
			catch(Exception ex) { actionOutcome=ActionOutcome.action_failed; result.Error=ex.Message; }
			finally {
				if(breakpoint is not null) {
					var cleanupFailed=false;
					try { await breakpoint.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
					catch(Exception ex) { cleanupFailed=true; result.Error=Combine(result.Error,"Breakpoint cleanup failed: "+ex.Message); }
					try { await host.ReleaseTemporaryHandlesAsync(CancellationToken.None).ConfigureAwait(false); }
					catch(Exception ex) { cleanupFailed=true; result.Error=Combine(result.Error,"Handle cleanup failed: "+ex.Message); }
					cleanup=Worse(cleanup,cleanupFailed ? CleanupOutcome.failed : CleanupOutcome.completed);
				}
				if(context is not null) {
					// The action gets its rollback inside the lease's window, minus the reserve the machine
					// needs for its own resume. Overrunning is reported, never silently abandoned.
					var windowEnds=lease.OwnershipEndsUtc-MachineCleanupReserve;
					var budgetEnds=DateTime.UtcNow+actionCleanupBudget;
					if(budgetEnds<windowEnds) windowEnds=budgetEnds;
					using var actionCleanup=new CancellationTokenSource(Remaining(windowEnds));
					try {
						var reported=await action.CleanupAsync(new AtomicActionCleanupContext(context,actionOutcome,interruption,mayHaveExecuted,windowEnds),actionCleanup.Token).ConfigureAwait(false);
						cleanup=Worse(cleanup,reported.Outcome);
						result.CleanupEvidence=reported.Evidence;
						if(!String.IsNullOrEmpty(reported.Error)) result.Error=Combine(result.Error,"Action cleanup: "+reported.Error);
						reconciliation=reported.ReconciliationOperation ?? reconciliation;
					}
					catch(OperationCanceledException) when(actionCleanup.IsCancellationRequested) {
						cleanup=Worse(cleanup,CleanupOutcome.ambiguous);
						result.Error=Combine(result.Error,"Action cleanup did not finish inside the lease's bounded cleanup window; what it installed may still be present.");
					}
					catch(Exception ex) { cleanup=Worse(cleanup,CleanupOutcome.failed); result.Error=Combine(result.Error,"Action cleanup failed: "+ex.Message); }
				}
				if(request.ResumePolicy==AtomicActionResumePolicy.resume && interruption!=InterruptionReason.external_debugger_action) {
					try { await host.ResumeAsync(mutation=>lease.ExecuteMutation(mutation),CancellationToken.None).ConfigureAwait(false); }
					catch(Exception ex) { cleanup=Worse(cleanup,CleanupOutcome.ambiguous); result.Error=Combine(result.Error,"Final resume was ambiguous: "+ex.Message); }
				}
				// A cleanup failure is the stronger fact: an unknown final state must not overwrite a
				// breakpoint release or a rollback that is known to have failed.
				try { result.FinalDebuggerState=await host.ReadFinalStateAsync(CancellationToken.None).ConfigureAwait(false); }
				catch(Exception ex) { cleanup=Worse(cleanup,CleanupOutcome.ambiguous); result.Error=Combine(result.Error,"Final debugger state is unknown: "+ex.Message); }
				var needsReconciliation=cleanup==CleanupOutcome.ambiguous || cleanup==CleanupOutcome.failed || mayHaveExecuted && actionOutcome!=ActionOutcome.completed;
				result.Status=new AtomicActionStatus(actionOutcome,interruption,cleanup,mayHaveExecuted,auditId,
					needsReconciliation ? reconciliation ?? action.ReconciliationOperation ?? StatusOperation : null);
			}
			return result;
		}

		/// <summary>Cleanup outcomes merge by severity, never by overwrite: <c>not_required</c> is the
		/// absence of cleanup, <c>completed</c> is cleanup that is known to have worked, <c>ambiguous</c>
		/// is cleanup whose result is unknown, and <c>failed</c> is cleanup known to have failed - the
		/// strongest fact, and the one a weaker later observation must never erase.</summary>
		internal static CleanupOutcome Worse(CleanupOutcome first,CleanupOutcome second) => Severity(second)>Severity(first) ? second : first;
		static int Severity(CleanupOutcome value) => value switch {
			CleanupOutcome.not_required=>0,CleanupOutcome.completed=>1,CleanupOutcome.ambiguous=>2,CleanupOutcome.failed=>3,_=>0,
		};

		static void Validate(AtomicActionRequest request,IAtomicAction action) {
			if(request is null) throw new ArgumentNullException(nameof(request)); if(action is null) throw new ArgumentNullException(nameof(action));
			if(request.SchemaVersion!=1) throw new ArgumentException("Atomic action request schema_version must be 1.",nameof(request));
			if(request.ProcessId<=0 || String.IsNullOrWhiteSpace(request.ActionId) || String.IsNullOrWhiteSpace(request.ActionName) || String.IsNullOrWhiteSpace(request.Module)) throw new ArgumentException("Action identity, process and target module are required.",nameof(request));
			if(request.DeadlineUtc.ToUniversalTime()<=DateTime.UtcNow) throw new ArgumentOutOfRangeException(nameof(request));
		}
		static TimeSpan Remaining(DateTime deadline) { var value=deadline.ToUniversalTime()-DateTime.UtcNow; return value>TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1); }
		static void EnsureIdentity(AtomicActionRequest request,AtomicActionStop stop) {
			if(stop.ProcessId!=request.ProcessId) throw new AtomicActionInterruptedException(InterruptionReason.external_debugger_action,"The owned stop belonged to another process.");
			// An omitted runtime or AppDomain is unconstrained, not a mismatch. Nothing over RPC reports
			// either value today, so requiring both made a valid request impossible to construct: every
			// run answered appdomain_unloaded against an id the caller had no way to learn.
			if((!String.IsNullOrEmpty(request.RuntimeId) && !String.Equals(stop.RuntimeId,request.RuntimeId,StringComparison.Ordinal))
				|| (!String.IsNullOrEmpty(request.AppDomainId) && !String.Equals(stop.AppDomainId,request.AppDomainId,StringComparison.Ordinal)))
				throw new AtomicActionInterruptedException(InterruptionReason.appdomain_unloaded,"The declared runtime or AppDomain was replaced before the action ran.");
			// The request declares no thread or frame, so there is nothing to compare those against: the
			// action runs on the thread the owned stop names, at that thread's frame 0. What can be checked
			// is that the stop actually named a thread - without one there is no frame to evaluate in.
			if(String.IsNullOrEmpty(stop.ThreadId)) throw new AtomicActionInterruptedException(InterruptionReason.target_exited,"The owned stop named no thread, so the action has no frame to run in.");
		}
		static string Combine(string? first,string second) => String.IsNullOrEmpty(first) ? second : first+" "+second;
	}
}
