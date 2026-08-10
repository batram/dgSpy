using System;
using System.Threading;
using dgSpy.Extension.Debugger;
using dnSpy.Debugger.Shared;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>
/// The race T06 left open: enforcement asked the lease on the queuing thread and mutated on the debugger
/// dispatcher thread, with nothing re-asking in between. These run against the real coordinator and the
/// real dnSpy debugger dispatcher, both compiled into this assembly, so the queue being exercised is the
/// one the product uses.
/// </summary>
public sealed class ActionLeaseAuthorizationTests {
	const int processId = 41;
	const int otherProcessId = 42;

	static ActionLease Acquire(ActionLeaseCoordinator coordinator, int process, string actionId, int seconds = 30) =>
		coordinator.Acquire(process, "run_to", actionId, DateTime.UtcNow.AddSeconds(seconds), "get_atomic_action", "cancel_atomic_action");

	[Fact]
	public void A_mutation_queued_before_the_lease_is_refused_when_it_finally_runs() {
		using var coordinator = new ActionLeaseCoordinator();
		using var authorization = new ActionLeaseAuthorization(coordinator);
		using var harness = new DispatcherHarness();
		using var release = new ManualResetEventSlim(false);
		using var done = new ManualResetEventSlim(false);
		harness.Dispatcher.BeginInvoke(() => release.Wait(TimeSpan.FromSeconds(30)));

		// An external caller decides while nothing owns the process, exactly as a UI command does.
		var captured = authorization.Capture();
		Assert.Null(captured);
		Assert.False(authorization.TryGetBlock(processId, "continue", captured, out _));

		var mutated = 0;
		string? refusal = null;
		harness.Dispatcher.BeginInvoke(() => {
			if (authorization.TryGetBlock(processId, "continue", captured, out var owner))
				refusal = owner.FormatBlockReason("continue");
			else
				Interlocked.Increment(ref mutated);
			done.Set();
		});

		using var lease = Acquire(coordinator, processId, "action-1");
		release.Set();

		Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
		Assert.Equal(0, Volatile.Read(ref mutated));
		Assert.NotNull(refusal);
		Assert.Contains("action-1", refusal!, StringComparison.Ordinal);
		Assert.Contains("cancel_atomic_action", refusal!, StringComparison.Ordinal);
	}

	[Fact]
	public void The_owner_s_own_mutation_still_passes_after_the_marshal_that_drops_its_authorization() {
		using var coordinator = new ActionLeaseCoordinator();
		using var authorization = new ActionLeaseAuthorization(coordinator);
		using var harness = new DispatcherHarness();
		using var done = new ManualResetEventSlim(false);
		using var lease = Acquire(coordinator, processId, "action-1");

		var mutated = 0;
		var threadStaticSurvived = true;
		var blocked = true;

		// Exactly the host's shape: the owner authorizes one synchronous call on the debugger thread, and
		// that call captures and then queues its engine work.
		harness.Dispatcher.Invoke(() => lease.ExecuteMutation(() => {
			var captured = authorization.Capture();
			Assert.NotNull(captured);
			harness.Dispatcher.BeginInvoke(() => {
				// The ThreadStatic authorization is gone by the time the queued callback runs - which is
				// precisely why moving the check here needs the capture rather than the thread.
				threadStaticSurvived = !coordinator.TryGetBlock(processId, "continue", out _);
				blocked = authorization.TryGetBlock(processId, "continue", captured, out _);
				if (!blocked)
					Interlocked.Increment(ref mutated);
				done.Set();
			});
		}));

		Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
		Assert.False(threadStaticSurvived);
		Assert.False(blocked);
		Assert.Equal(1, Volatile.Read(ref mutated));
	}

	[Fact]
	public void An_authorization_captured_under_one_lease_does_not_pass_a_later_one() {
		using var coordinator = new ActionLeaseCoordinator();
		using var authorization = new ActionLeaseAuthorization(coordinator);
		object? captured;
		using (var first = Acquire(coordinator, processId, "action-1"))
			captured = first.ExecuteMutation(() => authorization.Capture());
		Assert.NotNull(captured);

		using var second = Acquire(coordinator, processId, "action-2");
		Assert.True(authorization.TryGetBlock(processId, "continue", captured, out var owner));
		Assert.Equal("action-2", owner.ActionId);
	}

	[Fact]
	public void A_process_less_mutation_is_blocked_by_a_lease_the_capture_does_not_own() {
		using var coordinator = new ActionLeaseCoordinator();
		using var authorization = new ActionLeaseAuthorization(coordinator);
		using var owned = Acquire(coordinator, processId, "action-1");
		var captured = owned.ExecuteMutation(() => authorization.Capture());

		// Breakpoint state is not owned by one process, so it asks with no process id at all. The owner of
		// one process must not be waved through a second owner's lease.
		Assert.False(authorization.TryGetBlock(null, "breakpoint_mutation", captured, out _));
		using var other = Acquire(coordinator, otherProcessId, "action-2");
		Assert.True(authorization.TryGetBlock(null, "breakpoint_mutation", captured, out var blocking));
		Assert.Equal("action-2", blocking.ActionId);
	}

	/// <summary>
	/// The cold path, and the one every other test in this file walked around by constructing the bridge
	/// first. dnSpy imports its guards as <c>Lazy&lt;DbgActionGuard&gt;</c> and first realizes them inside
	/// <c>CaptureAuthorization()</c>, so on a freshly started host the guard - and this bridge with it -
	/// does not exist until the first guarded mutation, which for the first atomic action of the host's
	/// lifetime is the action's own. The lease is therefore already held when the bridge is built. A bridge
	/// that derived ownership from <c>LeaseChanged</c> notifications started empty here, captured nothing,
	/// and the queued debugger-thread recheck refused the owner's own mutation.
	/// </summary>
	[Fact]
	public void A_bridge_built_after_the_lease_still_captures_its_owner() {
		using var coordinator = new ActionLeaseCoordinator();
		using var lease = Acquire(coordinator, processId, "action-1");

		// Nothing existed when the lease was acquired; the bridge is realized by the owner's own mutation.
		using var authorization = new ActionLeaseAuthorization(coordinator);

		var captured = lease.ExecuteMutation(() => authorization.Capture());
		Assert.NotNull(captured);
		Assert.False(authorization.TryGetBlock(processId, "continue", captured, out _));
		Assert.False(authorization.TryGetBlock(null, "breakpoint_mutation", captured, out _));

		// An external caller realizing the same cold bridge is still refused.
		var external = authorization.Capture();
		Assert.Null(external);
		Assert.True(authorization.TryGetBlock(processId, "continue", external, out var owner));
		Assert.Equal("action-1", owner.ActionId);
		Assert.True(authorization.TryGetBlock(null, "breakpoint_mutation", external, out _));
	}

	/// <summary>The same cold path across the real marshal: capture on the thread that asks, decide on the
	/// debugger dispatcher thread that mutates, with the bridge built after the lease.</summary>
	[Fact]
	public void A_bridge_built_after_the_lease_passes_the_owner_across_the_marshal() {
		using var coordinator = new ActionLeaseCoordinator();
		using var lease = Acquire(coordinator, processId, "action-1");
		using var authorization = new ActionLeaseAuthorization(coordinator);
		using var harness = new DispatcherHarness();
		using var done = new ManualResetEventSlim(false);

		var mutated = 0;
		var blocked = true;
		harness.Dispatcher.Invoke(() => lease.ExecuteMutation(() => {
			var captured = authorization.Capture();
			harness.Dispatcher.BeginInvoke(() => {
				blocked = authorization.TryGetBlock(processId, "continue", captured, out _);
				if (!blocked)
					Interlocked.Increment(ref mutated);
				done.Set();
			});
		}));

		Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
		Assert.False(blocked);
		Assert.Equal(1, Volatile.Read(ref mutated));
	}

	/// <summary>
	/// The window a mirror-plus-subscribe fix would have to argue about, made explicit: the lease is
	/// acquired concurrently with the bridge being built, repeatedly. Nothing here is primed, so there is
	/// no interleaving in which an acquisition is missed.
	/// </summary>
	[Fact]
	public void A_lease_acquired_while_the_bridge_is_being_built_is_never_missed() {
		for (int i = 0; i < 200; i++) {
			using var coordinator = new ActionLeaseCoordinator();
			ActionLeaseAuthorization? bridge = null;
			using var ready = new ManualResetEventSlim(false);
			var builder = new Thread(() => { ready.Wait(TimeSpan.FromSeconds(30)); bridge = new ActionLeaseAuthorization(coordinator); });
			builder.Start();
			ready.Set();
			using (var lease = Acquire(coordinator, processId, "action-" + i)) {
				builder.Join(TimeSpan.FromSeconds(30));
				using var authorization = bridge!;
				Assert.True(authorization.TryGetBlock(processId, "continue", null, out var owner));
				Assert.Equal("action-" + i, owner.ActionId);
				Assert.NotNull(lease.ExecuteMutation(() => authorization.Capture()));
			}
		}
	}

	[Fact]
	public void An_unleased_process_is_never_blocked() {
		using var coordinator = new ActionLeaseCoordinator();
		using var authorization = new ActionLeaseAuthorization(coordinator);
		Assert.False(authorization.TryGetBlock(processId, "continue", authorization.Capture(), out _));
		using var lease = Acquire(coordinator, otherProcessId, "action-1");
		Assert.False(authorization.TryGetBlock(processId, "continue", null, out _));
	}

	/// <summary>dnSpy's own debugger dispatcher, on its own thread, so "block the dispatcher" means what it
	/// means in the product.</summary>
	sealed class DispatcherHarness : IDisposable {
		readonly Thread thread;
		readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
		Dispatcher? dispatcher;

		public DispatcherHarness() {
			thread = new Thread(() => {
				dispatcher = new Dispatcher();
				ready.Set();
				dispatcher.Run();
			}) { IsBackground = true, Name = "test-debugger-dispatcher" };
			thread.Start();
			Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
		}

		public Dispatcher Dispatcher => dispatcher!;

		public void Dispose() {
			Dispatcher.BeginInvokeShutdown();
			thread.Join(TimeSpan.FromSeconds(30));
			ready.Dispose();
		}
	}
}
