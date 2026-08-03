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
}
