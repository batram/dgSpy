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

	[Fact]
	public async Task Deadline_self_releases() {
		using var coordinator=new ActionLeaseCoordinator();
		using var lease=coordinator.Acquire(42,"short","action-8",DateTime.UtcNow.AddMilliseconds(80),"status","cancel");
		Assert.True(coordinator.TryGetBlock(42,"continue",out _));
		await Task.Delay(200);
		Assert.False(coordinator.TryGetBlock(42,"continue",out _));
	}

	[Fact]
	public void Owner_disconnect_releases_immediately() {
		using var coordinator=new ActionLeaseCoordinator();
		using var disconnected=new CancellationTokenSource();
		using var lease=Acquire(coordinator,ownerLifetime:disconnected.Token);
		disconnected.Cancel();
		Assert.False(coordinator.TryGetBlock(42,"continue",out _));
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
