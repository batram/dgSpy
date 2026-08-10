using System.Collections.Concurrent;
using dgSpy.Extension.Debugger;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ActionLeaseCoordinatorTests {
	static ActionLease Acquire(ActionLeaseCoordinator coordinator,int processId=42,CancellationToken ownerLifetime=default) =>
		coordinator.Acquire(processId,"bootstrap probe","action-7",DateTime.UtcNow.AddSeconds(5),"get_action_status","cancel_action",ownerLifetime);

	[Fact]
	public void Conflicting_call_is_blocked_with_recovery_details() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		Assert.True(coordinator.TryGetBlock(42,"continue",out var owner));
		var reason=owner.FormatBlockReason("continue");
		Assert.Contains("bootstrap probe",reason);
		Assert.Contains("action-7",reason);
		Assert.Contains(owner.DeadlineUtc.ToString("O"),reason);
		Assert.Contains("get_action_status",reason);
		Assert.Contains("cancel_action",reason);
	}

	[Fact]
	public void Different_process_is_not_blocked_but_global_mutation_is() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		Assert.False(coordinator.TryGetBlock(43,"continue",out _));
		Assert.True(coordinator.TryGetBlock(null,"breakpoint_mutation",out _));
	}

	[Fact]
	public void Owner_authorization_wraps_one_synchronous_call() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		lease.ExecuteMutation(()=>Assert.False(coordinator.TryGetBlock(42,"continue",out _)));
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
	}

	[Fact]
	public async Task Authorization_does_not_cross_a_marshal_boundary() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		var queued=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		lease.ExecuteMutation(()=>ThreadPool.QueueUserWorkItem(state=>queued.SetResult(coordinator.TryGetBlock(42,"continue",out _))));
		Assert.True(await queued.Task.WaitAsync(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void Direct_engine_transition_truthfully_interrupts_owner() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		ActionLeaseInfo? reported=null;
		string? operation=null;
		coordinator.ExternalDebuggerAction+=(info,value)=>{ reported=info; operation=value; };
		Assert.True(coordinator.ReportExternalDebuggerAction(42,"third_party_continue"));
		Assert.True(lease.ExternalActionCancellation.IsCancellationRequested);
		Assert.Equal("third_party_continue",lease.ExternalActionOperation);
		Assert.Same(lease.Info,reported);
		Assert.Equal("third_party_continue",operation);
		Assert.False(coordinator.ReportExternalDebuggerAction(42,"later_transition"));
		Assert.False(coordinator.ReportExternalDebuggerAction(43,"other_process"));
	}

	[Fact]
	public void Async_authorization_delegate_is_rejected() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		Assert.IsType<ArgumentException>(Record.Exception(()=>lease.ExecuteMutation((Action)(async ()=>await Task.Yield()))));
		Assert.IsType<ArgumentException>(Record.Exception((Action)(()=>{ lease.ExecuteMutation<Task>(()=>Task.CompletedTask); })));
		Assert.IsType<ArgumentException>(Record.Exception((Action)(()=>{ lease.ExecuteMutation<ValueTask>(()=>ValueTask.CompletedTask); })));
		Assert.IsType<ArgumentException>(Record.Exception((Action)(()=>{ lease.ExecuteMutation<object>(()=>Task.CompletedTask); })));
		Assert.IsType<ArgumentException>(Record.Exception((Action)(()=>{ lease.ExecuteMutation<object>(()=>ValueTask.CompletedTask); })));
	}

	[Fact]
	public void Cross_thread_scope_disposal_is_loud() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=Acquire(coordinator);
		var scope=coordinator.Enter(lease);
		Exception? error=null;
		var thread=new Thread(()=>error=Record.Exception(scope.Dispose));
		thread.Start(); thread.Join();
		Assert.IsType<InvalidOperationException>(error);
		scope.Dispose();
	}

	/// <summary>
	/// The deadline ends the lease's right to grant new work; it does not end its exclusion. Until the
	/// owner releases or the bounded window elapses, the process is still owned - see
	/// <see cref="An_expired_lease_still_excludes_everyone_but_its_owner"/> for why that matters.
	/// </summary>
	[Fact]
	public async Task Deadline_expires_the_lease_without_ending_its_exclusion() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=coordinator.Acquire(42,"short","action-8",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
		await Task.Delay(200);
		Assert.True(lease.IsExpired);
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
		lease.Dispose();
		Assert.False(coordinator.TryGetBlock(42,"continue",out _));
	}

	[Fact]
	public void Owner_disconnect_expires_the_lease_without_ending_its_exclusion() {
		using var coordinator=new ActionLeaseCoordinator();
		using var disconnected=new CancellationTokenSource();
		var lease=Acquire(coordinator,ownerLifetime:disconnected.Token);
		disconnected.Cancel();
		Assert.True(lease.IsExpired);
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
		lease.Dispose();
		Assert.False(coordinator.TryGetBlock(42,"continue",out _));
	}

	/// <summary>
	/// A lease that released itself at its deadline made its own owner's cleanup-time resume throw
	/// ObjectDisposedException, so the target was left paused and the caller was told the resume was
	/// ambiguous - for a resume that was never attempted. Expiry now stops granting new work and starts a
	/// bounded window in which the owner keeps authorization and releases explicitly.
	/// </summary>
	[Fact]
	public async Task An_expired_lease_still_authorizes_its_owners_cleanup() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=coordinator.Acquire(42,"short","action-9",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		await Task.Delay(250);
		Assert.True(lease.IsExpired);
		var resumed=false;
		lease.ExecuteMutation(()=>resumed=true);
		Assert.True(resumed);
	}

	/// <summary>
	/// This assertion used to be its own inverse: <c>TryGetBlock</c> short-circuited on <c>IsExpired</c>, so
	/// for the whole five-second cleanup window an ordinary UI or RPC continue, detach or step was
	/// <em>permitted</em> while the owner was still releasing its breakpoint, rolling back and resuming -
	/// the most order-sensitive moment of the run, and exactly the concurrency the lease exists to exclude.
	/// "Expiry stops granting new work" means no new <em>lease</em>. What it protects now: during cleanup
	/// the lease still blocks everyone except its own owner, whose authorization - by thread or by a capture
	/// naming this lease - is the only thing that passes.
	/// </summary>
	[Fact]
	public async Task An_expired_lease_still_excludes_everyone_but_its_owner() {
		using var coordinator=new ActionLeaseCoordinator();
		using var authorization=new ActionLeaseAuthorization(coordinator);
		using var lease=coordinator.Acquire(42,"short","action-12",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		await Task.Delay(250);
		Assert.True(lease.IsExpired);

		// An external caller, on its own thread and with no capture at all.
		Assert.True(coordinator.TryGetBlock(42,"continue",out var owner));
		Assert.Equal("action-12",owner.ActionId);
		Assert.True(coordinator.TryGetBlock(null,"breakpoint_mutation",out _));
		Assert.True(authorization.TryGetBlock(42,"continue",null,out _));

		// The owner, both ways it can present itself: the thread-scoped authorization, and a capture taken
		// under it and carried across the marshal to the debugger thread.
		lease.ExecuteMutation(()=>Assert.False(coordinator.TryGetBlock(42,"continue",out _)));
		var captured=lease.ExecuteMutation(()=>authorization.Capture());
		Assert.NotNull(captured);
		Assert.False(authorization.TryGetBlock(42,"continue",captured,out _));
		Assert.False(authorization.TryGetBlock(null,"breakpoint_mutation",captured,out _));
	}

	[Fact]
	public async Task An_expired_lease_still_authorizes_cleanup_after_an_owner_disconnect() {
		using var coordinator=new ActionLeaseCoordinator();
		using var disconnected=new CancellationTokenSource();
		using var lease=Acquire(coordinator,ownerLifetime:disconnected.Token);
		disconnected.Cancel();
		await Task.Delay(50);
		Assert.True(lease.IsExpired);
		var resumed=false;
		lease.ExecuteMutation(()=>resumed=true);
		Assert.True(resumed);
	}

	/// <summary>An external engine transition during the cleanup window is still classified, because the
	/// lease is still the process's owner until cleanup finishes.</summary>
	[Fact]
	public async Task An_external_debugger_action_during_the_cleanup_window_is_still_reported() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=coordinator.Acquire(42,"short","action-10",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		await Task.Delay(250);
		Assert.True(coordinator.ReportExternalDebuggerAction(42,"ui_continue"));
		Assert.True(lease.ExternalActionCancellation.IsCancellationRequested);
	}

	/// <summary>The window is a bound, not a promise: an owner that overruns it loses authorization and
	/// its next mutation fails loudly rather than being silently skipped.</summary>
	[Fact]
	public void An_owner_that_overruns_the_cleanup_window_loses_authorization() {
		using var coordinator=new ActionLeaseCoordinator();
		var lease=Acquire(coordinator);
		lease.Dispose();
		Assert.Throws<ObjectDisposedException>(()=>lease.ExecuteMutation(()=>{ }));
	}

	/// <summary>A second action cannot take the process while the previous owner is still cleaning up.</summary>
	[Fact]
	public async Task A_new_lease_waits_for_the_previous_owners_cleanup_window() {
		using var coordinator=new ActionLeaseCoordinator();
		var lease=coordinator.Acquire(42,"short","action-11",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		await Task.Delay(250);
		Assert.Throws<ActionLeaseConflictException>(()=>Acquire(coordinator));
		lease.Dispose();
		using var next=Acquire(coordinator);
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
	}

	[Fact]
	public void Concurrent_acquire_has_one_winner() {
		using var coordinator=new ActionLeaseCoordinator();
		var winners=new ConcurrentBag<ActionLease>();
		var conflicts=0;
		Parallel.For(0,16,index=>{
			try { winners.Add(coordinator.Acquire(42,"race","action-"+index,DateTime.UtcNow.AddSeconds(5),"status","cancel")); }
			catch(ActionLeaseConflictException) { Interlocked.Increment(ref conflicts); }
		});
		Assert.Single(winners);
		Assert.Equal(15,conflicts);
		foreach(var lease in winners) lease.Dispose();
	}
}
