using dgSpy.Extension;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ExtensionCoreTests {
	[Fact]
	public void Dispatcher_contains_async_fault_and_runs_the_next_callback() {
		dnSpy.Debugger.Shared.Dispatcher? dispatcher=null; Exception? recorded=null;
		using var ready=new ManualResetEvent(false); using var after=new ManualResetEvent(false);
		var thread=new Thread(()=>{ dispatcher=new dnSpy.Debugger.Shared.Dispatcher(ex=>recorded=ex); ready.Set(); dispatcher.Run(); }) { IsBackground=true };
		thread.Start(); Assert.True(ready.WaitOne(TimeSpan.FromSeconds(2)));
		dispatcher!.BeginInvoke(()=>throw new InvalidOperationException("injected dispatcher fault"));
		dispatcher.BeginInvoke(()=>after.Set());
		Assert.True(after.WaitOne(TimeSpan.FromSeconds(2)));
		Assert.Equal("injected dispatcher fault",recorded?.Message);
		dispatcher.BeginInvokeShutdown(); Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
	}

	[Fact]
	public void Dispatcher_invoke_propagates_fault_without_killing_dispatcher() {
		dnSpy.Debugger.Shared.Dispatcher? dispatcher=null;
		using var ready=new ManualResetEvent(false); using var after=new ManualResetEvent(false);
		var thread=new Thread(()=>{ dispatcher=new dnSpy.Debugger.Shared.Dispatcher(); ready.Set(); dispatcher.Run(); }) { IsBackground=true };
		thread.Start(); Assert.True(ready.WaitOne(TimeSpan.FromSeconds(2)));
		var fault=Assert.Throws<InvalidOperationException>(()=>dispatcher!.Invoke<int>(()=>throw new InvalidOperationException("sync fault")));
		Assert.Equal("sync fault",fault.Message);
		dispatcher!.BeginInvoke(()=>after.Set()); Assert.True(after.WaitOne(TimeSpan.FromSeconds(2)));
		dispatcher.BeginInvokeShutdown(); Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
	}

	[Theory]
	[InlineData(5ul,0,10,5)]
	[InlineData(5ul,3,10,2)]
	[InlineData(5ul,5,10,0)]
	[InlineData(100ul,10,20,20)]
	public void Member_pages_never_request_past_dnSpy_child_count(ulong total,int offset,int requested,int expected) =>
		Assert.Equal(expected,MemberPagination.Count(total,offset,requested));

	[Theory]
	[InlineData(EventKinds.ThreadCreated,false,false)]
	[InlineData(EventKinds.ThreadExited,false,false)]
	[InlineData(EventKinds.ModuleLoaded,false,false)]
	[InlineData(EventKinds.ModuleUnloaded,false,false)]
	[InlineData(EventKinds.ProcessCreated,true,false)]
	[InlineData(EventKinds.RuntimeExited,true,false)]
	[InlineData(EventKinds.Continued,false,true)]
	[InlineData(EventKinds.Stopped,false,true)]
	public void Events_advance_only_the_relevant_revision(string kind,bool lifecycle,bool execution) {
		Assert.Equal(lifecycle,StateRevisionKinds.ChangesLifecycle(kind));
		Assert.Equal(execution,StateRevisionKinds.ChangesExecution(kind));
	}
	[Fact]
	public void Rpc_authentication_requires_token_and_exact_host_identity() {
		Assert.Null(RpcRequestAuthenticator.Reject("get_host_info","host-a","secret","host-a","secret"));
		Assert.NotNull(RpcRequestAuthenticator.Reject("get_host_info","host-a",null,"host-a","secret"));
		Assert.NotNull(RpcRequestAuthenticator.Reject("get_host_info","host-a","guess","host-a","secret"));
		Assert.NotNull(RpcRequestAuthenticator.Reject("get_host_info","host-b","secret","host-a","secret"));
		Assert.NotNull(RpcRequestAuthenticator.Reject("get_host_info",null,"secret","host-a","secret"));
	}

	[Fact]
	public void Authenticated_ping_may_discover_the_stable_host_identity() =>
		Assert.Null(RpcRequestAuthenticator.Reject("ping",null,"secret","host-a","secret"));

	[Fact]
	public void ProgramIdentityUsesTypedRuntimeFields() {
		var runtimeGuid=Guid.Parse("3B476D35-A401-11D2-AAD4-00C04F990171");

		var id=ProgramIdentity.Create(42,runtimeGuid,"CLR v4.0.30319");

		Assert.Equal("42:3b476d35a40111d2aad400c04f990171:CLR v4.0.30319",id);
	}

	[Fact]
	public void TwoRuntimesAtOnePidGetDistinctStableIdentities() {
		// A process can expose more than one supported runtime, and both entries must stay separately
		// addressable. Identity is composed from typed fields, so the runtime GUID — the only thing that
		// separates .NET Framework from Unity/Mono — has to reach the id.
		var framework=Guid.Parse("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");
		var unity=Guid.Parse("CE8A11EE-73EF-4A51-B5D0-BDA2E665A2B4");

		var first=ProgramIdentity.Create(4242,framework,"CLR v4.0.30319");
		var second=ProgramIdentity.Create(4242,unity,"Unity");

		Assert.NotEqual(first,second);
		Assert.Equal(first,ProgramIdentity.Create(4242,framework,"CLR v4.0.30319"));
		Assert.StartsWith("4242:",first);
		Assert.StartsWith("4242:",second);
	}

	[Theory]
	[InlineData(true,false,false,null,"faulted")]
	[InlineData(false,true,false,null,"attaching")]
	[InlineData(false,false,false,null,"exited")]
	[InlineData(false,false,true,true,"running")]
	[InlineData(false,false,true,false,"paused")]
	[InlineData(false,false,true,null,"mixed")]
	public void SessionStateHasStablePrecedence(bool faulted,bool attaching,bool debugging,bool? running,string expected) =>
		Assert.Equal(expected,SessionStateCalculator.Get(faulted,attaching,debugging,running));

	[Fact]
	public void EventBufferRetainsOnlyItsBoundedTail() {
		var buffer=new DebugEventBuffer(3);

		for (var version=1;version<=4;version++) buffer.Add("stopped",version);

		Assert.Equal(4,buffer.LastEventId);
		Assert.Equal(2,buffer.OldestEventId);
		Assert.Equal(new long[] { 2,3,4 },buffer.FindAfter(0,"stopped").Select(e=>e.EventId));
		var snapshot=buffer.Snapshot(0);
		Assert.True(snapshot.Truncated);
		Assert.Equal(2,snapshot.OldestEventId);
		Assert.Equal(1,snapshot.OldestAvailableCursor);
	}

	[Fact]
	public async Task ConcurrentWaitersObserveTheSameEventWithoutConsumingIt() {
		var buffer=new DebugEventBuffer();
		var first=buffer.WaitForChangeAsync(0,CancellationToken.None);
		var second=buffer.WaitForChangeAsync(0,CancellationToken.None);

		buffer.Add(new DebugEvent { Kind="stopped",StopReason="breakpoint" },2);

		await Task.WhenAll(first,second);
		Assert.Equal(1,Assert.Single(buffer.Snapshot(0,new[]{"stopped"}).Events).EventId);
		Assert.Equal(1,Assert.Single(buffer.Snapshot(0,new[]{"stopped"}).Events).EventId);
	}

	/// <summary>A cursor of 0 replays the whole retained buffer, and it has to: reads are non-destructive
	/// so the buffer holds no per-caller position, and defaulting to "the end" instead would silently drop
	/// a stop that landed between two calls. A repeated stop is recoverable, a lost one is not.
	///
	/// The cost is that a caller polling wait_for_stop without advancing after_event_id re-reads stops it
	/// already handled — which happened, and read as the target hitting breakpoints it had hit minutes
	/// earlier. The remedy is that last_event_id is the next cursor and the tool text now says so; this
	/// test pins the behavior those words describe, in both directions.</summary>
	[Fact]
	public void ReadingFromCursorZeroReplaysHistoryAndLastEventIdIsTheCursorThatDoesNot() {
		var buffer=new DebugEventBuffer();
		buffer.Add(new DebugEvent { Kind="stopped",StopReason="breakpoint" },1);
		var handled=buffer.Snapshot(0,new[]{"stopped"});
		Assert.Equal(1,Assert.Single(handled.Events).EventId);

		buffer.Add(new DebugEvent { Kind="stopped",StopReason="breakpoint" },2);

		// The trap: the same bare call returns the already-handled stop alongside the new one.
		Assert.Equal(new long[]{1,2},buffer.Snapshot(0,new[]{"stopped"}).Events.Select(e=>e.EventId));
		// The documented way out, and the reason no server-side cursor is needed.
		var onlyNew=buffer.Snapshot(handled.LastEventId,new[]{"stopped"});
		Assert.Equal(2,Assert.Single(onlyNew.Events).EventId);
		Assert.False(onlyNew.Truncated);
		Assert.Empty(buffer.Snapshot(onlyNew.LastEventId,new[]{"stopped"}).Events);
	}

	/// <summary>A cursor past the last event id can never be satisfied, and left alone it produced
	/// `timed_out: true` with no events — byte-identical to "the target did not stop". Two agents hit
	/// this; one burned three calls on it. Verified live on 2026-08-08: after_event_id 99999 against a
	/// stream whose last id was 62 returned a clean timeout and no error.
	///
	/// The boundary is the whole point: after == last is the ordinary caught-up caller and must be
	/// allowed to wait, after == last + 1 cannot be.</summary>
	[Theory]
	[InlineData(0,62,false)]
	[InlineData(61,62,false)]
	[InlineData(62,62,false)]
	[InlineData(63,62,true)]
	[InlineData(99999,62,true)]
	[InlineData(1,0,true)]
	public void AnUnreachableEventCursorIsRejectedRatherThanWaitedOn(long after,long last,bool rejected) {
		Assert.Equal(rejected,EventCursorGuard.IsAheadOfStream(after,last));
		if (!rejected) { EventCursorGuard.EnsureReachable(after,last); return; }

		var error=Assert.Throws<RpcException>(()=>EventCursorGuard.EnsureReachable(after,last));
		Assert.Equal("cursor_ahead_of_stream",error.Code);
		Assert.Contains("last_event_id",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public async Task WaiterCancellationDoesNotPreventLaterWaiters() {
		var buffer=new DebugEventBuffer();
		using var cancelled=new CancellationTokenSource();
		var abandoned=buffer.WaitForChangeAsync(0,cancelled.Token);
		cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>abandoned);

		var next=buffer.WaitForChangeAsync(0,CancellationToken.None);
		buffer.Add("continued",1);
		await next;
	}

	[Fact]
	public async Task WaitReturnsImmediatelyWhenTheCursorIsAlreadyBehind() {
		var buffer=new DebugEventBuffer();
		buffer.Add("continued",1);

		await buffer.WaitForChangeAsync(0,CancellationToken.None);
	}

	[Fact]
	public void EventCursorIsNonDestructiveAndFiltersKinds() {
		var buffer=new DebugEventBuffer();
		buffer.Add("continued",1);
		buffer.Add("stopped",2);

		var first=buffer.FindAfter(0,"stopped");
		var second=buffer.FindAfter(0,"stopped");

		Assert.Single(first);
		Assert.Equal(2,first[0].EventId);
		Assert.Equal(first.Select(e=>e.EventId),second.Select(e=>e.EventId));
		Assert.Empty(buffer.FindAfter(2,"stopped"));
	}

	[Fact]
	public void EmptyEventBufferStartsAtCursorOne() {
		var buffer=new DebugEventBuffer();

		Assert.Equal(0,buffer.LastEventId);
		Assert.Equal(1,buffer.OldestEventId);
	}

	[Fact]
	public void FinalWaitSnapshotWithAnEventIsNotATimeout() {
		Assert.True(new EventBufferSnapshot { Events=new[] { new DebugEvent() } }.SatisfiesWait);
		Assert.True(new EventBufferSnapshot { Truncated=true }.SatisfiesWait);
		Assert.False(new EventBufferSnapshot().SatisfiesWait);
	}

	[Fact]
	public void EventBufferPreservesTerminalDetailsAndResetsBetweenSessions() {
		var buffer=new DebugEventBuffer();
		buffer.Add("session_exited",7,terminal:true,processId:4242,exitCode:23,reason:"target_exited");

		var terminal=Assert.Single(buffer.FindAfter(0));
		Assert.True(terminal.Terminal);
		Assert.Equal(4242,terminal.ProcessId);
		Assert.Equal(23,terminal.ExitCode);
		Assert.Equal("target_exited",terminal.Reason);

		buffer.Reset();
		Assert.Equal(0,buffer.LastEventId);
		Assert.Empty(buffer.FindAfter(0));
	}

	[Fact]
	public async Task OutputBufferIsBoundedCursorBasedAndNonDestructive() {
		var buffer=new OutputBuffer(2);
		buffer.Add("Output","one"); buffer.Add("ErrorUser","two"); buffer.Add("Output","three");
		var snapshot=buffer.Snapshot(0);
		Assert.True(snapshot.Truncated);
		Assert.Equal(2,snapshot.Messages.Length);
		Assert.Equal(new[]{"two","three"},snapshot.Messages.Select(m=>m.Message));
		Assert.Equal(snapshot.Messages.Select(m=>m.OutputId),buffer.Snapshot(0).Messages.Select(m=>m.OutputId));
		using var cancelled=new CancellationTokenSource();
		var wait=buffer.WaitAsync(snapshot.Last,cancelled.Token); cancelled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait);
	}

	[Fact]
	public void Closed_frame_has_the_documented_stale_handle_contract() {
		FrameSnapshotGuard.EnsureOpen(false,"still valid");

		var error=Assert.Throws<RpcException>(()=>FrameSnapshotGuard.EnsureOpen(true,"refresh the snapshot"));
		Assert.Equal("stale_handle",error.Code);
		Assert.Contains("refresh",error.Message);
	}

	[Theory]
	// A generated name is unspellable wherever it appears, and it always arrives as a segment after a dot.
	[InlineData("aggregate.<AutoName>k__BackingField")]
	[InlineData("((Milestone1Target.AggregateFixture)existing).<ModuleDef>k__BackingField")]
	[InlineData("this.<>c__DisplayClass0_0")]
	[InlineData("frame.<>4__this")]
	[InlineData("state.<>1__state")]
	[InlineData("CS$<>8__locals0.captured")]
	[InlineData("$VB$Local_x")]
	public void Unspellable_member_names_are_not_offered_as_expressions(string expression) =>
		Assert.False(ExpressionAddressability.IsAddressable(expression));

	[Theory]
	// The angle brackets of a generic type in a cast, and the leading '$' of a pseudo-variable, are both
	// legal - rejecting either would strip expressions that do work.
	[InlineData("aggregate.InstanceCount")]
	[InlineData("aggregate.AutoName")]
	[InlineData("Milestone1Target.AggregateFixture")]
	[InlineData("((System.Collections.Generic.List<int>)x).Count")]
	[InlineData("((System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<int>>)x).Keys")]
	[InlineData("$exception")]
	[InlineData("$exception.Message")]
	[InlineData("$1.InstanceName")]
	[InlineData("commandLine[0]")]
	[InlineData("commandLine[0].Length")]
	public void Ordinary_expressions_are_still_offered(string expression) =>
		Assert.True(ExpressionAddressability.IsAddressable(expression));

	[Fact]
	public void An_absent_expression_is_not_addressable() =>
		Assert.False(ExpressionAddressability.IsAddressable(""));

	[Fact]
	public void Failed_child_expansion_is_one_error_not_a_page_of_members() {
		// The regression this guards: DbgEngineValueNodeImpl used to answer a failed expansion with `count`
		// identical error nodes, so get_members returned a full page of fabricated members. It now throws,
		// and get_members turns that into exactly one error naming the parent expression and the reason.
		var error=ChildExpansionFailure.ToRpcException("this.items","Internal debugger error (InvalidOperationException: engine went away)");

		Assert.Equal("evaluation_failed",error.Code);
		Assert.Contains("this.items",error.Message);
		Assert.Contains("InvalidOperationException: engine went away",error.Message);
		// Nothing to add for an engine fault, so nothing is invented.
		Assert.DoesNotContain("gate",error.Message);
	}

	/// <summary>get_members and its child expansion throw rather than return, so DescribeNode never runs
	/// and these were the last evaluation answers still handing back dnSpy's bare sentence. Observed live
	/// on 2026-08-08: get_members on a method-call expression returned "This expression causes side
	/// effects and will not be evaluated" with no gate named at all. Neither tool can grant side effects,
	/// so the advice must point at evaluate/invoke_method, never at a flag they do not accept.</summary>
	[Fact]
	public void A_thrown_expansion_failure_still_names_the_gate_that_blocked_it() {
		var error=ChildExpansionFailure.ToRpcException("Some.Method(5)","This expression causes side effects and will not be evaluated");

		Assert.Equal("evaluation_failed",error.Code);
		Assert.Contains("no allow_side_effects argument",error.Message);
		Assert.Contains("invoke_method",error.Message);
		// The Gateway prefixes its own "Recovery:" when rendering the error; adding a second label here
		// put the word twice on one line with different text after each.
		Assert.DoesNotContain("Recovery:",error.Message);
		// dnSpy's sentences carry no trailing period, so the two used to run together as
		// "...will not be evaluated Blocked by the side-effects gate...".
		Assert.Contains("will not be evaluated. Blocked by",error.Message);
	}

	[Theory]
	// No terminator: supply one.
	[InlineData("This expression causes side effects and will not be evaluated")]
	// Already terminated: do not double it.
	[InlineData("This expression causes side effects and will not be evaluated.")]
	public void The_appended_advice_is_separated_from_the_engine_sentence_exactly_once(string engineError) {
		var message=engineError+ChildExpansionFailure.Advice(engineError);

		Assert.Contains("evaluated. Blocked",message,StringComparison.Ordinal);
		Assert.DoesNotContain("evaluated.. ",message,StringComparison.Ordinal);
		Assert.DoesNotContain("evaluated  ",message,StringComparison.Ordinal);
	}

	[Fact]
	public void Incomplete_detach_is_never_reported_as_success() {
		DetachCompletionGuard.EnsureRemoved(false,4242);

		var selected=Assert.Throws<RpcException>(()=>DetachCompletionGuard.EnsureRemoved(true,4242));
		Assert.Equal("detach_timed_out",selected.Code);
		Assert.Contains("4242",selected.Message);
		var global=Assert.Throws<RpcException>(()=>DetachCompletionGuard.EnsureRemoved(true));
		Assert.Equal("detach_timed_out",global.Code);
	}
}
