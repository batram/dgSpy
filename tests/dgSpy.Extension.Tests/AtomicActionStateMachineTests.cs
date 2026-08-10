using dgSpy.Extension.Debugger;
using dgSpy.Extension.Debugger.AtomicActions;
using HookLab.Contracts;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class AtomicActionStateMachineTests {
	static AtomicActionRequest Request() => new() { ActionId=Guid.NewGuid().ToString("N"),ActionName="test",ProcessId=42,RuntimeId="runtime",AppDomainId="domain",Module="target.dll",MethodToken=0x06000001,IlOffset=3,DeadlineUtc=DateTime.UtcNow.AddSeconds(10) };
	static AtomicActionStop Stop(bool evaluable=true,uint offset=3) => new("runtime","domain",42,"thread","target.dll",0x06000001,offset,evaluable);

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
		var result=await Run(new Host(Stop(false)),new ActionImpl(),Request()); Assert.Equal(ActionOutcome.reached_not_evaluable,result.Status.ActionOutcome);
	}

	[Fact] public async Task UndeclaredNearbySlotIsRejected() {
		var result=await Run(new Host(Stop(offset:4)),new ActionImpl(),Request()); Assert.Equal(ActionOutcome.nearby_slot_not_found,result.Status.ActionOutcome); Assert.Null(result.UsedSlot);
	}

	[Fact] public async Task DeclaredNearbySlotIsRecorded() {
		var request=Request(); request.NearbyOffsets=new uint[]{4}; var host=new Host(Stop(offset:4)) { Nearby=new AtomicActionSlot("target.dll",0x06000001,4) };
		var result=await Run(host,new ActionImpl(),request); Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome); Assert.Equal((uint)4,result.UsedSlot!.IlOffset);
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
		var host=new Host(new AtomicActionStop("runtime","new-domain",42,"thread","target.dll",0x06000001,3,true)); var result=await Run(host,new ActionImpl(),Request());
		Assert.Equal(InterruptionReason.appdomain_unloaded,result.Status.InterruptionReason);
	}

	static async Task<AtomicActionResult> Run(Host host,ActionImpl action,AtomicActionRequest request,CancellationToken client=default) { using var leases=new ActionLeaseCoordinator(); return await new AtomicActionStateMachine(leases,host).RunAsync(request,action,client,CancellationToken.None); }

	sealed class ActionImpl : IAtomicAction {
		public string Kind=>"test"; public AtomicActionExecution Execution=new() { Completed=true,MayHaveExecuted=true,Evidence="executed" }; public AtomicActionVerification Verification=new() { Verified=true,Evidence="verified" }; public Exception? ExecuteError; public Exception? VerifyError;
		public Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken token)=>ExecuteError is null?Task.FromResult(Execution):Task.FromException<AtomicActionExecution>(ExecuteError);
		public Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken token)=>VerifyError is null?Task.FromResult(Verification):Task.FromException<AtomicActionVerification>(VerifyError);
	}

	sealed class Breakpoint : IAtomicActionBreakpoint {
		public Guid OwnerToken { get; }=Guid.NewGuid(); public string? BindError=>null; public bool Released; public Exception? ReleaseError;
		public Task<bool> WaitBoundAsync(CancellationToken token)=>Task.FromResult(true);
		public Task ReleaseAsync(CancellationToken token) { Released=true; return ReleaseError is null?Task.CompletedTask:Task.FromException(ReleaseError); }
		public void Dispose() { }
	}

	sealed class Host : IAtomicActionHost {
		readonly AtomicActionStop stop; public readonly Breakpoint Breakpoint=new(); public bool Patched; public AtomicActionSlot? Nearby; public Exception? WaitError; public bool WaitUntilCancellation; public Action? OnContinue; public Exception? ResumeError; public int ResumeCalls; public bool HandlesReleased; public TaskCompletionSource<bool> Continued=new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Host(AtomicActionStop stop)=>this.stop=stop; public long CaptureEventCursor()=>7;
		public PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot)=>Patched?PatchedTargetState.patched:PatchedTargetState.not_patched;
		public Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> hit,CancellationToken token)=>Task.FromResult<IAtomicActionBreakpoint>(Breakpoint);
		public Task ContinueAsync(Action<Action> authorize,CancellationToken token) { authorize(()=>{}); OnContinue?.Invoke(); Continued.TrySetResult(true); return Task.CompletedTask; }
		public async Task<AtomicActionStop> WaitForOwnedStopAsync(Guid owner,long cursor,CancellationToken token) { if(WaitError is not null) throw WaitError; if(WaitUntilCancellation) await Task.Delay(Timeout.Infinite,token); return stop; }
		public Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? value,CancellationToken token)=>Task.FromResult(Nearby);
		public Task ReleaseTemporaryHandlesAsync(CancellationToken token) { HandlesReleased=true; return Task.CompletedTask; }
		public Task ResumeAsync(Action<Action> authorize,CancellationToken token) { ResumeCalls++; if(ResumeError is not null) throw ResumeError; authorize(()=>{}); return Task.CompletedTask; }
		public Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken token)=>Task.FromResult(new AtomicActionFinalState { SessionActive=true,ProcessActive=true,IsPaused=true });
	}
}
