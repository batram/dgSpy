namespace dgSpy.Extension.Tests;

using dgSpy.Extension.Debugger.OwnedBreakpoints;
using Xunit;

public sealed class CorDebugRunReconciliationTests {
	[Fact]
	public async Task Refuses_an_unsupported_runtime_without_dispatching() {
		var dispatched=false;
		await Assert.ThrowsAsync<NotSupportedException>(()=>CorDebugRunReconciliation.RunAsync(false,_=>dispatched=true,CancellationToken.None));
		Assert.False(dispatched);
	}

	[Fact]
	public async Task Propagates_the_engine_callback_error() {
		var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>
			CorDebugRunReconciliation.RunAsync(true,completed=>completed("engine refused reconciliation"),CancellationToken.None));
		Assert.Equal("engine refused reconciliation",error.Message);
	}

	[Fact]
	public async Task Completes_when_the_engine_callback_succeeds() {
		await CorDebugRunReconciliation.RunAsync(true,completed=>completed(null),CancellationToken.None);
	}
}
