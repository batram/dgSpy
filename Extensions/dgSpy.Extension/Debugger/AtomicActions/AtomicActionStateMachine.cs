using System;
using System.Threading;
using System.Threading.Tasks;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	public sealed class AtomicActionStateMachine {
		public const string StatusOperation="get_atomic_action_status";
		public const string CancelOperation="cancel_atomic_action";
		readonly ActionLeaseCoordinator leases;
		readonly IAtomicActionHost host;

		public AtomicActionStateMachine(ActionLeaseCoordinator leases,IAtomicActionHost host) { this.leases=leases ?? throw new ArgumentNullException(nameof(leases)); this.host=host ?? throw new ArgumentNullException(nameof(host)); }

		public async Task<AtomicActionResult> RunAsync(AtomicActionRequest request,IAtomicAction action,CancellationToken clientLifetime,CancellationToken cancellationToken) {
			Validate(request,action);
			var auditId=Guid.NewGuid().ToString("N");
			var result=new AtomicActionResult { RequestedSlot=new AtomicActionSlot(request.Module,request.MethodToken,request.IlOffset) };
			var actionOutcome=ActionOutcome.trigger_not_reached;
			var interruption=InterruptionReason.none;
			var cleanup=CleanupOutcome.not_required;
			var mayHaveExecuted=false;
			IAtomicActionBreakpoint? breakpoint=null;
			using var deadline=new CancellationTokenSource(Remaining(request.DeadlineUtc));
			using var requested=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,cancellationToken);
			var leaseLifetime=request.DisconnectPolicy==AtomicActionInterruptionPolicy.cancel_on_disconnect ? clientLifetime : CancellationToken.None;
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
				if(stop.MethodToken!=request.MethodToken || stop.IlOffset!=request.IlOffset || !String.Equals(stop.Module,request.Module,StringComparison.OrdinalIgnoreCase)) {
					if(request.NearbyOffsets.Length==0) { actionOutcome=ActionOutcome.nearby_slot_not_found; result.Error="The exact target was not reached and nearby-slot search was not allowed."; return result; }
					slot=await host.SelectNearbySlotAsync(request,stop,active.Token).ConfigureAwait(false);
					if(slot is null) { actionOutcome=ActionOutcome.nearby_slot_not_found; result.Error="No declared nearby slot was evaluable."; return result; }
				}
				result.UsedSlot=slot;
				if(!stop.Evaluable) { actionOutcome=ActionOutcome.reached_not_evaluable; result.Error="The target was reached at a CorDebug-unsafe point."; return result; }
				var context=new AtomicActionContext(request,stop,slot);
				AtomicActionExecution execution;
				try { execution=await action.ExecuteAsync(context,active.Token).ConfigureAwait(false); }
				catch(AtomicActionInterruptedException ex) { interruption=ex.Reason; mayHaveExecuted=ex.ActionMayHaveExecuted; result.Error=ex.Message; return result; }
				catch(OperationCanceledException) { actionOutcome=ActionOutcome.action_failed; mayHaveExecuted=true; throw; }
				catch(Exception ex) { actionOutcome=ActionOutcome.action_failed; mayHaveExecuted=true; result.Error=ex.Message; return result; }
				mayHaveExecuted=execution.MayHaveExecuted || execution.Completed;
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
					clientLifetime.IsCancellationRequested ? InterruptionReason.client_disconnected :
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
					cleanup=cleanupFailed ? CleanupOutcome.failed : CleanupOutcome.completed;
				}
				if(request.ResumePolicy==AtomicActionResumePolicy.resume && interruption!=InterruptionReason.external_debugger_action) {
					try { await host.ResumeAsync(mutation=>lease.ExecuteMutation(mutation),CancellationToken.None).ConfigureAwait(false); }
					catch(Exception ex) { cleanup=cleanup==CleanupOutcome.failed ? cleanup : CleanupOutcome.ambiguous; result.Error=Combine(result.Error,"Final resume was ambiguous: "+ex.Message); }
				}
				try { result.FinalDebuggerState=await host.ReadFinalStateAsync(CancellationToken.None).ConfigureAwait(false); }
				catch(Exception ex) { cleanup=CleanupOutcome.ambiguous; result.Error=Combine(result.Error,"Final debugger state is unknown: "+ex.Message); }
				result.Status=new AtomicActionStatus(actionOutcome,interruption,cleanup,mayHaveExecuted,auditId,
					cleanup==CleanupOutcome.ambiguous || mayHaveExecuted && actionOutcome!=ActionOutcome.completed ? StatusOperation : null);
			}
			return result;
		}

		static void Validate(AtomicActionRequest request,IAtomicAction action) {
			if(request is null) throw new ArgumentNullException(nameof(request)); if(action is null) throw new ArgumentNullException(nameof(action));
			if(request.SchemaVersion!=1) throw new ArgumentException("Atomic action request schema_version must be 1.",nameof(request));
			if(request.ProcessId<=0 || String.IsNullOrWhiteSpace(request.ActionId) || String.IsNullOrWhiteSpace(request.ActionName) || String.IsNullOrWhiteSpace(request.Module)) throw new ArgumentException("Action identity, process and target module are required.",nameof(request));
			if(request.DeadlineUtc.ToUniversalTime()<=DateTime.UtcNow) throw new ArgumentOutOfRangeException(nameof(request));
		}
		static TimeSpan Remaining(DateTime deadline) { var value=deadline.ToUniversalTime()-DateTime.UtcNow; return value>TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1); }
		static void EnsureIdentity(AtomicActionRequest request,AtomicActionStop stop) {
			if(stop.ProcessId!=request.ProcessId) throw new AtomicActionInterruptedException(InterruptionReason.external_debugger_action,"The owned stop belonged to another process.");
			if(!String.Equals(stop.RuntimeId,request.RuntimeId,StringComparison.Ordinal) || !String.Equals(stop.AppDomainId,request.AppDomainId,StringComparison.Ordinal)) throw new AtomicActionInterruptedException(InterruptionReason.appdomain_unloaded,"The declared runtime or AppDomain was replaced before the action ran.");
		}
		static string Combine(string? first,string second) => String.IsNullOrEmpty(first) ? second : first+" "+second;
	}
}
