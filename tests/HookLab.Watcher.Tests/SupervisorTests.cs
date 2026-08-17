using HookLab.Watcher;
using Xunit;

public sealed class SupervisorTests {
	[Fact]
	public void Clean_exit_stops_supervision_without_restart() {
		var runs=0;
		var result=WatcherSupervisor.Run(()=>{ runs++; return (0,TimeSpan.FromSeconds(1)); },_=>Assert.Fail("No sleep expected."),_=>{ });
		Assert.Equal(0,result); Assert.Equal(1,runs);
	}

	[Fact]
	public void Crashes_restart_with_linear_backoff_then_give_up_with_last_exit_code() {
		var runs=0; var delays=new List<TimeSpan>();
		var result=WatcherSupervisor.Run(()=>{ runs++; return (-1,TimeSpan.FromSeconds(10)); },delays.Add,_=>{ });
		Assert.Equal(-1,result); Assert.Equal(4,runs);
		Assert.Equal(new[]{TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(2),TimeSpan.FromMinutes(3)},delays);
	}

	[Fact]
	public void Healthy_uptime_resets_the_consecutive_failure_budget() {
		var policy=new SupervisorPolicy();
		Assert.Equal(TimeSpan.FromMinutes(1),policy.NextRestartDelay(1,TimeSpan.FromSeconds(5)));
		Assert.Equal(TimeSpan.FromMinutes(2),policy.NextRestartDelay(1,TimeSpan.FromSeconds(5)));
		Assert.Equal(TimeSpan.FromMinutes(1),policy.NextRestartDelay(1,TimeSpan.FromHours(2)));
		Assert.Equal(TimeSpan.FromMinutes(2),policy.NextRestartDelay(1,TimeSpan.FromSeconds(5)));
		Assert.Equal(TimeSpan.FromMinutes(3),policy.NextRestartDelay(1,TimeSpan.FromSeconds(5)));
		Assert.Null(policy.NextRestartDelay(1,TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public void Give_up_is_reported() {
		var messages=new List<string>();
		WatcherSupervisor.Run(()=>(7,TimeSpan.Zero),_=>{ },messages.Add);
		Assert.Contains(messages,message=>message.Contains("giving up",StringComparison.Ordinal)&&message.Contains("7",StringComparison.Ordinal));
	}
}
