using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookLabDrainPolicyTests {
	[Fact]
	public void PartialPageCompletesRemovalBoundary() {
		var report=Report(44);
		Assert.Equal(44,HookLabDrainPolicy.EventCount(report));
		Assert.False(HookLabDrainPolicy.NeedsAnotherPage(report,1));
	}

	[Fact]
	public void FullPageRequiresAnotherBoundedDrain() {
		Assert.True(HookLabDrainPolicy.NeedsAnotherPage(Report(256),1));
		Assert.False(HookLabDrainPolicy.NeedsAnotherPage(Report(256),HookLabDrainPolicy.MaximumPages));
	}

	[Fact]
	public void EmptyPageCompletesImmediately() => Assert.False(HookLabDrainPolicy.NeedsAnotherPage(Report(0),1));

	static Dictionary<string,string> Report(int count) {
		var result=new Dictionary<string,string>();
		for(var index=0;index<count;index++) result["event_"+index]="event";
		return result;
	}
}
