using System.Collections.Concurrent;
using System.Text.Json;
using HookLab.ApplyOnce;
using Xunit;

namespace HookLab.Watcher.Tests;

public sealed class WatcherTests {
	[Fact]
	public void Watcher_layout_contains_the_DPAPI_runtime_dependency()=>Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory,"System.Security.Cryptography.ProtectedData.dll")));

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
	public async Task Run_reconciles_initial_snapshot_and_drains_on_cancellation() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl")); using var cancellation=new CancellationTokenSource(); var calls=0;
		IReadOnlyList<ProcessIdentity> Snapshot() { cancellation.Cancel(); return new[]{new ProcessIdentity(4,4,"Target.exe",1)}; }
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,Snapshot,work=>{ Interlocked.Increment(ref calls); return Result("ok"); });
		await runner.RunAsync(cancellation.Token); Assert.Equal(1,calls);
	}

	[Fact]
	public async Task Immediately_completed_work_is_removed_after_registration() {
		using var directory=new TemporaryDirectory(); using var audit=new AuditWriter(System.IO.Path.Combine(directory.Path,"audit.jsonl"));
		var runner=new WatchRunner(new[]{Definition("alpha","Target.exe")},1,25,1,audit,null,apply:work=>Result("ok"));
		runner.Schedule(new[]{new ProcessIdentity(9,9,"Target.exe",1)});
		await runner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(1));
	}

	[Fact]
	public void Audit_is_json_lines_and_sanitizes_messages() {
		using var directory=new TemporaryDirectory(); var path=System.IO.Path.Combine(directory.Path,"audit.jsonl");
		using(var audit=new AuditWriter(path)) audit.Write(new WatchWork(Definition("alpha","Target.exe"),new ProcessIdentity(5,6,"Target.exe",1)),new("error",new string('b',64),"probe","probe:alpha",7),9,"bad\r\nmessage");
		using var document=JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal("bad  message",document.RootElement.GetProperty("message").GetString()); Assert.Equal(new string('b',64),document.RootElement.GetProperty("definitionSha256").GetString()); Assert.Equal("probe",document.RootElement.GetProperty("probeInstanceId").GetString()); Assert.Equal("probe:alpha",document.RootElement.GetProperty("patchId").GetString()); Assert.Equal(7,document.RootElement.GetProperty("hooksVersion").GetInt64());
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
	sealed class TemporaryDirectory : IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-watcher-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
