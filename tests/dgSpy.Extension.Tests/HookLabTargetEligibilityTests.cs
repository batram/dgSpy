using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookLabTargetEligibilityTests {
	static readonly Guid DesktopClr=new("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");
	static readonly Guid CoreClr=new("E0B4EB52-D1D9-42AB-B130-028CA31CF9F6");

	[Fact]
	public void X64_desktop_clr_v4_is_supported() {
		var reason=HookLabTargetEligibility.UnsupportedReason(64,"X64",new[] { new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319") });
		Assert.Null(reason);
	}

	[Fact]
	public void CoreClr_is_rejected_with_the_observed_runtime() {
		var reason=HookLabTargetEligibility.UnsupportedReason(64,"X64",new[] { new HookLabRuntimeIdentity(CoreClr,"CoreCLR") });
		Assert.Equal("HookLab currently supports x64 desktop CLR v4 targets; the attached process exposes CoreCLR.",reason);
	}

	[Theory]
	[InlineData(32,"X86")]
	[InlineData(64,"Arm64")]
	public void Unsupported_architecture_is_rejected_before_runtime_selection(int bitness,string architecture) {
		var reason=HookLabTargetEligibility.UnsupportedReason(bitness,architecture,new[] { new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319") });
		Assert.Contains("attached process architecture is "+architecture,reason,StringComparison.Ordinal);
	}

	[Fact]
	public void A_supported_runtime_wins_in_a_multi_runtime_process() {
		var reason=HookLabTargetEligibility.UnsupportedReason(64,"X64",new[] {
			new HookLabRuntimeIdentity(CoreClr,"CoreCLR"),
			new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319")
		});
		Assert.Null(reason);
	}

	[Fact]
	public void Missing_runtime_is_rejected_explicitly() {
		var reason=HookLabTargetEligibility.UnsupportedReason(64,"X64",Array.Empty<HookLabRuntimeIdentity>());
		Assert.Equal("HookLab currently supports x64 desktop CLR v4 targets; the attached process exposes no managed runtime.",reason);
	}
}
