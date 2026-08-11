using dgSpy.Extension.Debugger;
using dgSpy.Extension.Debugger.AtomicActions;
using HookLab.Contracts;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class AtomicActionStateMachineTests {
	[Fact]
	public async Task ExactSlotMismatchReportsRequestedAndObservedIdentity() {
		var stop=new AtomicActionStop("runtime","domain",42,"thread","other.dll",0x06000002,7,AtomicActionEvaluationProbe.Clear);
		var result=await Run(new Host(stop),new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome);
		Assert.Contains("Requested",result.Error);
		Assert.Contains("observed",result.Error);
	}

	[Fact]
	public async Task LegacyModuleNameAcceptsResolvedOwnedBreakpointModuleId() {
		var stop=new AtomicActionStop("runtime","domain",42,"thread","dm1:resolved",0x06000001,3,AtomicActionEvaluationProbe.Clear);
		var result=await Run(new Host(stop),new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
	}
	static AtomicActionRequest Request() => new() { ActionId=Guid.NewGuid().ToString("N"),ActionName="test",ProcessId=42,RuntimeId="runtime",AppDomainId="domain",Module="target.dll",MethodToken=0x06000001,IlOffset=3,DeadlineUtc=DateTime.UtcNow.AddSeconds(10) };
	static AtomicActionStop Stop(AtomicActionEvaluationProbe? evaluation=null,uint offset=3) => new("runtime","domain",42,"thread","target.dll",0x06000001,offset,evaluation ?? AtomicActionEvaluationProbe.Clear);

	[Fact] public async Task ExactSlotCompletesAndRecordsEvidence() {
		var host=new Host(Stop()); var action=new ActionImpl();
		var result=await Run(host,action,Request());
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome); Assert.Equal(InterruptionReason.none,result.Status.InterruptionReason);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome); Assert.Equal((uint)3,result.UsedSlot!.IlOffset); Assert.Equal("verified",result.VerificationEvidence);
		Assert.True(host.Breakpoint.Released);
	}

	[Fact] public async Task WireResultIsVersionedAndUsesFrozenSnakeCaseValues() {
		var result=await Run(new Host(Stop()),new ActionImpl(),Request()); var json=ProtocolJson.ToNode(result)!.AsObject();
		Assert.Equal(1,(int?)json["schema_version"]); Assert.Equal("completed",(string?)json["action_outcome"]); Assert.Equal("none",(string?)json["interruption_reason"]); Assert.Equal("completed",(string?)json["cleanup_outcome"]); Assert.Null(json["Status"]);
	}

	[Fact] public async Task TriggerNotReachedOnPatchedMethodIsTruthfulAndBestEffortQualified() {
		var host=new Host(Stop()) { Patched=true }; var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.trigger_not_reached,result.Status.ActionOutcome); Assert.Contains("best-effort",result.Error); Assert.Equal(CleanupOutcome.not_required,result.Status.CleanupOutcome);
	}

	[Fact] public async Task UnsafePointIsReachedNotEvaluable() {
		var result=await Run(new Host(Stop(new AtomicActionEvaluationProbe(AtomicActionEvaluationBlocker.unsafe_point))),new ActionImpl(),Request()); Assert.Equal(ActionOutcome.reached_not_evaluable,result.Status.ActionOutcome);
	}

	[Fact] public async Task UndeclaredNearbySlotIsRejected() {
		var result=await Run(new Host(Stop(offset:4)),new ActionImpl(),Request()); Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome); Assert.Null(result.UsedSlot);
	}

	[Fact] public async Task DeclaredNearbySlotIsRecorded() {
		var request=Request(); request.NearbyOffsets=new uint[]{4}; var host=new Host(Stop(offset:4));
		var result=await Run(host,new ActionImpl(),request); Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome); Assert.Equal((uint)4,result.UsedSlot!.IlOffset);
	}

	/// <summary>The used slot is the one execution bound, not the first offset the caller declared.</summary>
	[Fact] public async Task StopAtASecondDeclaredOffsetNamesThatOffset() {
		var request=Request(); request.NearbyOffsets=new uint[]{4,9,15};
		var result=await Run(new Host(Stop(offset:9)),new ActionImpl(),request);
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome); Assert.Equal((uint)9,result.UsedSlot!.IlOffset);
	}

	[Fact] public async Task StopAtNoDeclaredOffsetIsNotAttributedToOne() {
		var request=Request(); request.NearbyOffsets=new uint[]{4,9};
		var result=await Run(new Host(Stop(offset:11)),new ActionImpl(),request);
		Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome); Assert.Null(result.UsedSlot);
	}

	[Fact] public async Task ActionFailureCanBePotentiallySideEffecting() {
		var action=new ActionImpl { Execution=new AtomicActionExecution { Completed=false,MayHaveExecuted=true,Error="failed" } }; var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal(ActionOutcome.action_failed,result.Status.ActionOutcome); Assert.True(result.Status.ActionMayHaveExecuted); Assert.Equal(AtomicActionStateMachine.StatusOperation,result.Status.ReconciliationOperation);
	}

	[Fact] public async Task ActionExceptionIsActionFailure() {
		var result=await Run(new Host(Stop()),new ActionImpl { ExecuteError=new InvalidOperationException("boom") },Request());
		Assert.Equal(ActionOutcome.action_failed,result.Status.ActionOutcome); Assert.True(result.Status.ActionMayHaveExecuted);
	}

	[Fact] public async Task VerificationFailurePreservesSuccessfulAction() {
		var action=new ActionImpl { Verification=new AtomicActionVerification { Verified=false,Evidence="mismatch",Error="wrong" } }; var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal(ActionOutcome.verification_failed,result.Status.ActionOutcome); Assert.True(result.Status.ActionMayHaveExecuted); Assert.Equal("mismatch",result.VerificationEvidence);
	}

	[Fact] public async Task VerificationExceptionIsVerificationFailure() {
		var result=await Run(new Host(Stop()),new ActionImpl { VerifyError=new InvalidOperationException("verify boom") },Request());
		Assert.Equal(ActionOutcome.verification_failed,result.Status.ActionOutcome); Assert.True(result.Status.ActionMayHaveExecuted);
	}

	[Fact] public async Task ActionSuccessAndCleanupFailureAreIndependent() {
		var host=new Host(Stop()); host.Breakpoint.ReleaseError=new InvalidOperationException("engine cleanup"); var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome); Assert.Equal(CleanupOutcome.failed,result.Status.CleanupOutcome); Assert.True(result.Status.ActionMayHaveExecuted); Assert.True(host.HandlesReleased);
	}

	[Theory]
	[InlineData(InterruptionReason.cancelled)] [InlineData(InterruptionReason.target_exited)] [InlineData(InterruptionReason.appdomain_unloaded)]
	[InlineData(InterruptionReason.ui_shutdown)] [InlineData(InterruptionReason.dispatcher_degraded)]
	public async Task TypedInterruptionsRemainOrthogonal(InterruptionReason reason) {
		var host=new Host(Stop()) { WaitError=new AtomicActionInterruptedException(reason,reason.ToString()) }; var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.trigger_not_reached,result.Status.ActionOutcome); Assert.Equal(reason,result.Status.InterruptionReason); Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
	}

	[Fact] public async Task TimeoutUsesSameCleanupPath() {
		var request=Request(); request.DeadlineUtc=DateTime.UtcNow.AddMilliseconds(80); var host=new Host(Stop()) { WaitUntilCancellation=true };
		var result=await Run(host,new ActionImpl(),request); Assert.Equal(InterruptionReason.timeout,result.Status.InterruptionReason); Assert.True(host.Breakpoint.Released);
	}

	[Fact] public async Task DisconnectUsesSameCleanupPath() {
		using var disconnected=new CancellationTokenSource(); var host=new Host(Stop()) { OnContinue=disconnected.Cancel,WaitUntilCancellation=true };
		var result=await Run(host,new ActionImpl(),Request(),disconnected.Token); Assert.Equal(InterruptionReason.client_disconnected,result.Status.InterruptionReason); Assert.True(host.Breakpoint.Released);
	}

	[Fact] public async Task ExternalDebuggerActionIsNotAutomaticallyResumed() {
		using var leases=new ActionLeaseCoordinator(); var host=new Host(Stop()) { WaitUntilCancellation=true }; var request=Request(); request.ResumePolicy=AtomicActionResumePolicy.resume;
		var run=new AtomicActionStateMachine(leases,host).RunAsync(request,new ActionImpl(),CancellationToken.None,CancellationToken.None);
		await host.Continued.Task; Assert.True(leases.ReportExternalDebuggerAction(42,"ui_pause")); var result=await run;
		Assert.Equal(InterruptionReason.external_debugger_action,result.Status.InterruptionReason); Assert.Equal(0,host.ResumeCalls);
	}

	[Fact] public async Task AmbiguousResumeNamesRecoveryOperation() {
		var request=Request(); request.ResumePolicy=AtomicActionResumePolicy.resume; var host=new Host(Stop()) { ResumeError=new InvalidOperationException("unknown") };
		var result=await Run(host,new ActionImpl(),request); Assert.Equal(CleanupOutcome.ambiguous,result.Status.CleanupOutcome); Assert.Equal(AtomicActionStateMachine.StatusOperation,result.Status.ReconciliationOperation);
	}

	[Fact] public async Task AppDomainReplacementIsClassifiedBeforeAction() {
		var host=new Host(new AtomicActionStop("runtime","new-domain",42,"thread","target.dll",0x06000001,3,AtomicActionEvaluationProbe.Clear)); var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(InterruptionReason.appdomain_unloaded,result.Status.InterruptionReason);
	}

	// Defect 1: the lease used to release itself at its deadline and at owner disconnect, so the machine's
	// own cleanup-time resume threw ObjectDisposedException through a lease that was already gone. Both
	// existing cleanup tests left ResumePolicy at preserve_stop, so neither could see it.
	[Fact] public async Task TimeoutStillResumesTheTargetThroughItsOwnLease() {
		var request=Request(); request.DeadlineUtc=DateTime.UtcNow.AddMilliseconds(120); request.ResumePolicy=AtomicActionResumePolicy.resume;
		var host=new Host(Stop()) { WaitUntilCancellation=true };
		var result=await Run(host,new ActionImpl(),request);
		Assert.Equal(InterruptionReason.timeout,result.Status.InterruptionReason);
		Assert.Equal(1,host.ResumeCalls); Assert.True(host.Resumed);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
		Assert.True(result.FinalDebuggerState.IsRunning);
	}

	[Fact] public async Task DisconnectStillResumesTheTargetThroughItsOwnLease() {
		using var disconnected=new CancellationTokenSource();
		var request=Request(); request.ResumePolicy=AtomicActionResumePolicy.resume;
		var host=new Host(Stop()) { OnContinue=disconnected.Cancel,WaitUntilCancellation=true };
		var result=await Run(host,new ActionImpl(),request,disconnected.Token);
		Assert.Equal(InterruptionReason.client_disconnected,result.Status.InterruptionReason);
		Assert.Equal(1,host.ResumeCalls); Assert.True(host.Resumed);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
	}

	// Defect 7: under complete_on_disconnect the client lifetime is deliberately not linked, so a
	// disconnected-then-timed-out action must not report the one classification that policy suppresses.
	[Fact] public async Task DisconnectedThenTimedOutUnderCompleteOnDisconnectIsATimeout() {
		using var disconnected=new CancellationTokenSource();
		var request=Request(); request.DisconnectPolicy=AtomicActionInterruptionPolicy.complete_on_disconnect; request.DeadlineUtc=DateTime.UtcNow.AddMilliseconds(150);
		var host=new Host(Stop()) { OnContinue=disconnected.Cancel,WaitUntilCancellation=true };
		var result=await Run(host,new ActionImpl(),request,disconnected.Token);
		Assert.True(disconnected.IsCancellationRequested);
		Assert.Equal(InterruptionReason.timeout,result.Status.InterruptionReason);
	}

	// Defect 6: a genuine cleanup failure is the stronger fact and an unknown final state must not erase it.
	[Fact] public async Task CleanupFailureSurvivesAFailedFinalStateRead() {
		var host=new Host(Stop()); host.Breakpoint.ReleaseError=new InvalidOperationException("engine cleanup"); host.FinalStateError=new InvalidOperationException("state unknown");
		var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(CleanupOutcome.failed,result.Status.CleanupOutcome);
		Assert.Contains("engine cleanup",result.Error); Assert.Contains("state unknown",result.Error);
	}

	// dispatcher_degraded's producer is the RpcException the dispatcher raises, not a fake throwing the
	// interruption directly.
	[Fact] public async Task UnavailableDispatcherIsReportedAsDispatcherDegraded() {
		var host=new Host(Stop()) { WaitError=new RpcException("dispatcher_unavailable","The debugger thread is gone.") };
		var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(InterruptionReason.dispatcher_degraded,result.Status.InterruptionReason);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
	}

	// Item 10: an action can report on undoing itself, and that report merges by severity.
	[Fact] public async Task ActionRollbackIsAskedForOnEveryPathThatEnteredTheAction() {
		var action=new ActionImpl { Verification=new AtomicActionVerification { Verified=false,Error="mismatch" },Cleanup=new AtomicActionCleanup { Outcome=CleanupOutcome.completed,Evidence="unpatched" } };
		var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal(ActionOutcome.verification_failed,result.Status.ActionOutcome);
		Assert.True(action.CleanupSeen!.RollbackRecommended);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
		Assert.Equal("unpatched",result.CleanupEvidence);
	}

	[Fact] public async Task ActionRollbackFailureIsVisibleInCleanupOutcome() {
		var action=new ActionImpl { Cleanup=new AtomicActionCleanup { Outcome=CleanupOutcome.failed,Error="the probe is still resident" } };
		var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal(CleanupOutcome.failed,result.Status.CleanupOutcome);
		Assert.Contains("the probe is still resident",result.Error);
		Assert.Equal(AtomicActionStateMachine.StatusOperation,result.Status.ReconciliationOperation);
	}

	[Fact] public async Task ASuccessfulRollbackNeverMasksAFailedBreakpointRelease() {
		var host=new Host(Stop()); host.Breakpoint.ReleaseError=new InvalidOperationException("engine cleanup");
		var result=await Run(host,new ActionImpl { Cleanup=new AtomicActionCleanup { Outcome=CleanupOutcome.completed } },Request());
		Assert.Equal(CleanupOutcome.failed,result.Status.CleanupOutcome);
	}

	[Fact] public async Task AnActionThatNeverRanIsNeverAskedToCleanUp() {
		var action=new ActionImpl();
		var result=await Run(new Host(Stop()) { Patched=true },action,Request());
		Assert.Null(action.CleanupSeen);
		Assert.Equal(CleanupOutcome.not_required,result.Status.CleanupOutcome);
	}

	[Fact] public async Task ARollbackThatOverrunsItsWindowIsAmbiguousRatherThanAbandoned() {
		var action=new ActionImpl { CleanupHangs=true };
		var result=await Run(new Host(Stop()),action,Request(),budget:TimeSpan.FromMilliseconds(120));
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal(CleanupOutcome.ambiguous,result.Status.CleanupOutcome);
		Assert.Contains("bounded cleanup window",result.Error);
	}

	// Item 11: the action names the operation that reconciles what it left behind.
	[Fact] public async Task AnActionSuppliesItsOwnReconciliationOperation() {
		var action=new ActionImpl { ReconciliationOperation="get_probe_state",Execution=new AtomicActionExecution { Completed=false,MayHaveExecuted=true,Error="install failed" } };
		var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal("get_probe_state",result.Status.ReconciliationOperation);
	}

	[Fact] public async Task ACleanupCanOverrideTheReconciliationOperation() {
		var action=new ActionImpl { ReconciliationOperation="get_probe_state",Cleanup=new AtomicActionCleanup { Outcome=CleanupOutcome.ambiguous,ReconciliationOperation="check_probe_health" } };
		var result=await Run(new Host(Stop()),action,Request());
		Assert.Equal("check_probe_health",result.Status.ReconciliationOperation);
	}

	/// <summary>The stop names the module dnSpy resolved; the request names what the caller typed.</summary>
	[Fact] public async Task AResolvedModulePathIsStillTheRequestedExactTarget() {
		var host=new Host(new AtomicActionStop("runtime","domain",42,"thread",@"C:\build\out\target.dll",0x06000001,3,AtomicActionEvaluationProbe.Clear));
		var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal((uint)3,result.UsedSlot!.IlOffset);
	}

	/// <summary>Nothing over RPC reports a runtime guid or an AppDomain id, so requiring both made a valid
	/// request impossible to construct. Omitted means unconstrained; supplied still binds.</summary>
	[Fact] public async Task AnOmittedRuntimeAndAppDomainAreUnconstrained() {
		var request=Request(); request.RuntimeId=""; request.AppDomainId="";
		var result=await Run(new Host(Stop()),new ActionImpl(),request);
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal(InterruptionReason.none,result.Status.InterruptionReason);
	}

	// "Bounded cleanup" was not bounded: both releases were passed CancellationToken.None, and
	// OwnedBreakpoint.ReleaseAsync waits for the debugger's own removal callback. A wedged engine or
	// dispatcher parked the machine before the action rollback, the final resume and the result itself, so
	// run_atomic_action stayed in progress forever and permanently burned its action_id. Both fakes here
	// ignore the token, which is the case that actually hangs and the only one that proves the bound.
	[Fact] public async Task ABreakpointReleaseThatNeverCompletesStillProducesATerminalResult() {
		var request=Request(); request.ResumePolicy=AtomicActionResumePolicy.resume;
		var host=new Host(Stop()); host.Breakpoint.ReleaseHangs=true;
		var action=new ActionImpl();
		var started=DateTime.UtcNow;
		var result=await Run(host,action,request,releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.True(DateTime.UtcNow-started<TimeSpan.FromSeconds(10));
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal(CleanupOutcome.ambiguous,result.Status.CleanupOutcome);
		Assert.Contains("owned breakpoint release did not finish",result.Error);
		Assert.Equal(AtomicActionStateMachine.StatusOperation,result.Status.ReconciliationOperation);
		// The reserves are what this bound buys: the rollback and the final resume still happen.
		Assert.NotNull(action.CleanupSeen);
		Assert.Equal(1,host.ResumeCalls); Assert.True(host.Resumed);
	}

	[Fact] public async Task AHandleReleaseThatNeverCompletesIsReportedRatherThanWaitedOut() {
		var host=new Host(Stop()) { HandleReleaseHangs=true };
		var result=await Run(host,new ActionImpl(),Request(),releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.Equal(CleanupOutcome.ambiguous,result.Status.CleanupOutcome);
		Assert.Contains("temporary-handle release did not finish",result.Error);
	}

	/// <summary>A release that overruns is ambiguous; a release that is known to have failed still reports
	/// the stronger fact.</summary>
	[Fact] public async Task AFailedReleaseIsStillStrongerThanAnOverrunOne() {
		var host=new Host(Stop()) { HandleReleaseHangs=true }; host.Breakpoint.ReleaseError=new InvalidOperationException("engine cleanup");
		var result=await Run(host,new ActionImpl(),Request(),releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.Equal(CleanupOutcome.failed,result.Status.CleanupOutcome);
	}

	/// <summary>The same unbounded-release hang as the two above, in the place the fix for those did not
	/// reach: the nearby-slot search released every breakpoint that failed to bind with
	/// CancellationToken.None, so a wedged engine parked the machine before the action ever ran - no
	/// result, no resume, and the action_id burned. Both releases here ignore the token, which is the case
	/// that hangs; without the bound this test does not fail, it never returns.</summary>
	[Fact] public async Task AnUnboundBreakpointReleaseDuringTheNearbySlotSearchCannotHangTheRun() {
		var request=Request(); request.NearbyOffsets=new uint[]{4}; request.ResumePolicy=AtomicActionResumePolicy.resume;
		var host=new Host(Stop()); host.Breakpoint.Binds=false; host.Breakpoint.ReleaseHangs=true;
		var started=DateTime.UtcNow;
		var result=await Run(host,new ActionImpl(),request,releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.True(DateTime.UtcNow-started<TimeSpan.FromSeconds(10),"The nearby-slot search waited out an unbounded release.");
		// The exact slot and the one declared nearby slot were both tried, and both releases were abandoned.
		Assert.Equal(2,host.Breakpoint.ReleaseCalls);
		Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome);
		// An abandoned release may have left the breakpoint installed, so the run is not clean cleanup.
		Assert.Equal(CleanupOutcome.ambiguous,result.Status.CleanupOutcome);
		Assert.Contains("nearby-slot search did not finish",result.Error);
		Assert.Equal(AtomicActionStateMachine.StatusOperation,result.Status.ReconciliationOperation);
	}

	/// <summary>The search's releases share one bound, and it must not eat the reserves: a search that
	/// overran still leaves the machine able to resume the target and read its final state.</summary>
	[Fact] public async Task ASearchThatOverrunsStillResumesTheTarget() {
		var request=Request(); request.NearbyOffsets=new uint[]{4,9,15}; request.ResumePolicy=AtomicActionResumePolicy.resume;
		var host=new Host(Stop()); host.Breakpoint.Binds=false; host.Breakpoint.ReleaseHangs=true;
		var result=await Run(host,new ActionImpl(),request,releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.Equal(4,host.Breakpoint.ReleaseCalls);
		Assert.Equal(1,host.ResumeCalls); Assert.True(host.Resumed);
		Assert.NotNull(result.FinalDebuggerState);
	}

	/// <summary>A release that completes during the search is still ordinary completed cleanup, so the
	/// bound does not turn every search into an ambiguous one.</summary>
	[Fact] public async Task ASearchWhoseReleasesCompleteIsNotReportedAsAmbiguous() {
		var request=Request(); request.NearbyOffsets=new uint[]{4};
		var host=new Host(Stop()); host.Breakpoint.Binds=false;
		var result=await Run(host,new ActionImpl(),request,releaseBudget:TimeSpan.FromMilliseconds(150));
		Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome);
		Assert.Equal(CleanupOutcome.completed,result.Status.CleanupOutcome);
	}

	static async Task<AtomicActionResult> Run(Host host,ActionImpl action,AtomicActionRequest request,CancellationToken client=default,TimeSpan? budget=null,TimeSpan? releaseBudget=null) {
		using var leases=new ActionLeaseCoordinator();
		return await new AtomicActionStateMachine(leases,host,budget,releaseBudget).RunAsync(request,action,client,CancellationToken.None);
	}

	sealed class ActionImpl : IAtomicAction {
		public string Kind=>"test"; public AtomicActionExecution Execution=new() { Completed=true,MayHaveExecuted=true,Evidence="executed" }; public AtomicActionVerification Verification=new() { Verified=true,Evidence="verified" }; public Exception? ExecuteError; public Exception? VerifyError;
		public string? ReconciliationOperation { get; set; }
		public AtomicActionCleanup? Cleanup; public Exception? CleanupError; public bool CleanupHangs; public AtomicActionCleanupContext? CleanupSeen;
		public Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken token)=>ExecuteError is null?Task.FromResult(Execution):Task.FromException<AtomicActionExecution>(ExecuteError);
		public Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken token)=>VerifyError is null?Task.FromResult(Verification):Task.FromException<AtomicActionVerification>(VerifyError);
		public async Task<AtomicActionCleanup> CleanupAsync(AtomicActionCleanupContext context,CancellationToken token) {
			CleanupSeen=context;
			if(CleanupHangs) await Task.Delay(Timeout.Infinite,token);
			if(CleanupError is not null) throw CleanupError;
			return Cleanup ?? new AtomicActionCleanup();
		}
	}

	sealed class Breakpoint : IAtomicActionBreakpoint {
		public Guid OwnerToken { get; }=Guid.NewGuid(); public string? BindError=>null; public bool Released; public Exception? ReleaseError; public bool ReleaseHangs;
		/// <summary>False makes every bind attempt fail, which is what drives the nearby-slot search loop.</summary>
		public bool Binds=true; public int ReleaseCalls;
		public Task<bool> WaitBoundAsync(CancellationToken token)=>Task.FromResult(Binds);
		// Deliberately ignores the token: OwnedBreakpoint.ReleaseAsync waits on the debugger's removal
		// callback, and a wedged engine never delivers it, so honouring cancellation is exactly what cannot
		// be assumed here. The machine's bound has to hold without the callee's cooperation.
		public Task ReleaseAsync(CancellationToken token) { Released=true; ReleaseCalls++; if(ReleaseHangs) return new TaskCompletionSource<bool>().Task; return ReleaseError is null?Task.CompletedTask:Task.FromException(ReleaseError); }
		public void Dispose() { }
	}

	sealed class Host : IAtomicActionHost {
		readonly AtomicActionStop stop; public readonly Breakpoint Breakpoint=new(); public bool Patched; public Exception? WaitError; public bool WaitUntilCancellation; public Action? OnContinue; public Exception? ResumeError; public Exception? FinalStateError; public int ResumeCalls; public bool Resumed; public bool HandlesReleased; public TaskCompletionSource<bool> Continued=new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Host(AtomicActionStop stop)=>this.stop=stop; public long CaptureEventCursor()=>7;
		public PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot)=>Patched?PatchedTargetState.patched:PatchedTargetState.not_patched;
		public Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> hit,CancellationToken token)=>Task.FromResult<IAtomicActionBreakpoint>(Breakpoint);
		public Task ContinueAsync(Action<Action> authorize,CancellationToken token) { authorize(()=>{}); OnContinue?.Invoke(); Continued.TrySetResult(true); return Task.CompletedTask; }
		public async Task<AtomicActionStop> WaitForOwnedStopAsync(Guid owner,long cursor,CancellationToken token) { if(WaitError is not null) throw WaitError; if(WaitUntilCancellation) await Task.Delay(Timeout.Infinite,token); return stop; }
		// The production host answers this from NearbySlotSelector, so the fake does too. A fake that
		// returned a canned slot is what made DeclaredNearbySlotIsRecorded vacuous while the production
		// selector ignored the stop entirely and always named the first declared offset.
		public Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? value,CancellationToken token)=>Task.FromResult(NearbySlotSelector.Select(request,value));
		public bool HandleReleaseHangs;
		public Task ReleaseTemporaryHandlesAsync(CancellationToken token) { HandlesReleased=true; if(HandleReleaseHangs) return new TaskCompletionSource<bool>().Task; return Task.CompletedTask; }
		// authorize() runs the machine's lease.ExecuteMutation, so a lease that released itself while its
		// owner was still cleaning up surfaces here as an ObjectDisposedException, exactly as in production.
		public Task ResumeAsync(Action<Action> authorize,CancellationToken token) { ResumeCalls++; if(ResumeError is not null) throw ResumeError; authorize(()=>Resumed=true); return Task.CompletedTask; }
		public Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken token)=>FinalStateError is null
			?Task.FromResult(new AtomicActionFinalState { SessionActive=true,ProcessActive=true,IsPaused=!Resumed,IsRunning=Resumed })
			:Task.FromException<AtomicActionFinalState>(FinalStateError);
	}
}
