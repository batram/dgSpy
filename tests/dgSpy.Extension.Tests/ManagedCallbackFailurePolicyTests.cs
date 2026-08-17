using dndbg.Engine;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ManagedCallbackFailurePolicyTests {
	[Theory]
	[InlineData(false, false, false, 0)]
	[InlineData(false, false, true, 1)]
	[InlineData(false, true, true, 0)]
	[InlineData(true, false, false, 2)]
	[InlineData(true, true, true, 2)]
	public void Selects_one_terminal_disposition(bool isExitProcess, bool hasQueuedCallbacks, bool hasIntentionalPause, int expected) =>
		Assert.Equal((ManagedCallbackFailureDisposition)expected, ManagedCallbackFailurePolicy.Decide(isExitProcess, hasQueuedCallbacks, hasIntentionalPause));
}
