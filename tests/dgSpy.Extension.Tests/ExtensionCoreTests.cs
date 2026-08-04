using dgSpy.Extension;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ExtensionCoreTests {
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
