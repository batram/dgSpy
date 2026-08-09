using dgSpy.Extension.Debugger.OwnedBreakpoints;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class OwnedBreakpointStopLabelTests {
	[Fact]
	public void InternalOnlyStopCarriesUnforgeableOwnerIdentity() {
		var token=Guid.NewGuid();
		Assert.Equal(OwnedBreakpointStopLabel.Prefix+token.ToString("N"),OwnedBreakpointStopLabel.Select(false,token));
	}

	[Fact]
	public void UserVisibleStopWinsOverInternalOwner() {
		Assert.Null(OwnedBreakpointStopLabel.Select(true,Guid.NewGuid()));
	}

	[Fact]
	public void OrdinaryPauseIsNotRelabeled() {
		Assert.Null(OwnedBreakpointStopLabel.Select(false,null));
	}
}
