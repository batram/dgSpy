using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HookLab.ApplyOnce.Tests;

public sealed class ApplyOnceEndToEndTests {
	[Theory]
	[InlineData("Alpha",111,111,10)]
	[InlineData("Beta",222,6,222)]
	public void External_definition_changes_only_its_exact_target(string method,int replacement,int expectedAlpha,int expectedBeta) {
		using var run=TargetRun.Start();
		var definition=run.Definition(method,replacement);
		var apply=run.Apply(definition);
		Assert.True(apply.ExitCode==0,apply.Output);
		var report=ReadPairs(File.ReadAllText(OneShotInjector.ResultPath(run.Process.Id)));
		Assert.Equal("ok",report["status"]);
		Assert.Equal(definition.Id,report["definition_id"]);
		Assert.Equal(Sha256(run.DefinitionPath),report["definition_sha256"]);
		var behavior=run.ReleaseAndRead();
		Assert.Equal(expectedAlpha,Int32.Parse(behavior["alpha"],CultureInfo.InvariantCulture));
		Assert.Equal(expectedBeta,Int32.Parse(behavior["beta"],CultureInfo.InvariantCulture));
	}

	[Theory]
	[InlineData("mvid")]
	[InlineData("token")]
	[InlineData("signature")]
	[InlineData("il")]
	[InlineData("assembly")]
	[InlineData("type")]
	[InlineData("method")]
	public void Changed_exact_guard_fails_closed_and_leaves_original_behavior(string guard) {
		using var run=TargetRun.Start();
		var definition=run.Definition("Alpha",333);
		switch(guard) {
			case "mvid": definition.Target!.ModuleMvid=Guid.NewGuid().ToString("D"); break;
			case "token": definition.Target!.MetadataToken++; break;
			case "signature": definition.Target!.Signature="System.Int32 Alpha(System.Int64)"; break;
			case "il": definition.Target!.IlSha256=new string('0',64); break;
			case "assembly": definition.Target!.Assembly="WrongAssembly"; break;
			case "type": definition.Target!.DeclaringType="Wrong.Target"; break;
			case "method": definition.Target!.Method="WrongMethod"; break;
		}
		var apply=run.Apply(definition);
		Assert.NotEqual(0,apply.ExitCode);
		var behavior=run.ReleaseAndRead();
		Assert.Equal("6",behavior["alpha"]);
		Assert.Equal("10",behavior["beta"]);
	}

	[Fact]
	public void Process_basename_mismatch_fails_before_injection() {
		using var run=TargetRun.Start();
		var definition=run.Definition("Alpha",444);
		definition.Process!.FileName="NotTheTarget.exe";
		var apply=run.Apply(definition);
		Assert.NotEqual(0,apply.ExitCode);
		Assert.Contains("is not NotTheTarget.exe",apply.Output,StringComparison.Ordinal);
		var behavior=run.ReleaseAndRead();
		Assert.Equal("6",behavior["alpha"]);
	}

	[Fact]
	public void Serialized_parameters_come_entirely_from_definition() {
		var definition=TargetRun.SampleDefinition();
		var text=OneShotInjector.Parameters("C:\\Target.exe",123,456,"C:\\completion.txt",definition);
		var values=ReadPairs(text);
		Assert.Equal(definition.Id,values["hook_id"]);
		Assert.Equal(definition.Target!.ModuleMvid,values["hook_module_mvid"]);
		Assert.Equal(definition.Target.MetadataToken.ToString(CultureInfo.InvariantCulture),values["hook_metadata_token"]);
		Assert.Equal(definition.Target.DeclaringType,values["hook_type"]);
		Assert.Equal(definition.Target.Method,values["hook_method"]);
		Assert.Equal(definition.Target.Signature,values["hook_method_signature"]);
		Assert.Equal(definition.Target.IlSha256,values["hook_il_sha256"]);
		Assert.Equal(definition.Hook!.Kind,values["hook_kind"]);
		Assert.Equal(definition.Hook.Revision.ToString(CultureInfo.InvariantCulture),values["hook_revision"]);
		Assert.Equal(definition.Hook.Source,Encoding.UTF8.GetString(Convert.FromBase64String(values["hook_source_base64"])));
	}

	static Dictionary<string,string> ReadPairs(string text)=>text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split(new[]{'='},2)).ToDictionary(parts=>parts[0],parts=>parts[1],StringComparer.Ordinal);
	static string Sha256(string path) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant(); }

	sealed class TargetRun : IDisposable {
		readonly string directory;
		readonly string factsPath;
		readonly string signalPath;
		readonly string resultPath;
		readonly Dictionary<string,string> facts;
		public Process Process { get; }
		public string DefinitionPath=>Path.Combine(directory,"definition.json");

		TargetRun(string directory,Process process,string factsPath,string signalPath,string resultPath,Dictionary<string,string> facts) { this.directory=directory; Process=process; this.factsPath=factsPath; this.signalPath=signalPath; this.resultPath=resultPath; this.facts=facts; }

		public static TargetRun Start() {
			var root=RepoRoot();
			var executable=Path.Combine(root,"tests","TestTargets","HookLab.ApplyOnceTarget","bin","Release","net48","HookLab.ApplyOnceTarget.exe");
			if(!File.Exists(executable)) throw new FileNotFoundException("Build the ApplyOnce target first.",executable);
			var directory=Path.Combine(Path.GetTempPath(),"hooklab-apply-once-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			var facts=Path.Combine(directory,"facts.txt"); var signal=Path.Combine(directory,"signal.txt"); var result=Path.Combine(directory,"result.txt");
			var start=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true };
			start.ArgumentList.Add(facts); start.ArgumentList.Add(signal); start.ArgumentList.Add(result);
			var process=Process.Start(start)??throw new InvalidOperationException("Could not start target.");
			var factsText=ReadFileWhenReady(facts,process,TimeSpan.FromSeconds(5));
			return new TargetRun(directory,process,facts,signal,result,ReadPairs(factsText));
		}

		public HookDefinition Definition(string method,int replacement) {
			var prefix=method+".";
			return new HookDefinition {
				SchemaVersion=1,Id="test-"+method.ToLowerInvariant()+"-"+replacement,Process=new ProcessDefinition { FileName=Path.GetFileName(Process.MainModule!.FileName) },
				Target=new TargetDefinition { Assembly=facts[prefix+"assembly"],ModuleMvid=facts[prefix+"mvid"],DeclaringType=facts[prefix+"type"],Method=method,MetadataToken=Int32.Parse(facts[prefix+"token"],CultureInfo.InvariantCulture),Signature=facts[prefix+"signature"],IlSha256=facts[prefix+"il_sha256"] },
				Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static bool Prefix(ref int __result){__result="+replacement.ToString(CultureInfo.InvariantCulture)+";return false;}}",MaximumEventsPerSecond=10,MaximumStringLength=128 }
			};
		}

		public static HookDefinition SampleDefinition()=>new() { SchemaVersion=1,Id="sample",Process=new ProcessDefinition { FileName="Target.exe" },Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Int32 Run(System.Int32)",IlSha256=new string('a',64) },Hook=new PatchDefinition { Kind="Prefix",Revision=7,Source="public static class H{public static bool Prefix(){return true;}}",MaximumEventsPerSecond=9,MaximumStringLength=99 } };

		public ApplyResult Apply(HookDefinition definition) {
			var options=new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true };
			File.WriteAllText(DefinitionPath,JsonSerializer.Serialize(definition,options),new UTF8Encoding(false));
			var root=RepoRoot(); var apply=Path.Combine(root,"tools","HookLab.ApplyOnce","bin","Release","net10.0-windows","win-x64","HookLab.ApplyOnce.dll");
			var payload=Path.Combine(directory,"payload"); Directory.CreateDirectory(payload);
			File.Copy(Path.Combine(root,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll"),Path.Combine(payload,"HookLab.NativeBootstrap.x64.dll"),true);
			File.Copy(Path.Combine(root,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll"),Path.Combine(payload,"HookLab.Bootstrap.dll"),true);
			var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
			foreach(var value in new[]{apply,"--pid",Process.Id.ToString(CultureInfo.InvariantCulture),"--definition",DefinitionPath,"--payload-dir",payload}) start.ArgumentList.Add(value);
			using var child=Process.Start(start)??throw new InvalidOperationException("Could not start ApplyOnce.");
			var output=child.StandardOutput.ReadToEnd()+child.StandardError.ReadToEnd();
			if(!child.WaitForExit(15000)) { child.Kill(); throw new TimeoutException("ApplyOnce did not exit."); }
			return new ApplyResult(child.ExitCode,output);
		}

		public Dictionary<string,string> ReleaseAndRead() { File.WriteAllText(signalPath,"go"); var resultText=ReadFileWhenReady(resultPath,Process,TimeSpan.FromSeconds(10)); if(!Process.WaitForExit(5000)) throw new TimeoutException("Target did not exit."); return ReadPairs(resultText); }
		public void Dispose() { try { if(!Process.HasExited) Process.Kill(); Process.Dispose(); } catch { } try { Directory.Delete(directory,true); } catch { } }
		static string ReadFileWhenReady(string path,Process process,TimeSpan timeout) {
			var watch=Stopwatch.StartNew();
			while(watch.Elapsed<timeout) {
				if(File.Exists(path)) {
					try { var text=File.ReadAllText(path); if(text.Length!=0) return text; }
					catch(IOException) { }
					catch(UnauthorizedAccessException) { }
				}
				if(process.HasExited&&!File.Exists(path)) throw new InvalidOperationException("Target exited before writing "+path);
				Thread.Sleep(10);
			}
			throw new TimeoutException("Timed out waiting to read "+path);
		}
		static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null&&!Directory.Exists(Path.Combine(current.FullName,"HookLab"))) current=current.Parent; return current?.FullName??throw new DirectoryNotFoundException("Repository root not found."); }
	}

	readonly record struct ApplyResult(int ExitCode,string Output);
}
