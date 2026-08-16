using System.Security.Cryptography;
using System.Text.Json;
using HookLab.Watcher;
using Xunit;

public sealed class InstallationTests {
	[Fact]
	public void Install_is_closed_atomic_verified_and_task_read_back() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask();
		var installer=new WatcherInstaller(task); var options=InstalledWatcherOptions.Defaults(source+Path.DirectorySeparatorChar,install,state);
		installer.Install(options);
		Assert.True(File.Exists(Path.Combine(install,"HookLab.Watcher.exe"))); Assert.True(File.Exists(Path.Combine(install,"hooklab-watcher-install.json"))); Assert.True(task.Registered);
		var result=JsonSerializer.SerializeToElement(installer.Verify(options)); Assert.Equal("valid",result.GetProperty("status").GetString()); Assert.True(result.GetProperty("taskRegistered").GetBoolean());
		Assert.EndsWith("powershell.exe",WatcherInstaller.TaskExecutable(),StringComparison.OrdinalIgnoreCase);
		var arguments=WatcherInstaller.TaskArguments(install,state);
		Assert.Contains("-NoProfile -NonInteractive -WindowStyle Hidden -File",arguments,StringComparison.Ordinal);
		Assert.Contains(Path.Combine(install,"run-installed.ps1"),arguments,StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Task_readback_accepts_schtasks_quote_normalization() {
		const string xml="<Task xmlns='urn:test'><Principals><Principal><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings><Actions><Exec><Command>\"C:\\Windows\\powershell.exe\"</Command><Arguments>-NoProfile -File C:\\Users\\mjb\\run-installed.ps1</Arguments></Exec></Actions></Task>";
		Assert.True(WindowsWatcherTaskScheduler.MatchesXml(xml,"C:\\Windows\\powershell.exe","-NoProfile -File \"C:\\Users\\mjb\\run-installed.ps1\""));
	}

	[Fact]
	public void Task_xml_has_unbounded_runtime_and_bounded_failure_recovery() {
		var xml=WindowsWatcherTaskScheduler.BuildTaskXml("C:\\Windows\\powershell.exe","-File C:\\watcher.ps1");
		Assert.True(WindowsWatcherTaskScheduler.MatchesXml(xml,"C:\\Windows\\powershell.exe","-File C:\\watcher.ps1")); Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>",xml,StringComparison.Ordinal); Assert.Contains("<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>",xml,StringComparison.Ordinal);
	}

	[Fact]
	public void Upgrade_rolls_back_when_task_readback_fails() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path,"first"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); var original=File.ReadAllText(Path.Combine(install,"payload.txt"));
		File.WriteAllText(Path.Combine(source,"payload.txt"),"second"); RewriteLayout(source); task.Match=false;
		Assert.Throws<InvalidOperationException>(()=>installer.Install(options)); Assert.Equal(original,File.ReadAllText(Path.Combine(install,"payload.txt")));
	}

	[Fact]
	public void Upgrade_stops_the_existing_task_before_replacing_files() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); installer.Install(options); Assert.True(task.Stopped);
	}

	[Fact]
	public void Upgrade_recognizes_and_restores_the_legacy_visible_task_on_failure() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path,"first"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task); var options=InstalledWatcherOptions.Defaults(source,install,state);
		installer.Install(options); task.Register(Path.Combine(install,"HookLab.Watcher.exe"),"run-installed"); task.FailReadbackAfterRegister=true;
		File.WriteAllText(Path.Combine(source,"payload.txt"),"second"); RewriteLayout(source);
		Assert.Throws<InvalidOperationException>(()=>installer.Install(options)); Assert.Equal(Path.Combine(install,"HookLab.Watcher.exe"),task.Executable); Assert.Equal("run-installed",task.Arguments);
	}

	[Fact]
	public void Install_rejects_tampering_and_uninstall_preserves_state_when_requested() {
		using var temporary=new TemporaryDirectory(); var source=WriteSource(temporary.Path); File.AppendAllText(Path.Combine(source,"payload.txt"),"tampered"); var install=Path.Combine(temporary.Path,"installed"); var state=Path.Combine(temporary.Path,"state"); var task=new FakeTask(); var installer=new WatcherInstaller(task);
		Assert.Throws<InvalidDataException>(()=>installer.Install(InstalledWatcherOptions.Defaults(source,install,state)));
		RewriteLayout(source); installer.Install(InstalledWatcherOptions.Defaults(source,install,state)); File.WriteAllText(Path.Combine(state,"operator.txt"),"keep"); installer.Uninstall(InstalledWatcherOptions.Defaults(source,install,state,keepState:true));
		Assert.False(Directory.Exists(install)); Assert.True(File.Exists(Path.Combine(state,"operator.txt"))); Assert.True(task.Removed);
	}

	static string WriteSource(string root,string payload="payload") { var layout=Path.Combine(root,"layout"); var source=Path.Combine(layout,"hooklab-watcher"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source,"HookLab.Watcher.exe"),"exe"); File.WriteAllText(Path.Combine(source,"payload.txt"),payload); RewriteLayout(source); return source; }
	static void RewriteLayout(string source) { var root=Directory.GetParent(source)!.FullName; var files=Directory.EnumerateFiles(source).Select(path=>new { path="hooklab-watcher/"+Path.GetFileName(path),size=new FileInfo(path).Length,sha256=Hash(path),owner="hooklab-watcher" }).ToArray(); File.WriteAllText(Path.Combine(root,"dgspy-layout.json"),JsonSerializer.Serialize(new { formatVersion=1,files })); }
	static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
	sealed class FakeTask:IWatcherTaskScheduler { public bool Registered,Stopped,Removed,Match=true,FailNextReadback,FailReadbackAfterRegister; public string? Executable,Arguments; public void Register(string value,string args) { Registered=true; Executable=value; Arguments=args; if(FailReadbackAfterRegister) { FailReadbackAfterRegister=false; FailNextReadback=true; } } public void Stop()=>Stopped=true; public void Remove()=>Removed=true; public bool Matches(string value,string args) { if(FailNextReadback) { FailNextReadback=false; return false; } return MatchesAction(value,args); } public bool MatchesAction(string value,string args)=>Match&&Registered&&value==Executable&&args==Arguments; }
	sealed class TemporaryDirectory:IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-install-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
