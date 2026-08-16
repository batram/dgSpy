using System.Collections.Concurrent;
using System.Text.Json;
using HookLab.Injector;
using Xunit;

namespace HookLab.Watcher.Tests;

public sealed class WatcherTests {
	[Fact]
	public void Package_apply_and_status_options_are_strict() {
		Assert.True(CommandOptions.TryParseApply(new[]{"apply","--pid","42","--package","pkg"},out var apply)); Assert.Equal(42,apply.ProcessId); Assert.EndsWith("pkg",apply.PackagePath);
		Assert.True(CommandOptions.TryParseStatus(new[]{"status"},out var all)); Assert.Null(all.ProcessId);
		Assert.True(CommandOptions.TryParseStatus(new[]{"status","--pid","42"},out var one)); Assert.Equal(42,one.ProcessId);
		Assert.False(CommandOptions.TryParseApply(new[]{"apply","--package","pkg"},out _)); Assert.False(CommandOptions.TryParseStatus(new[]{"status","--pid","0"},out _));
	}

	[Fact]
	public void Control_options_are_strict() {
		Assert.True(ControlOptions.TryParse(new[]{"pause"},out var pause)); Assert.True(pause.Paused);
		Assert.True(ControlOptions.TryParse(new[]{"disable-profile","alpha"},out var disable)); Assert.Equal("alpha",disable.DisableProfile);
		Assert.False(ControlOptions.TryParse(new[]{"disable-profile",""},out _)); Assert.False(ControlOptions.TryParse(new[]{"pause","now"},out _));
	}

	[Theory]
	[InlineData(typeof(InvalidDataException),"invalid_input",CommandExitCodes.InvalidInput)]
	[InlineData(typeof(UnauthorizedAccessException),"access_denied",CommandExitCodes.AccessDenied)]
	[InlineData(typeof(ClrReadinessTimeoutException),"runtime_not_ready",CommandExitCodes.Timeout)]
	[InlineData(typeof(TimeoutException),"timeout_known_state_required",CommandExitCodes.Timeout)]
	public void Command_failures_have_stable_categories(Type exceptionType,string code,int exitCode) {
		var exception=(Exception)Activator.CreateInstance(exceptionType,"test")!; var failure=CommandFailure.Classify(exception); Assert.Equal(code,failure.Code); Assert.Equal(exitCode,failure.ExitCode);
	}

	[Fact]
	public void Watcher_layout_contains_the_DPAPI_runtime_dependency()=>Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory,"System.Security.Cryptography.ProtectedData.dll")));
	[Fact]
	public void Watcher_layout_contains_the_authoritative_injector()=>Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory,"HookLab.Injector.dll")));
	[Fact]
	public void Watcher_layout_contains_the_process_start_runtime()=>Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory,"System.Management.dll")));

	[Fact]
	public void Options_are_order_independent_and_bounded() {
		Assert.True(WatchOptions.TryParse(new[]{"run","--max-parallel","7","--definitions","defs","--poll-ms","25","--audit","audit.jsonl"},out var value));
		Assert.Equal(7,value.MaximumParallel); Assert.Equal(25,value.PollMilliseconds); Assert.EndsWith("defs",value.DefinitionsDirectory); Assert.EndsWith("audit.jsonl",value.AuditPath);
		Assert.False(WatchOptions.TryParse(new[]{"run","--definitions","defs","--poll-ms","24"},out _));
		Assert.False(WatchOptions.TryParse(new[]{"run","--definitions","defs","--max-parallel","33"},out _));
		Assert.False(WatchOptions.TryParse(new[]{"run","--definitions","a","--definitions","b"},out _));
		Assert.False(WatchOptions.TryParse(new[]{"launch","--definitions","defs"},out _));
	}

	[Fact]
	public void Catalog_rejects_empty_invalid_and_duplicate_ids() {
		using var directory=new TemporaryDirectory();
		Assert.Throws<InvalidDataException>(()=>DefinitionCatalog.Load(directory.Path));
		File.WriteAllText(System.IO.Path.Combine(directory.Path,"bad.json"),"{}");
		Assert.Throws<InvalidDataException>(()=>DefinitionCatalog.Load(directory.Path));
		File.Delete(System.IO.Path.Combine(directory.Path,"bad.json"));
		WriteDefinition(directory.Path,"one.json","same","One.exe"); WriteDefinition(directory.Path,"two.json","same","Two.exe");
		Assert.Contains("Duplicate definition id",Assert.Throws<InvalidDataException>(()=>DefinitionCatalog.Load(directory.Path)).Message,StringComparison.Ordinal);
	}

	[Fact]
	public void Catalog_loads_multiple_general_definitions() {
		using var directory=new TemporaryDirectory(); WriteDefinition(directory.Path,"one.json","alpha","One.exe"); WriteDefinition(directory.Path,"two.json","beta","Two.exe");
		var values=DefinitionCatalog.Load(directory.Path);
		Assert.Equal(new[]{"alpha","beta"},values.Select(value=>value.Value.Id).OrderBy(value=>value));
	}

	[Fact]
	public void Catalog_accepts_multiple_definitions_for_one_process() {
		using var directory=new TemporaryDirectory(); WriteDefinition(directory.Path,"one.json","alpha","Target.exe"); WriteDefinition(directory.Path,"two.json","beta","target.EXE");
		Assert.Equal(2,DefinitionCatalog.Load(directory.Path).Count);
	}

	[Fact]
	public void Tracker_deduplicates_stable_identity_but_accepts_pid_reuse() {
		var tracker=new CandidateTracker(new[]{Definition("alpha","Target.exe")},4);
		var original=new ProcessIdentity(10,100,"Target.exe",4);
		Assert.Single(tracker.Select(new[]{original})); Assert.Empty(tracker.Select(new[]{original}));
		Assert.Single(tracker.Select(new[]{original with { CreationUtcTicks=101 }}));
	}

	[Fact]
	public void Tracker_isolates_session_basename_and_definition() {
		var tracker=new CandidateTracker(new[]{Definition("alpha","Target.exe"),Definition("other","Other.exe")},7);
		Assert.Empty(tracker.Select(new[]{new ProcessIdentity(1,1,"Target.exe",8),new ProcessIdentity(2,2,"Wrong.exe",7)}));
		var selected=tracker.Select(new[]{new ProcessIdentity(3,3,"target.EXE",7)});
		Assert.Equal("alpha",Assert.Single(selected).Definition.Value.Id);
	}

	[Fact]
	public void Tracker_honors_control_and_retries_only_retryable_attempts() {
		var definition=Definition("alpha","Target.exe") with { ProfileId="profile-alpha" }; var tracker=new CandidateTracker(new[]{definition},7); var process=new ProcessIdentity(3,3,"Target.exe",7); var start=DateTime.UtcNow;
		Assert.Empty(tracker.Select(new[]{process},new(true,new HashSet<string>()),start));
		Assert.Empty(tracker.Select(new[]{process},new(false,new HashSet<string>{"profile-alpha"}),start));
		var work=Assert.Single(tracker.Select(new[]{process},now:start)); tracker.Complete(work,AttemptDisposition.Retryable,start);
		Assert.Empty(tracker.Select(new[]{process},now:start.AddMilliseconds(249))); Assert.Single(tracker.Select(new[]{process},now:start.AddMilliseconds(250)));
	}

	[Fact]
	public void Tracker_treats_changed_definition_digest_as_new_work() {
		var first=Definition("alpha","Target.exe"); var tracker=new CandidateTracker(new[]{first},7); var process=new ProcessIdentity(3,3,"Target.exe",7);
		var work=Assert.Single(tracker.Select(new[]{process})); tracker.Complete(work,AttemptDisposition.Completed);
		tracker.Update(new[]{first with { DefinitionSha256=new string('b',64) }}); Assert.Single(tracker.Select(new[]{process}));
	}

	[Fact]
	public void Retry_policy_is_bounded_and_ambiguous_failures_do_not_retry() {
		var tracker=new CandidateTracker(new[]{Definition("alpha","Target.exe")},7); var process=new ProcessIdentity(3,3,"Target.exe",7); var now=DateTime.UtcNow;
		for(var attempt=0;attempt<3;attempt++) { var work=Assert.Single(tracker.Select(new[]{process},now:now)); tracker.Complete(work,AttemptDisposition.Retryable,now); now=now.AddSeconds(5); }
		Assert.Empty(tracker.Select(new[]{process},now:now));
		Assert.Equal(AttemptDisposition.Retryable,WatchRunner.Classify(new IOException("temporary transport failure")));
		Assert.Equal(AttemptDisposition.Retryable,WatchRunner.Classify(new ClrReadinessTimeoutException("not ready")));
		Assert.Equal(AttemptDisposition.AmbiguousNoRetry,WatchRunner.Classify(new TimeoutException("unknown completion")));
		Assert.Equal(AttemptDisposition.DeterministicRefusal,WatchRunner.Classify(new InvalidDataException("bad definition")));
	}

	[Fact]
	public void Control_and_status_stores_round_trip() {
		using var directory=new TemporaryDirectory(); var controlPath=System.IO.Path.Combine(directory.Path,"control.json"); var statusPath=System.IO.Path.Combine(directory.Path,"status.json"); var store=new WatchControlStore(controlPath);
		var changed=store.Update(paused:true,disableProfile:"alpha"); Assert.True(changed.Paused); Assert.Contains("alpha",store.Read().DisabledProfiles);
		var definition=Definition("alpha","Target.exe"); var status=new WatcherStatusStore(statusPath); status.Publish(changed,new StaticWatchCatalog(new[]{definition}).Current(),new WatchWork(definition,new ProcessIdentity(1,2,"Target.exe",7)),"installed",12,null);
		var value=WatcherStatusStore.Read(statusPath)!.Value; Assert.True(value.GetProperty("paused").GetBoolean()); Assert.Equal("installed",Assert.Single(value.GetProperty("lastResults").EnumerateArray()).GetProperty("status").GetString());
		Assert.True(value.GetProperty("processAlive").GetBoolean()); Assert.Equal("starting",value.GetProperty("recordedLifecycle").GetString());
		var stale=WatcherStatusStore.Read(statusPath,(_,_)=>false)!.Value; Assert.Equal("stale",stale.GetProperty("lifecycle").GetString()); Assert.Equal("starting",stale.GetProperty("recordedLifecycle").GetString()); Assert.False(stale.GetProperty("processAlive").GetBoolean());
	}

	[Fact]
	public async Task Runner_bounds_parallelism_and_isolates_failure() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl"));
		var definitions=new[]{Definition("alpha","Target.exe"),Definition("beta","Other.exe")}; var current=0; var maximum=0; var calls=new ConcurrentBag<string>();
		WatchApplyResult Apply(WatchWork work) { var now=Interlocked.Increment(ref current); maximum=Math.Max(maximum,now); calls.Add(work.Definition.Value.Id!); Thread.Sleep(40); Interlocked.Decrement(ref current); if(work.Process.ProcessId==2) throw new InvalidOperationException("contained failure"); return Result("ok"); }
		var runner=new WatchRunner(definitions,1,25,2,audit,null,apply:Apply);
		runner.Schedule(new[]{new ProcessIdentity(1,1,"Target.exe",1),new ProcessIdentity(2,2,"Other.exe",1),new ProcessIdentity(3,3,"Target.exe",1)});
		await runner.DrainAsync();
		Assert.Equal(2,maximum); Assert.Equal(3,calls.Count);
		audit.Dispose();
		var lines=File.ReadAllLines(System.IO.Path.Combine(directory.Path,"audit.jsonl")); Assert.Equal(3,lines.Length); Assert.Single(lines,line=>JsonDocument.Parse(line).RootElement.GetProperty("status").GetString()=="error");
	}

	[Fact]
	public async Task Cancellation_during_discovery_prevents_new_work_and_publishes_stopped() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl")); using var cancellation=new CancellationTokenSource(); var calls=0; var statusPath=System.IO.Path.Combine(directory.Path,"status.json");
		IReadOnlyList<ProcessIdentity> Snapshot() { cancellation.Cancel(); return new[]{new ProcessIdentity(4,4,"Target.exe",1)}; }
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,Snapshot,work=>{ Interlocked.Increment(ref calls); return Result("ok"); },statusStore:new WatcherStatusStore(statusPath));
		await runner.RunAsync(cancellation.Token); Assert.Equal(0,calls); var status=WatcherStatusStore.Read(statusPath)!.Value; Assert.Equal("stopped",status.GetProperty("lifecycle").GetString()); Assert.Equal("polling",status.GetProperty("discoveryMode").GetString());
	}

	[Fact]
	public async Task Process_start_signal_wakes_full_reconciliation_without_replacing_polling() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl")); using var cancellation=new CancellationTokenSource(); var snapshots=0; var calls=new ConcurrentBag<int>(); var signal=new ImmediateProcessStartSignal(); var watch=System.Diagnostics.Stopwatch.StartNew(); var statusPath=System.IO.Path.Combine(directory.Path,"status.json");
		IReadOnlyList<ProcessIdentity> Snapshot() { snapshots++; return snapshots==1?new[]{new ProcessIdentity(1,1,"Target.exe",1)}:new[]{new ProcessIdentity(1,1,"Target.exe",1),new ProcessIdentity(2,2,"Target.exe",1)}; }
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,5000,1,audit,null,Snapshot,work=>{ calls.Add(work.Process.ProcessId); if(work.Process.ProcessId==2) cancellation.Cancel(); return Result("ok"); },statusStore:new WatcherStatusStore(statusPath),processStarts:signal);
		await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(new[]{1,2},calls.OrderBy(value=>value)); Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2)); Assert.True(signal.Disposed); var status=WatcherStatusStore.Read(statusPath)!.Value; Assert.Equal("subscription",status.GetProperty("lastDiscoveryTrigger").GetString()); Assert.Equal(1,status.GetProperty("subscriptionWakeCount").GetInt64());
	}

	[Fact]
	public void Profile_deadlines_flow_to_authoritative_injector_request() {
		var definition=Definition("alpha","Target.exe") with { ClrReadinessTimeoutMs=750,InitializationTimeoutMs=1250 }; var request=WatchRunner.Request(new(definition,new ProcessIdentity(9,10,"Target.exe",1,"C:\\Target.exe")));
		Assert.Equal(750,request.ClrReadinessTimeoutMs); Assert.Equal(1250,request.InitializationTimeoutMs);
	}

	[Fact]
	public async Task Notification_policy_emits_only_selected_results() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl")); var sink=new RecordingNotificationSink();
		var definitions=new[]{Definition("all","All.exe") with { NotificationPolicy="all" },Definition("errors","Errors.exe") with { NotificationPolicy="errors" },Definition("none","None.exe") with { NotificationPolicy="none" }};
		var runner=new WatchRunner(definitions,1,25,3,audit,null,apply:work=>work.Definition.Value.Id=="all"?Result("installed"):throw new IOException("failed"),notifications:sink);
		runner.Schedule(new[]{new ProcessIdentity(1,1,"All.exe",1),new ProcessIdentity(2,2,"Errors.exe",1),new ProcessIdentity(3,3,"None.exe",1)}); await runner.DrainAsync();
		Assert.Equal(new[]{"all:installed","errors:error"},sink.Values.OrderBy(value=>value));
	}

	[Fact]
	public async Task Immediately_completed_work_is_removed_after_registration() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl"));
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,apply:work=>Result("ok"));
		runner.Schedule(new[]{new ProcessIdentity(9,9,"Target.exe",1)});
		await runner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task Telemetry_failure_cannot_turn_success_into_retryable_work() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl")); var statusPath=System.IO.Path.Combine(directory.Path,"status.json"); File.WriteAllText(statusPath,"{}"); using var locked=new FileStream(statusPath,FileMode.Open,FileAccess.Read,FileShare.None); var calls=0;
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,apply:work=>{ calls++; return Result("ok"); },statusStore:new WatcherStatusStore(statusPath)); var process=new ProcessIdentity(9,9,"Target.exe",1);
		runner.Schedule(new[]{process}); await runner.DrainAsync(); runner.Schedule(new[]{process}); await runner.DrainAsync(); Assert.Equal(1,calls);
	}

	[Fact]
	public void Audit_is_json_lines_and_sanitizes_messages() {
		using var directory=new TemporaryDirectory(); var path=System.IO.Path.Combine(directory.Path,"audit.jsonl");
		using(var audit=new AuditWriter(path)) audit.Write(new WatchWork(Definition("alpha","Target.exe"),new ProcessIdentity(5,6,"Target.exe",1)),new("error",new string('b',64),"probe","probe:alpha",7),9,"bad\r\nmessage");
		using var document=JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal("bad  message",document.RootElement.GetProperty("message").GetString()); Assert.Equal(new string('b',64),document.RootElement.GetProperty("definitionSha256").GetString()); Assert.Equal("probe",document.RootElement.GetProperty("probeInstanceId").GetString()); Assert.Equal("probe:alpha",document.RootElement.GetProperty("patchId").GetString()); Assert.Equal(7,document.RootElement.GetProperty("hooksVersion").GetInt64());
	}

	[Fact]
	public void Audit_rotates_to_a_bounded_number_of_files() {
		using var directory=new TemporaryDirectory(); var path=System.IO.Path.Combine(directory.Path,"audit.jsonl"); var work=new WatchWork(Definition("alpha","Target.exe"),new ProcessIdentity(5,6,"Target.exe",1));
		using(var audit=new AuditWriter(path,200,2)) for(var index=0;index<8;index++) audit.Write(work,Result("ok"),index,new string('x',40));
		Assert.True(File.Exists(path)); Assert.True(File.Exists(path+".1")); Assert.True(File.Exists(path+".2")); Assert.False(File.Exists(path+".3"));
	}

	[Fact]
	public async Task Target_exit_is_a_non_error_terminal_disposition() {
		using var directory=new TemporaryDirectory(); var path=System.IO.Path.Combine(directory.Path,"audit.jsonl");
		using(var audit=new AuditWriter(path)) { var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,apply:work=>Result("target_exited")); runner.Schedule(new[]{new ProcessIdentity(44,55,"Target.exe",1)}); await runner.DrainAsync(); }
		using var document=JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal("target_exited",document.RootElement.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null,document.RootElement.GetProperty("message").ValueKind);
	}

	static WatchDefinition Definition(string id,string fileName)=>new(id+".json",Valid(id,fileName),new string('a',64));
	static WatchApplyResult Result(string status)=>new(status,new string('a',64),null,null,null);
	static HookDefinition Valid(string id,string fileName)=>new() { SchemaVersion=1,Id=id,Process=new ProcessDefinition { FileName=fileName },Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Void Run()",IlSha256=new string('a',64) },Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static bool Prefix(){return true;}}",MaximumEventsPerSecond=10,MaximumStringLength=100 } };
	static void WriteDefinition(string directory,string name,string id,string fileName) { var options=new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }; File.WriteAllText(System.IO.Path.Combine(directory,name),JsonSerializer.Serialize(Valid(id,fileName),options)); }
	sealed class RecordingNotificationSink : IWatchNotificationSink { public ConcurrentBag<string> Values { get; }=new(); public void Publish(WatchNotification notification)=>Values.Add(notification.DefinitionId+":"+notification.Status); }
	sealed class ImmediateProcessStartSignal : IProcessStartSignal { int waits; public bool Disposed { get; private set; } public string Mode=>"subscription+polling"; public async Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation) { if(Interlocked.Increment(ref waits)==1) return true; await Task.Delay(pollingMilliseconds,cancellation); return false; } public void Dispose()=>Disposed=true; }
	sealed class TemporaryDirectory : IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-watcher-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
