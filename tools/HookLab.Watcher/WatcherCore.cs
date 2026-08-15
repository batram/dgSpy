using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using HookLab.ApplyOnce;

namespace HookLab.Watcher;

internal sealed record WatchDefinition(string Path,HookDefinition Value,string DefinitionSha256);
internal readonly record struct ProcessIdentity(int ProcessId,long CreationUtcTicks,string FileName,int SessionId);
internal readonly record struct WatchKey(string DefinitionId,int ProcessId,long CreationUtcTicks);
internal readonly record struct WatchWork(WatchDefinition Definition,ProcessIdentity Process);

internal static class DefinitionCatalog {
	public static IReadOnlyList<WatchDefinition> Load(string directory) {
		var root=Path.GetFullPath(directory);
		if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Definitions directory does not exist: "+root);
		var paths=Directory.GetFiles(root,"*.json",SearchOption.TopDirectoryOnly).OrderBy(value=>value,StringComparer.OrdinalIgnoreCase).ToArray();
		if(paths.Length==0) throw new InvalidDataException("Definitions directory contains no JSON definitions: "+root);
		var result=paths.Select(path=>new WatchDefinition(path,HookDefinition.Load(path),Sha256(path))).ToArray();
		var duplicate=result.GroupBy(value=>value.Value.Id!,StringComparer.Ordinal).FirstOrDefault(group=>group.Count()>1);
		if(duplicate is not null) throw new InvalidDataException("Duplicate definition id: "+duplicate.Key);
		return result;
	}
	static string Sha256(string path) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant(); }
}

internal sealed class CandidateTracker {
	readonly Dictionary<string,WatchDefinition[]> byFileName;
	readonly HashSet<WatchKey> attempted=new();
	readonly int sessionId;

	public CandidateTracker(IEnumerable<WatchDefinition> definitions,int sessionId) {
		this.sessionId=sessionId;
		byFileName=definitions.GroupBy(value=>value.Value.Process!.FileName!,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.OrdinalIgnoreCase);
	}

	public IReadOnlyList<WatchWork> Select(IEnumerable<ProcessIdentity> processes) {
		var result=new List<WatchWork>();
		foreach(var process in processes) {
			if(process.SessionId!=sessionId||!byFileName.TryGetValue(process.FileName,out var definitions)) continue;
			foreach(var definition in definitions) {
				var key=new WatchKey(definition.Value.Id!,process.ProcessId,process.CreationUtcTicks);
				if(attempted.Add(key)) result.Add(new WatchWork(definition,process));
			}
		}
		return result;
	}
}

internal static class ProcessDiscovery {
	public static IReadOnlyList<ProcessIdentity> Snapshot(IEnumerable<string> fileNames) {
		var result=new List<ProcessIdentity>();
		foreach(var fileName in fileNames.Distinct(StringComparer.OrdinalIgnoreCase)) {
			var processName=Path.GetFileNameWithoutExtension(fileName);
			foreach(var process in Process.GetProcessesByName(processName)) using(process) {
				try { result.Add(new ProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks,process.ProcessName+".exe",process.SessionId)); }
				catch(InvalidOperationException) { }
				catch(System.ComponentModel.Win32Exception) { }
			}
		}
		return result;
	}
}

internal sealed class AuditWriter : IDisposable {
	readonly object gate=new(); readonly StreamWriter writer;
	public AuditWriter(string path) { var full=Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); writer=new StreamWriter(new FileStream(full,FileMode.Append,FileAccess.Write,FileShare.Read)); writer.AutoFlush=true; }
	public void Write(WatchWork work,WatchApplyResult result,long elapsedMs,string? message) {
		var value=new { timestampUtc=DateTime.UtcNow.ToString("O"),status=result.Status,definitionId=work.Definition.Value.Id,definitionPath=work.Definition.Path,definitionSha256=result.DefinitionSha256,processId=work.Process.ProcessId,processCreationUtcTicks=work.Process.CreationUtcTicks,probeInstanceId=result.ProbeInstanceId,patchId=result.PatchId,hooksVersion=result.HooksVersion,elapsedMs,message=Sanitize(message) };
		lock(gate) writer.WriteLine(JsonSerializer.Serialize(value));
	}
	public void Dispose()=>writer.Dispose();
	static string? Sanitize(string? value)=>value is null?null:value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed class WatchRunner {
	readonly IReadOnlyList<WatchDefinition> definitions; readonly CandidateTracker tracker; readonly int pollMilliseconds; readonly SemaphoreSlim parallel; readonly AuditWriter audit; readonly string? payloadDirectory;
	readonly Func<IReadOnlyList<ProcessIdentity>> snapshot;
	readonly Func<WatchWork,WatchApplyResult> apply;
	readonly ConcurrentDictionary<WatchKey,Task> running=new();
	public WatchRunner(IReadOnlyList<WatchDefinition> definitions,int sessionId,int pollMilliseconds,int maximumParallel,AuditWriter audit,string? payloadDirectory,Func<IReadOnlyList<ProcessIdentity>>? snapshot=null,Func<WatchWork,WatchApplyResult>? apply=null) { this.definitions=definitions; tracker=new CandidateTracker(definitions,sessionId); this.pollMilliseconds=pollMilliseconds; parallel=new SemaphoreSlim(maximumParallel); this.audit=audit; this.payloadDirectory=payloadDirectory; var names=definitions.Select(value=>value.Value.Process!.FileName!).ToArray(); this.snapshot=snapshot??(()=>ProcessDiscovery.Snapshot(names)); var coordinator=new ResidentCoordinator(payloadDirectory); this.apply=apply??coordinator.Apply; }
	public async Task RunAsync(CancellationToken cancellation) {
		while(!cancellation.IsCancellationRequested) {
			Schedule(snapshot());
			try { await Task.Delay(pollMilliseconds,cancellation); } catch(OperationCanceledException) { break; }
		}
		await Task.WhenAll(running.Values);
	}
	internal void Schedule(IEnumerable<ProcessIdentity> processes) {
		foreach(var work in tracker.Select(processes)) {
			var key=new WatchKey(work.Definition.Value.Id!,work.Process.ProcessId,work.Process.CreationUtcTicks);
			var task=ApplyAsync(work); running[key]=task;
			_ = task.ContinueWith(completedTask=>running.TryRemove(key,out var removedTask),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
		}
	}
	internal async Task DrainAsync() { while(!running.IsEmpty) await Task.WhenAll(running.Values); }
	async Task ApplyAsync(WatchWork work) {
		await parallel.WaitAsync(); var watch=Stopwatch.StartNew();
		try { var result=await Task.Run(()=>apply(work)); audit.Write(work,result,watch.ElapsedMilliseconds,null); Console.WriteLine("HookLab watcher "+result.Status+" "+work.Definition.Value.Id+" for PID "+work.Process.ProcessId+" in "+watch.ElapsedMilliseconds+" ms."); }
		catch(Exception ex) { audit.Write(work,new("error",work.Definition.DefinitionSha256,null,null,null),watch.ElapsedMilliseconds,ex.Message); Console.Error.WriteLine("HookLab watcher failed "+work.Definition.Value.Id+" for PID "+work.Process.ProcessId+": "+ex.Message); }
		finally { parallel.Release(); }
	}
}
