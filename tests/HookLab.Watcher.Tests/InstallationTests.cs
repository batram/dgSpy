using System.Security.Cryptography;
using System.Text.Json;
using HookLab.Watcher;
using Xunit;

public sealed class InstallationTests {
	[Fact]
	public void Install_is_closed_atomic_verified_and_task_read_back() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask();
		var health=new FakeHealth(); var installer=new WatcherInstaller(task,health); var options=InstalledWatcherOptions.Defaults(source+Path.DirectorySeparatorChar,install,state);
		var installed=JsonSerializer.SerializeToElement(installer.Install(options));
		Assert.True(File.Exists(Path.Combine(install,"HookLab.Watcher.exe"))); Assert.True(File.Exists(Path.Combine(install,"hooklab-watcher-install.json"))); Assert.True(task.Registered);
		Assert.True(task.Started); Assert.True(installed.GetProperty("running").GetBoolean()); Assert.Equal(123,installed.GetProperty("watcherProcessId").GetInt32()); Assert.Equal(64,installed.GetProperty("catalogGeneration").GetString()!.Length);
		var result=JsonSerializer.SerializeToElement(installer.Verify(options)); Assert.Equal("valid",result.GetProperty("status").GetString()); Assert.True(result.GetProperty("taskRegistered").GetBoolean());
		Assert.Equal(Path.Combine(install,"HookLab.Watcher.exe"),WatcherInstaller.TaskExecutable(install));
		Assert.Equal("supervise --state-root \""+state+"\"",WatcherInstaller.TaskArguments(install,state));
	}

	[Fact]
	public void Task_readback_accepts_schtasks_quote_normalization() {
		const string xml="<Task version='1.2' xmlns='urn:test'><Triggers><TimeTrigger><Repetition><Interval>PT1H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>2020-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers><Principals><Principal><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings><Actions><Exec><Command>\"C:\\Windows\\powershell.exe\"</Command><Arguments>-NoProfile -File C:\\Users\\mjb\\run-installed.ps1</Arguments></Exec></Actions></Task>";
		Assert.True(WindowsWatcherTaskScheduler.MatchesXml(xml,"C:\\Windows\\powershell.exe","-NoProfile -File \"C:\\Users\\mjb\\run-installed.ps1\""));
	}

	[Fact]
	public void Task_xml_has_unbounded_runtime_and_bounded_failure_recovery() {
		var xml=WindowsWatcherTaskScheduler.BuildTaskXml("C:\\Windows\\powershell.exe","-File C:\\watcher.ps1");
		Assert.True(WindowsWatcherTaskScheduler.MatchesXml(xml,"C:\\Windows\\powershell.exe","-File C:\\watcher.ps1")); Assert.Contains("<Task version=\"1.2\"",xml,StringComparison.Ordinal); Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>",xml,StringComparison.Ordinal); Assert.DoesNotContain("UseUnifiedSchedulingEngine",xml,StringComparison.Ordinal); Assert.Contains("<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>",xml,StringComparison.Ordinal); Assert.Contains("<TimeTrigger><Repetition><Interval>PT1H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>",xml,StringComparison.Ordinal);
	}

	[Fact]
	public void Upgrade_rolls_back_when_task_readback_fails() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path,"first"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var health=new FakeHealth(); var installer=new WatcherInstaller(task,health); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); var original=File.ReadAllText(Path.Combine(install,"payload.txt"));
		File.WriteAllText(Path.Combine(source,"payload.txt"),"second"); RewriteLayout(source); health.Running=true; task.FailReadbackAfterRegister=true;
		Assert.Throws<InvalidOperationException>(()=>installer.Install(options)); Assert.Equal(original,File.ReadAllText(Path.Combine(install,"payload.txt")));
		Assert.True(task.StartCount>=2); Assert.True(health.WaitCount>=2);
	}

	[Fact]
	public void Upgrade_stops_the_existing_task_before_replacing_files() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var health=new FakeHealth(); var installer=new WatcherInstaller(task,health); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); health.Running=true; installer.Install(options); Assert.True(task.Stopped); Assert.Equal(2,task.StartCount);
	}

	[Fact]
	public void Upgrade_preserves_intentionally_stopped_state() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var health=new FakeHealth(); var installer=new WatcherInstaller(task,health); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); health.Running=false; var result=JsonSerializer.SerializeToElement(installer.Install(options)); Assert.Equal(1,task.StartCount); Assert.False(result.GetProperty("running").GetBoolean());
	}

	[Fact]
	public void Upgrade_recognizes_and_restores_the_legacy_visible_task_on_failure() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path,"first"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task,new FakeHealth()); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); task.Register(Path.Combine(install,"HookLab.Watcher.exe"),"run-installed"); task.FailReadbackAfterRegister=true;
		File.WriteAllText(Path.Combine(source,"payload.txt"),"second"); RewriteLayout(source);
		Assert.Throws<InvalidOperationException>(()=>installer.Install(options)); Assert.Equal(Path.Combine(install,"HookLab.Watcher.exe"),task.Executable); Assert.Equal("run-installed",task.Arguments);
	}

	[Fact]
	public void Upgrade_recognizes_the_previous_supervisor_task_and_preserves_running_state() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var health=new FakeHealth { Running=true }; var installer=new WatcherInstaller(task,health); var options=InstalledWatcherOptions.Defaults(source,install,state);
		Directory.CreateDirectory(install); task.Register(Path.Combine(install,"HookLab.Watcher.exe"),"supervise"); installer.Install(options); Assert.Equal(1,task.StartCount); Assert.Equal(WatcherInstaller.TaskArguments(install,state),task.Arguments);
	}

	[Fact]
	public void Install_rejects_tampering_and_uninstall_preserves_state_when_requested() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); File.AppendAllText(Path.Combine(source,"payload.txt"),"tampered"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task,new FakeHealth());
		Assert.Throws<InvalidDataException>(()=>installer.Install(InstalledWatcherOptions.Defaults(source,install,state)));
		RewriteLayout(source); installer.Install(InstalledWatcherOptions.Defaults(source,install,state)); File.WriteAllText(Path.Combine(state,"operator.txt"),"keep"); installer.Uninstall(InstalledWatcherOptions.Defaults(source,install,state,keepState:true));
		Assert.False(Directory.Exists(install)); Assert.True(File.Exists(Path.Combine(state,"operator.txt"))); Assert.True(task.Removed);
	}

	static string WriteSource(string root,string payload="payload") { var layout=Path.Combine(root,"layout"); var source=Path.Combine(layout,"hooklab-watcher"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source,"HookLab.Watcher.exe"),"exe"); File.WriteAllText(Path.Combine(source,"payload.txt"),payload); RewriteLayout(source); return source; }
	static void RewriteLayout(string source) { var root=Directory.GetParent(source)!.FullName; var files=Directory.EnumerateFiles(source).Select(path=>new { path="hooklab-watcher/"+Path.GetFileName(path),size=new FileInfo(path).Length,sha256=Hash(path),owner="hooklab-watcher" }).ToArray(); File.WriteAllText(Path.Combine(root,"dgspy-layout.json"),JsonSerializer.Serialize(new { formatVersion=1,files })); }
	static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
	sealed class FakeTask:IWatcherTaskScheduler { public bool Registered,Started,Stopped,Removed,Match=true,FailNextReadback,FailReadbackAfterRegister; public int StartCount; public string? Executable,Arguments; public void Register(string value,string args) { Registered=true; Executable=value; Arguments=args; if(FailReadbackAfterRegister) { FailReadbackAfterRegister=false; FailNextReadback=true; } } public void Start() { Started=true; StartCount++; } public void Stop()=>Stopped=true; public void Remove()=>Removed=true; public bool Matches(string value,string args) { if(FailNextReadback) { FailNextReadback=false; return false; } return MatchesAction(value,args); } public bool MatchesAction(string value,string args)=>Match&&Registered&&value==Executable&&args==Arguments; }
	sealed class FakeHealth:IInstalledWatcherHealthProbe { public bool Running; public int WaitCount; public bool IsHealthy(string installRoot,string stateRoot)=>Running; public InstalledWatcherHealth WaitForHealthy(string installRoot,string stateRoot,TimeSpan timeout) { WaitCount++; Running=true; return new(123,456,Path.Combine(installRoot,"HookLab.Watcher.exe"),new string('a',64)); } }
	sealed class TemporaryDirectory:IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-install-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
