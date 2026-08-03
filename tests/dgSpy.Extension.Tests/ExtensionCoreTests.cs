using dgSpy.Extension;
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
}
