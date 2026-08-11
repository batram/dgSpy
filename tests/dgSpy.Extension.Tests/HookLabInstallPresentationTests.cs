using dgSpy.Extension;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookLabInstallPresentationTests {
	[Fact]
	public void Waiting_status_explains_automatic_initialization_and_resident_install() {
		var text=HookLabInstallPresentation.Waiting("ConsoleHost.ReadLine");
		Assert.Contains("Initializing HookLab if needed",text);
		Assert.Contains("resident runtime",text);
		Assert.Contains("ConsoleHost.ReadLine",text);
	}

	[Fact]
	public void Trigger_timeout_is_actionable() {
		var text=HookLabInstallPresentation.Failure("prepare",ActionOutcome.trigger_not_reached,InterruptionReason.timeout,null);
		Assert.Contains("trigger_not_reached",text);
		Assert.Contains("timeout",text);
		Assert.Contains("Trigger the method",text);
	}
}
