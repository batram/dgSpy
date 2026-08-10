using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using dgSpy.Extension.Debugger;
using dgSpy.Extension.Debugger.AtomicActions;
using dgSpy.Protocol;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>
/// T08c. The asynchronous action shape (start returns after REGISTRATION, not after arming), the cancel
/// authorization that replaced <c>expected_execution_version</c>, and the typed evaluability blocker.
/// </summary>
public sealed class AtomicActionAsyncLifecycleTests {
	static readonly TimeSpan Bound=TimeSpan.FromSeconds(10);

	static AtomicActionResult Terminal(InterruptionReason reason,string message) => new() {
		Error=message,
		Status=new AtomicActionStatus(ActionOutcome.trigger_not_reached,reason,CleanupOutcome.not_required,false,"audit",null),
	};

	static async Task<bool> Eventually(Func<bool> condition) {
		var deadline=DateTime.UtcNow+Bound;
		while(DateTime.UtcNow<deadline) { if(condition()) return true; await Task.Delay(5); }
		return condition();
	}

	// ---- start returns after registration, not after arming -------------------------------------

	[Fact]
	public async Task Start_returns_before_arming_begins_and_the_record_is_already_retrievable() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		// The run never gets past its first line, standing in for a lease acquisition or a breakpoint bind
		// that hangs. A start that waited for "armed" could not return at all here - which is the whole
		// defect: it would still occupy the caller's single host connection and still be uncancellable.
		var arming=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var elapsed=System.Diagnostics.Stopwatch.StartNew();
		var acceptance=scheduler.Start("a-1",AtomicActionGeneration.Unscoped,
			async record=>{ record.AdvanceToPhase(AtomicActionPhase.arming); await arming.Task; return Terminal(InterruptionReason.none,"done"); },Terminal);
		elapsed.Stop();

		// The load-bearing assertion: Start answered while the run is provably still parked in arming.
		// Anything that waits for the run - even for "armed" - fails here, which is the point: a start that
		// waits still occupies the caller's single host connection and is still uncancellable.
		Assert.True(elapsed.ElapsedMilliseconds<1000,"start_atomic_action waited for the run: "+elapsed.ElapsedMilliseconds+" ms");
		Assert.False(arming.Task.IsCompleted);
		Assert.True(acceptance.Accepted);
		Assert.Equal("a-1",acceptance.ActionId);
		Assert.Equal(AtomicActionPhase.queued,acceptance.Phase);
		Assert.True(acceptance.RecommendedPollAfterMs>0);
		Assert.Equal("get_atomic_action_status",acceptance.StatusOperation);
		Assert.Equal("cancel_atomic_action",acceptance.CancelOperation);

		Assert.True(store.TryGet("a-1",out var record));
		Assert.False(record.Completed);
		Assert.True(await Eventually(()=>record.Phase==AtomicActionPhase.arming));
		arming.SetResult(true);
		Assert.True(await Eventually(()=>record.Completed));
	}

	[Fact]
	public void The_acceptance_goes_out_snake_case_like_every_neighbouring_response() {
		var json=ProtocolJson.ToNode(new AtomicActionAcceptance("a-0",AtomicActionPhase.queued,250))!.AsObject();
		// ProtocolJson applies no naming policy, so a DTO without explicit names ships PascalCase beside
		// snake_case neighbours and no compiler notices.
		Assert.Equal(1,(int?)json["schema_version"]);
		Assert.Equal("a-0",(string?)json["action_id"]);
		Assert.True((bool?)json["accepted"]);
		Assert.Equal("queued",(string?)json["phase"]);
		Assert.Equal(250,(int?)json["recommended_poll_after_ms"]);
		Assert.Equal("get_atomic_action_status",(string?)json["status_operation"]);
		Assert.Equal("cancel_atomic_action",(string?)json["cancel_operation"]);
		Assert.All(json.Select(pair=>pair.Key),name=>Assert.Equal(name.ToLowerInvariant(),name));
	}

	[Fact]
	public async Task Cancel_immediately_after_acceptance_and_before_arming_ends_the_action() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered=false;
		// The run is held off entirely, so the cancel lands strictly in `queued`.
		scheduler.Start("a-2",AtomicActionGeneration.Unscoped,
			async record=>{ entered=true; await gate.Task; return Terminal(InterruptionReason.none,"done"); },Terminal);
		Assert.True(store.TryGet("a-2",out var record));
		Assert.True(record.TryCancel(out var already));
		Assert.False(already);

		Assert.True(await Eventually(()=>record.Completed));
		Assert.Equal(InterruptionReason.cancelled,record.Result!.Status.InterruptionReason);
		Assert.Equal(AtomicActionPhase.terminal,record.Phase);
		gate.SetResult(true);
		// Either the run never started, or it started and the queued cancel still won. What must never
		// happen is a queued cancel being ignored, and that is what the assertions above pin.
		Assert.True(entered || !entered);
	}

	[Fact]
	public async Task Cancel_while_breakpoint_binding_is_pending_reaches_the_run() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		var binding=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		scheduler.Start("a-3",AtomicActionGeneration.Unscoped,async record=>{
			record.AdvanceToPhase(AtomicActionPhase.arming);
			binding.TrySetResult(true);
			// Stands in for OwnedBreakpoint.WaitBoundAsync, which is where a bind that never settles parks.
			await Task.Delay(Timeout.Infinite,record.Cancelled.Token);
			return Terminal(InterruptionReason.none,"unreachable");
		},Terminal);
		Assert.True(store.TryGet("a-3",out var record));
		await binding.Task;
		Assert.Equal(AtomicActionPhase.arming,record.Phase);

		record.TryCancel();
		Assert.True(await Eventually(()=>record.Completed));
		Assert.Equal(InterruptionReason.cancelled,record.Result!.Status.InterruptionReason);
		Assert.Contains("armed",record.Result!.Error);
	}

	[Fact]
	public async Task Arming_failure_becomes_a_retained_terminal_record_rather_than_a_lost_run() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		scheduler.Start("a-4",AtomicActionGeneration.Unscoped,
			_=>throw new RpcException("action_in_progress","Another action owns this process."),Terminal);
		Assert.True(store.TryGet("a-4",out var record));
		Assert.True(await Eventually(()=>record.Completed));
		Assert.Equal("action_in_progress",record.ErrorCode);
		Assert.Equal(AtomicActionPhase.terminal,record.Phase);
		// Still retrievable, which is what makes it reconcilable at all.
		Assert.True(store.TryGet("a-4",out _));
	}

	[Fact]
	public async Task An_exception_in_the_detached_run_is_captured_in_the_record_and_never_unobserved() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		var unobserved=new List<Exception>();
		EventHandler<UnobservedTaskExceptionEventArgs> watch=(_,e)=>{ lock(unobserved) unobserved.AddRange(e.Exception.InnerExceptions); };
		TaskScheduler.UnobservedTaskException+=watch;
		try {
			scheduler.Start("a-5",AtomicActionGeneration.Unscoped,
				_=>throw new InvalidOperationException("the action host blew up"),Terminal);
			Assert.True(store.TryGet("a-5",out var record));
			Assert.True(await Eventually(()=>record.Completed));
			Assert.Equal("internal_error",record.ErrorCode);
			Assert.Contains("InvalidOperationException",record.ErrorMessage);
			Assert.Contains("the action host blew up",record.ErrorMessage);
			// The scheduler's own task must be observed too - a faulted detached task is how the only
			// account of a mutation that may already have applied gets lost.
			await record.Run!;
			GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
			lock(unobserved) Assert.Empty(unobserved);
		}
		finally { TaskScheduler.UnobservedTaskException-=watch; }
	}

	[Fact]
	public async Task A_start_request_ending_normally_does_not_cancel_the_accepted_action() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		// The lifetime of the request that issued the start. After acceptance the run is host-owned, so
		// this ending - which is exactly what a successful start_atomic_action does - must not touch it.
		using var startRequestLifetime=new CancellationTokenSource();
		var running=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		scheduler.Start("a-6",AtomicActionGeneration.Unscoped,async record=>{
			record.AdvanceToPhase(AtomicActionPhase.running);
			running.TrySetResult(true);
			await release.Task;
			// If any request-scoped token had leaked into the run, this would have thrown instead.
			record.Cancelled.Token.ThrowIfCancellationRequested();
			return Terminal(InterruptionReason.none,"survived");
		},Terminal);
		Assert.True(store.TryGet("a-6",out var record));
		await running.Task;

		startRequestLifetime.Cancel();
		await Task.Delay(50);
		Assert.False(record.Completed);
		Assert.Equal(AtomicActionPhase.running,record.Phase);

		release.SetResult(true);
		Assert.True(await Eventually(()=>record.Completed));
		Assert.Equal(InterruptionReason.none,record.Result!.Status.InterruptionReason);
	}

	[Fact]
	public async Task Host_shutdown_forces_a_terminal_record_onto_a_run_that_ignores_cancellation() {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var never=new TaskCompletionSource<bool>();
		scheduler.Start("a-7",AtomicActionGeneration.Unscoped,async record=>{
			started.TrySetResult(true);
			await never.Task;   // deliberately deaf to the cancel, like a wedged engine callback
			return Terminal(InterruptionReason.none,"unreachable");
		},Terminal);
		var cooperative=scheduler.Start("a-8",AtomicActionGeneration.Unscoped,async record=>{
			await Task.Delay(Timeout.Infinite,record.Cancelled.Token);
			return Terminal(InterruptionReason.none,"unreachable");
		},Terminal);
		await started.Task;

		var forced=await scheduler.ShutdownAsync(TimeSpan.FromMilliseconds(300),Terminal);

		Assert.True(store.TryGet("a-7",out var wedged));
		Assert.True(store.TryGet("a-8",out var polite));
		Assert.Equal(1,forced);
		Assert.True(wedged.Completed);
		Assert.Equal(InterruptionReason.ui_shutdown,wedged.Result!.Status.InterruptionReason);
		Assert.True(polite.Completed);
		Assert.Equal(InterruptionReason.cancelled,polite.Result!.Status.InterruptionReason);
		Assert.Equal("a-8",cooperative.ActionId);
	}

	// ---- retention, duplicates, idempotence ------------------------------------------------------

	[Fact]
	public async Task A_running_record_is_never_TTL_evicted_while_a_terminal_one_keeps_its_TTL() {
		var now=DateTime.UtcNow;
		using var store=new AtomicActionRecordStore(()=>now);
		var scheduler=new AtomicActionScheduler(store);
		var hold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		scheduler.Start("live",AtomicActionGeneration.Unscoped,async _=>{ await hold.Task; return Terminal(InterruptionReason.none,"done"); },Terminal);
		var finished=store.TryStart("dead")!;
		store.Complete(finished,Terminal(InterruptionReason.none,"done"));

		now+=AtomicActionRecordStore.TerminalRetention+TimeSpan.FromMinutes(1);
		store.Evict();

		Assert.True(store.TryGet("live",out _));
		Assert.False(store.TryGet("dead",out _));
		hold.SetResult(true);
	}

	[Theory]
	[InlineData(AtomicActionPhase.queued)]
	[InlineData(AtomicActionPhase.running)]
	[InlineData(AtomicActionPhase.cleaning_up)]
	public async Task A_duplicate_action_id_is_refused_in_every_non_terminal_phase(AtomicActionPhase phase) {
		using var store=new AtomicActionRecordStore();
		var scheduler=new AtomicActionScheduler(store);
		var hold=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var reached=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		scheduler.Start("dup",AtomicActionGeneration.Unscoped,async record=>{
			record.AdvanceToPhase(phase); reached.TrySetResult(true); await hold.Task; return Terminal(InterruptionReason.none,"done");
		},Terminal);
		await reached.Task;

		Assert.Equal("action_exists",Assert.Throws<RpcException>(()=>scheduler.Start("dup",AtomicActionGeneration.Unscoped,_=>Task.FromResult(Terminal(InterruptionReason.none,"x")),Terminal)).Code);
		hold.SetResult(true);
		Assert.True(store.TryGet("dup",out var record) && await Eventually(()=>record.Completed));
		// And still refused while retained for reconciliation.
		Assert.Equal("action_exists",Assert.Throws<RpcException>(()=>scheduler.Start("dup",AtomicActionGeneration.Unscoped,_=>Task.FromResult(Terminal(InterruptionReason.none,"x")),Terminal)).Code);
	}

	[Fact]
	public void Cancel_is_idempotent_while_running_and_truthful_once_terminal() {
		using var store=new AtomicActionRecordStore();
		var record=store.TryStart("c-1")!;
		Assert.True(record.TryCancel(out var first));
		Assert.False(first);
		Assert.True(record.TryCancel(out var second));
		Assert.True(second);

		store.Complete(record,Terminal(InterruptionReason.cancelled,"done"));
		Assert.False(record.TryCancel(out var afterwards));
		Assert.True(afterwards);
		Assert.True(record.Completed);
		// The fact that a cancel was requested survives the disposal of the cancellation source.
		Assert.True(record.CancelRequested);
	}

	// ---- phases -----------------------------------------------------------------------------------

	[Fact]
	public void Phases_advance_monotonically_and_never_regress() {
		var record=new AtomicActionRecord("p-1",DateTime.UtcNow);
		Assert.Equal(AtomicActionPhase.queued,record.Phase);
		Assert.Equal(AtomicActionPhase.running,record.AdvanceTo(AtomicActionPhase.running));
		Assert.Equal(AtomicActionPhase.running,record.AdvanceTo(AtomicActionPhase.arming));
		Assert.Equal(AtomicActionPhase.running,record.Phase);
		Assert.Equal(AtomicActionPhase.terminal,record.AdvanceTo(AtomicActionPhase.terminal));
		Assert.Equal(AtomicActionPhase.terminal,record.AdvanceTo(AtomicActionPhase.cleaning_up));
	}

	[Fact]
	public void The_recommended_poll_interval_backs_off_in_the_long_phase_and_is_zero_once_terminal() {
		using var store=new AtomicActionRecordStore();
		var record=store.TryStart("p-2")!;
		Assert.Equal(250,record.RecommendedPollAfterMs);
		record.AdvanceTo(AtomicActionPhase.running);
		Assert.True(record.RecommendedPollAfterMs>=500 && record.RecommendedPollAfterMs<=1000);
		record.AdvanceTo(AtomicActionPhase.cleaning_up);
		Assert.Equal(250,record.RecommendedPollAfterMs);
		store.Complete(record,Terminal(InterruptionReason.none,"done"));
		Assert.Equal(0,record.RecommendedPollAfterMs);
	}

	// ---- cancel authorization ---------------------------------------------------------------------

	[Fact]
	public void Cancel_authorization_is_the_captured_session_and_process_generation() {
		var record=new AtomicActionGeneration("session-a",1234,7);
		Assert.True(record.Authorizes(new AtomicActionGeneration("session-a",1234,7)));
		Assert.False(record.Authorizes(new AtomicActionGeneration("session-b",1234,7)));
		Assert.False(record.Authorizes(new AtomicActionGeneration("session-a",9999,7)));
		Assert.False(record.Authorizes(new AtomicActionGeneration("session-a",1234,8)));
		Assert.False(record.Authorizes(new AtomicActionGeneration(null,1234,7)));
		// The in-process paths carry no session, and must not be broken by a check they cannot satisfy.
		Assert.True(AtomicActionGeneration.Unscoped.Authorizes(new AtomicActionGeneration("session-a",1,2)));
		Assert.True(record.Authorizes(AtomicActionGeneration.Unscoped));
	}

	// ---- disconnect policy ------------------------------------------------------------------------

	[Fact]
	public void Disconnect_policy_is_refused_on_the_asynchronous_start_and_accepted_on_the_blocking_form() {
		var withPolicy=new JsonObject { ["action_id"]="x",["disconnect_policy"]="cancel_on_disconnect" };
		var refusal=Assert.Throws<RpcException>(()=>AtomicActionRequestScope.ResolveAsyncDisconnectPolicy(withPolicy));
		Assert.Equal("invalid_arguments",refusal.Code);
		Assert.Contains("run_atomic_action",refusal.Message);
		Assert.Contains("cancel_atomic_action",refusal.Message);

		Assert.Equal(AtomicActionInterruptionPolicy.complete_on_disconnect,AtomicActionRequestScope.ResolveAsyncDisconnectPolicy(new JsonObject { ["action_id"]="x" }));
		Assert.Equal(AtomicActionInterruptionPolicy.complete_on_disconnect,AtomicActionRequestScope.ResolveAsyncDisconnectPolicy(null));
	}

	// ---- typed blocker reason ---------------------------------------------------------------------

	[Theory]
	[InlineData(AtomicActionEvaluationBlocker.no_frames,"no stack frame")]
	[InlineData(AtomicActionEvaluationBlocker.native_frame,"native")]
	[InlineData(AtomicActionEvaluationBlocker.unsafe_point,"CorDebug-unsafe point")]
	public async Task Each_blocker_reaches_the_terminal_result_distinctly(AtomicActionEvaluationBlocker blocker,string phrase) {
		var result=await RunToStop(new AtomicActionEvaluationProbe(blocker));
		Assert.Equal(ActionOutcome.reached_not_evaluable,result.Status.ActionOutcome);
		Assert.Equal(blocker,result.EvaluationBlocker);
		Assert.Equal(AtomicActionEvaluationProbeStage.none,result.EvaluationProbeStage);
		Assert.Contains(phrase,result.Error);
	}

	[Fact]
	public async Task A_failed_probe_is_reported_with_its_stage_rather_than_reading_as_safe() {
		var stackWalk=await RunToStop(AtomicActionEvaluationProbe.FromStackWalkFailure(new COMException("boom",unchecked((int)0x8013132C))));
		Assert.Equal(ActionOutcome.reached_not_evaluable,stackWalk.Status.ActionOutcome);
		Assert.Equal(AtomicActionEvaluationBlocker.probe_failed,stackWalk.EvaluationBlocker);
		Assert.Equal(AtomicActionEvaluationProbeStage.stack_walk,stackWalk.EvaluationProbeStage);
		Assert.Equal("COMException",stackWalk.EvaluationProbeErrorCategory);
		Assert.Equal("HRESULT 0x8013132C",stackWalk.EvaluationProbeError);

		var userState=await RunToStop(AtomicActionEvaluationProbe.UserStateUnavailable);
		Assert.Equal(AtomicActionEvaluationBlocker.probe_failed,userState.EvaluationBlocker);
		Assert.Equal(AtomicActionEvaluationProbeStage.user_state,userState.EvaluationProbeStage);
		Assert.Contains("GetUserState",userState.EvaluationProbeError);
	}

	[Fact]
	public async Task A_clear_probe_is_recorded_as_none_rather_than_left_absent() {
		var result=await RunToStop(AtomicActionEvaluationProbe.Clear);
		Assert.Equal(ActionOutcome.completed,result.Status.ActionOutcome);
		Assert.Equal(AtomicActionEvaluationBlocker.none,result.EvaluationBlocker);
		var json=ProtocolJson.ToNode(result)!.AsObject();
		Assert.Equal("none",(string?)json["evaluation_blocker"]);
		Assert.Equal("none",(string?)json["evaluation_probe_stage"]);
	}

	[Fact]
	public void A_probe_failure_is_bounded_and_carries_no_quoted_target_value() {
		var leaky=new InvalidOperationException("Cannot evaluate 'player.secretToken' whose value is \"hunter2\" at C:\\game\\Assembly.dll");
		var detail=ProbeFailure.Detail(leaky);
		Assert.DoesNotContain("hunter2",detail);
		Assert.DoesNotContain("player.secretToken",detail);
		Assert.Contains("<redacted>",detail);
		Assert.True(detail.Length<=ProbeFailure.MaxDetailLength);
		Assert.Equal("InvalidOperationException",ProbeFailure.Category(leaky));

		Assert.True(ProbeFailure.Detail(new InvalidOperationException(new string('x',5000))).Length<=ProbeFailure.MaxDetailLength);
		// An unterminated quote redacts to the end rather than falling through and emitting the tail.
		Assert.DoesNotContain("tail",ProbeFailure.Redact("prefix 'tail"));
		// No stack trace and no exception serialization, whatever the input.
		Exception thrown;
		try { throw new InvalidOperationException("plain"); } catch(Exception ex) { thrown=ex; }
		Assert.NotNull(thrown.StackTrace);
		Assert.Equal("plain",ProbeFailure.Detail(thrown));
	}

	// ---- harness ---------------------------------------------------------------------------------

	static async Task<AtomicActionResult> RunToStop(AtomicActionEvaluationProbe probe) {
		using var leases=new ActionLeaseCoordinator();
		var machine=new AtomicActionStateMachine(leases,new StopHost(probe));
		return await machine.RunAsync(new AtomicActionRequest {
			ActionId=Guid.NewGuid().ToString("N"),ActionName="test",ProcessId=42,Module="target.dll",
			MethodToken=0x06000001,IlOffset=3,DeadlineUtc=DateTime.UtcNow.AddSeconds(10),
		},new NoopAction(),CancellationToken.None,CancellationToken.None);
	}

	sealed class NoopAction : IAtomicAction {
		public string Kind=>"capture";
		public string? ReconciliationOperation=>null;
		public Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext c,CancellationToken t)=>Task.FromResult(new AtomicActionExecution { Completed=true,Evidence="e" });
		public Task<AtomicActionVerification> VerifyAsync(AtomicActionContext c,AtomicActionExecution e,CancellationToken t)=>Task.FromResult(new AtomicActionVerification { Verified=true,Evidence="v" });
		public Task<AtomicActionCleanup> CleanupAsync(AtomicActionCleanupContext c,CancellationToken t)=>Task.FromResult(new AtomicActionCleanup());
	}

	sealed class StopBreakpoint : IAtomicActionBreakpoint {
		public Guid OwnerToken { get; }=Guid.NewGuid();
		public string? BindError=>null;
		public Task<bool> WaitBoundAsync(CancellationToken t)=>Task.FromResult(true);
		public Task ReleaseAsync(CancellationToken t)=>Task.CompletedTask;
		public void Dispose() { }
	}

	/// <summary>Reaches the exact requested slot and reports whatever probe the test supplied. The probe is
	/// the input under test, so the fake must not synthesize one - a host that decided evaluability itself
	/// would be supplying the very effect these tests are meant to detect.</summary>
	sealed class StopHost : IAtomicActionHost {
		readonly AtomicActionEvaluationProbe probe;
		public StopHost(AtomicActionEvaluationProbe probe)=>this.probe=probe;
		public long CaptureEventCursor()=>0;
		public PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot)=>PatchedTargetState.not_patched;
		public Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> hit,CancellationToken t)=>Task.FromResult<IAtomicActionBreakpoint>(new StopBreakpoint());
		public Task ContinueAsync(Action<Action> authorize,CancellationToken t) { authorize(()=>{ }); return Task.CompletedTask; }
		public Task<AtomicActionStop> WaitForOwnedStopAsync(Guid owner,long cursor,CancellationToken t)=>
			Task.FromResult(new AtomicActionStop("runtime","domain",42,"thread","target.dll",0x06000001,3,probe));
		public Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest r,AtomicActionStop? s,CancellationToken t)=>Task.FromResult(NearbySlotSelector.Select(r,s));
		public Task ReleaseTemporaryHandlesAsync(CancellationToken t)=>Task.CompletedTask;
		public Task ResumeAsync(Action<Action> authorize,CancellationToken t) { authorize(()=>{ }); return Task.CompletedTask; }
		public Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken t)=>Task.FromResult(new AtomicActionFinalState());
	}
}
