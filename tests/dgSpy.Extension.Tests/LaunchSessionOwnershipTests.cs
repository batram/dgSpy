using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

public class LaunchSessionOwnershipTests {
	[Fact]
	public void AdoptsTheOwnedImageOnlyWhileThatImageIsLive() {
		Assert.True(LaunchSessionOwnership.CanAdopt(
			@"launch:cordebug:C:\targets\A.exe",
			new[]{@"launch:cordebug:C:\targets\A.exe"},
			new[]{@"c:\TARGETS\a.exe"}));
	}

	[Fact]
	public void DoesNotAdoptAStaleProgramIdWhenOnlyAnotherImageSurvives() {
		Assert.False(LaunchSessionOwnership.CanAdopt(
			@"launch:cordebug:C:\targets\A.exe",
			new[]{@"launch:cordebug:C:\targets\A.exe",@"launch:cordebug:C:\targets\B.exe"},
			new[]{@"C:\targets\B.exe"}));
	}
}
