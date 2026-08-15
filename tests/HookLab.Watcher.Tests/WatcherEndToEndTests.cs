using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HookLab.ApplyOnce;
using Xunit;

namespace HookLab.Watcher.Tests;

public sealed class WatcherEndToEndTests {
	[Theory]
	[InlineData("Alpha",311,"311","10")]
	[InlineData("Beta",422,"6","422")]
	public async Task Watcher_discovers_and_applies_to_distinct_real_targets(string method,int replacement,string expectedAlpha,string expectedBeta) {
		using var target=TargetRun.Start(); using var directory=new TemporaryDirectory();
		var definition=target.Definition(method,replacement); var definitionPath=Path.Combine(directory.Path,"hook.json");
		File.WriteAllText(definitionPath,JsonSerializer.Serialize(definition,new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }),new UTF8Encoding(false));
		var definitions=DefinitionCatalog.Load(directory.Path); var auditPath=Path.Combine(directory.Path,"audit.jsonl");
		using(var audit=new AuditWriter(auditPath)) {
			var runner=new WatchRunner(definitions,Process.GetCurrentProcess().SessionId,25,2,audit,null);
			runner.Schedule(ProcessDiscovery.Snapshot(new[]{"HookLab.ApplyOnceTarget.exe"}));
			await runner.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
		}
		var behavior=target.ReleaseAndRead();
		Assert.Equal(expectedAlpha,behavior["alpha"]); Assert.Equal(expectedBeta,behavior["beta"]);
		using var auditDocument=JsonDocument.Parse(File.ReadAllText(auditPath)); Assert.Equal("ok",auditDocument.RootElement.GetProperty("status").GetString());
	}

	sealed class TargetRun : IDisposable {
		readonly string directory; readonly string signalPath; readonly string resultPath; readonly Dictionary<string,string> facts; readonly Process process;
		TargetRun(string directory,string signalPath,string resultPath,Dictionary<string,string> facts,Process process) { this.directory=directory; this.signalPath=signalPath; this.resultPath=resultPath; this.facts=facts; this.process=process; }
		public static TargetRun Start() {
			var executable=Path.Combine(RepoRoot(),"tests","TestTargets","HookLab.ApplyOnceTarget","bin","Release","net48","HookLab.ApplyOnceTarget.exe"); var directory=Path.Combine(Path.GetTempPath(),"hooklab-watcher-e2e-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			var factsPath=Path.Combine(directory,"facts.txt"); var signalPath=Path.Combine(directory,"signal.txt"); var resultPath=Path.Combine(directory,"result.txt"); var start=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true };
			start.ArgumentList.Add(factsPath); start.ArgumentList.Add(signalPath); start.ArgumentList.Add(resultPath); var process=Process.Start(start)??throw new InvalidOperationException("Could not start target.");
			return new TargetRun(directory,signalPath,resultPath,ReadPairs(ReadWhenReady(factsPath,process,TimeSpan.FromSeconds(5))),process);
		}
		public HookDefinition Definition(string method,int replacement) { var prefix=method+"."; return new HookDefinition { SchemaVersion=1,Id="watcher-"+method.ToLowerInvariant(),Process=new ProcessDefinition { FileName="HookLab.ApplyOnceTarget.exe" },Target=new TargetDefinition { Assembly=facts[prefix+"assembly"],ModuleMvid=facts[prefix+"mvid"],DeclaringType=facts[prefix+"type"],Method=method,MetadataToken=Int32.Parse(facts[prefix+"token"],CultureInfo.InvariantCulture),Signature=facts[prefix+"signature"],IlSha256=facts[prefix+"il_sha256"] },Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static bool Prefix(ref int __result){__result="+replacement.ToString(CultureInfo.InvariantCulture)+";return false;}}",MaximumEventsPerSecond=10,MaximumStringLength=128 } }; }
		public Dictionary<string,string> ReleaseAndRead() { File.WriteAllText(signalPath,"go"); var text=ReadWhenReady(resultPath,process,TimeSpan.FromSeconds(5)); if(!process.WaitForExit(5000)) throw new TimeoutException("Target did not exit."); return ReadPairs(text); }
		public void Dispose() { try { if(!process.HasExited) process.Kill(); process.Dispose(); } catch { } try { Directory.Delete(directory,true); } catch { } }
	}

	static string ReadWhenReady(string path,Process process,TimeSpan timeout) { var watch=Stopwatch.StartNew(); while(watch.Elapsed<timeout) { if(File.Exists(path)) try { var text=File.ReadAllText(path); if(text.Length!=0) return text; } catch(IOException) { } if(process.HasExited&&!File.Exists(path)) throw new InvalidOperationException("Target exited before writing "+path); Thread.Sleep(10); } throw new TimeoutException("Timed out reading "+path); }
	static Dictionary<string,string> ReadPairs(string text)=>text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split(new[]{'='},2)).ToDictionary(parts=>parts[0],parts=>parts[1],StringComparer.Ordinal);
	static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null&&!Directory.Exists(Path.Combine(current.FullName,"HookLab"))) current=current.Parent; return current?.FullName??throw new DirectoryNotFoundException("Repository root not found."); }
	sealed class TemporaryDirectory : IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-watcher-definitions-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
