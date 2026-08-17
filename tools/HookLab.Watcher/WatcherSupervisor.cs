namespace HookLab.Watcher;

internal sealed class SupervisorPolicy {
	public static readonly TimeSpan BaseDelay=TimeSpan.FromMinutes(1);
	public static readonly TimeSpan HealthyUptime=TimeSpan.FromMinutes(5);
	public const int MaximumConsecutiveFailures=3;
	int failures;
	public TimeSpan? NextRestartDelay(int exitCode,TimeSpan uptime) {
		if(exitCode==0) return null;
		if(uptime>=HealthyUptime) failures=0;
		failures++;
		if(failures>MaximumConsecutiveFailures) return null;
		return BaseDelay*failures;
	}
}

internal static class WatcherSupervisor {
	public static int Run(Func<(int ExitCode,TimeSpan Uptime)> runChild,Action<TimeSpan> sleep,Action<string> report) {
		var policy=new SupervisorPolicy();
		while(true) {
			var (exitCode,uptime)=runChild();
			var delay=policy.NextRestartDelay(exitCode,uptime);
			if(delay is null) {
				if(exitCode!=0) report("HookLab watcher supervisor is giving up after repeated failures; last exit code "+exitCode+".");
				return exitCode;
			}
			report("HookLab watcher exited with code "+exitCode+" after "+(int)uptime.TotalSeconds+"s; restarting in "+(int)delay.Value.TotalSeconds+"s.");
			sleep(delay.Value);
		}
	}
}
