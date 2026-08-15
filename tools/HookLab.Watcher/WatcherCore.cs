using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using HookLab.Injector;

namespace HookLab.Watcher;

internal sealed record WatchDefinition(string Path,HookDefinition Value,string DefinitionSha256,string? ProfileId=null,string? PackageId=null,IReadOnlyList<string>? PermittedExecutablePaths=null,string NotificationPolicy="errors",int ClrReadinessTimeoutMs=5000,int InitializationTimeoutMs=5000);
internal readonly record struct ProcessIdentity(int ProcessId,long CreationUtcTicks,string FileName,int SessionId,string? ImagePath=null);
internal readonly record struct WatchKey(string DefinitionId,string DefinitionSha256,int ProcessId,long CreationUtcTicks);
internal readonly record struct WatchWork(WatchDefinition Definition,ProcessIdentity Process);
internal sealed record WatchApplyResult(string Status,string DefinitionSha256,string? ProbeInstanceId,string? PatchId,long? HooksVersion);
internal enum AttemptDisposition { Running,Completed,TargetExited,DeterministicRefusal,AmbiguousNoRetry,Retryable }
internal sealed record AttemptState(AttemptDisposition Disposition,int Attempts,DateTime RetryUtc);

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
	Dictionary<string,WatchDefinition[]> byFileName=new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<WatchKey,AttemptState> attempts=new();
	readonly object gate=new();
	readonly int sessionId;

	public CandidateTracker(IEnumerable<WatchDefinition> definitions,int sessionId) {
		this.sessionId=sessionId; Update(definitions);
	}
	public void Update(IEnumerable<WatchDefinition> definitions) { lock(gate) byFileName=definitions.GroupBy(value=>value.Value.Process!.FileName!,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.OrdinalIgnoreCase); }

	public IReadOnlyList<WatchWork> Select(IEnumerable<ProcessIdentity> processes,WatchControl? control=null,DateTime? now=null) {
		control??=WatchControl.Active; var current=now??DateTime.UtcNow; if(control.Paused) return Array.Empty<WatchWork>(); lock(gate) {
		var result=new List<WatchWork>();
		foreach(var process in processes) {
			if(process.SessionId!=sessionId||!byFileName.TryGetValue(process.FileName,out var definitions)) continue;
			foreach(var definition in definitions) {
				if(definition.ProfileId is not null&&control.DisabledProfiles.Contains(definition.ProfileId)) continue;
				if(definition.PermittedExecutablePaths is { Count:>0 } permitted&&(String.IsNullOrWhiteSpace(process.ImagePath)||!permitted.Contains(Path.GetFullPath(process.ImagePath),StringComparer.OrdinalIgnoreCase))) continue;
				var key=Key(definition,process); if(attempts.TryGetValue(key,out var state)&&(state.Disposition!=AttemptDisposition.Retryable||state.Attempts>=3||state.RetryUtc>current)) continue;
				attempts[key]=new(AttemptDisposition.Running,state?.Attempts??0,DateTime.MaxValue); result.Add(new WatchWork(definition,process));
			}
		}
		return result; }
	}
	public void Complete(WatchWork work,AttemptDisposition disposition,DateTime? now=null) { lock(gate) { var key=Key(work.Definition,work.Process); var previous=attempts.TryGetValue(key,out var state)?state:new(AttemptDisposition.Running,0,DateTime.MaxValue); var count=previous.Attempts+1; var delay=TimeSpan.FromMilliseconds(250*Math.Pow(2,Math.Min(count-1,4))); attempts[key]=new(disposition,count,disposition==AttemptDisposition.Retryable?(now??DateTime.UtcNow).Add(delay):DateTime.MaxValue); } }
	static WatchKey Key(WatchDefinition definition,ProcessIdentity process)=>new(definition.Value.Id!,definition.DefinitionSha256,process.ProcessId,process.CreationUtcTicks);
}

internal static class ProcessDiscovery {
	public static IReadOnlyList<ProcessIdentity> Snapshot(IEnumerable<string> fileNames) {
		var result=new List<ProcessIdentity>();
		foreach(var fileName in fileNames.Distinct(StringComparer.OrdinalIgnoreCase)) {
			var processName=Path.GetFileNameWithoutExtension(fileName);
			foreach(var process in Process.GetProcessesByName(processName)) using(process) {
				try { result.Add(new ProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks,process.ProcessName+".exe",process.SessionId,process.MainModule?.FileName)); }
				catch(InvalidOperationException) { }
				catch(System.ComponentModel.Win32Exception) { }
			}
		}
		return result;
	}
}

internal sealed class AuditWriter : IDisposable {
	readonly object gate=new(); readonly string path; readonly long maximumBytes; readonly int retainedFiles; StreamWriter writer;
	public AuditWriter(string path,long maximumBytes=2*1024*1024,int retainedFiles=3) { if(maximumBytes<1) throw new ArgumentOutOfRangeException(nameof(maximumBytes)); if(retainedFiles<1) throw new ArgumentOutOfRangeException(nameof(retainedFiles)); this.path=Path.GetFullPath(path); this.maximumBytes=maximumBytes; this.retainedFiles=retainedFiles; Directory.CreateDirectory(Path.GetDirectoryName(this.path)!); writer=Open(); }
	public void Write(WatchWork work,WatchApplyResult result,long elapsedMs,string? message) {
		var value=new { timestampUtc=DateTime.UtcNow.ToString("O"),status=result.Status,definitionId=work.Definition.Value.Id,definitionPath=work.Definition.Path,definitionSha256=result.DefinitionSha256,profileId=work.Definition.ProfileId,packageId=work.Definition.PackageId,processId=work.Process.ProcessId,processCreationUtcTicks=work.Process.CreationUtcTicks,probeInstanceId=result.ProbeInstanceId,patchId=result.PatchId,hooksVersion=result.HooksVersion,elapsedMs,message=Sanitize(message) };
		lock(gate) { var line=JsonSerializer.Serialize(value); if(writer.BaseStream.Length>0&&writer.BaseStream.Length+System.Text.Encoding.UTF8.GetByteCount(line)+Environment.NewLine.Length>maximumBytes) Rotate(); writer.WriteLine(line); }
	}
	StreamWriter Open() { var value=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)); value.AutoFlush=true; return value; }
	void Rotate() { writer.Dispose(); var oldest=path+"."+retainedFiles; if(File.Exists(oldest)) File.Delete(oldest); for(var index=retainedFiles-1;index>=1;index--) { var source=path+"."+index; if(File.Exists(source)) File.Move(source,path+"."+(index+1),true); } File.Move(path,path+".1",true); writer=Open(); }
	public void Dispose() { lock(gate) writer.Dispose(); }
	static string? Sanitize(string? value)=>value is null?null:value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed class WatchRunner {
	readonly IWatchCatalog catalog; readonly CandidateTracker tracker; readonly int pollMilliseconds; readonly SemaphoreSlim parallel; readonly AuditWriter audit; readonly string? payloadDirectory; readonly WatchControlStore control; readonly WatcherStatusStore? statusStore;
	readonly Func<IReadOnlyList<ProcessIdentity>>? snapshot;
	readonly Func<WatchWork,WatchApplyResult> apply; readonly IWatchNotificationSink notifications; readonly IProcessStartSignal processStarts;
	readonly ConcurrentDictionary<WatchKey,Task> running=new();
	public WatchRunner(IReadOnlyList<WatchDefinition> definitions,int sessionId,int pollMilliseconds,int maximumParallel,AuditWriter audit,string? payloadDirectory,Func<IReadOnlyList<ProcessIdentity>>? snapshot=null,Func<WatchWork,WatchApplyResult>? apply=null,WatchControlStore? control=null,WatcherStatusStore? statusStore=null,IWatchNotificationSink? notifications=null,IProcessStartSignal? processStarts=null):this(new StaticWatchCatalog(definitions),sessionId,pollMilliseconds,maximumParallel,audit,payloadDirectory,snapshot,apply,control,statusStore,notifications,processStarts) { }
	public WatchRunner(IWatchCatalog catalog,int sessionId,int pollMilliseconds,int maximumParallel,AuditWriter audit,string? payloadDirectory,Func<IReadOnlyList<ProcessIdentity>>? snapshot=null,Func<WatchWork,WatchApplyResult>? apply=null,WatchControlStore? control=null,WatcherStatusStore? statusStore=null,IWatchNotificationSink? notifications=null,IProcessStartSignal? processStarts=null) { this.catalog=catalog; var initial=catalog.Current(); tracker=new CandidateTracker(initial.Definitions,sessionId); this.pollMilliseconds=pollMilliseconds; parallel=new SemaphoreSlim(maximumParallel); this.audit=audit; this.payloadDirectory=payloadDirectory; this.snapshot=snapshot; var coordinator=new ResidentCoordinator(payloadDirectory); this.apply=apply??(work=>Map(coordinator.Apply(Request(work)))); this.control=control??new WatchControlStore(Path.Combine(Path.GetTempPath(),"hooklab-watcher-control-"+Guid.NewGuid().ToString("N")+".json")); this.statusStore=statusStore; this.notifications=notifications??new ConsoleWatchNotificationSink(); this.processStarts=processStarts??new PollingProcessStartSignal(); }
	internal static InjectorRequest Request(WatchWork work)=>new(work.Process.ProcessId,work.Process.CreationUtcTicks,work.Definition.Path,work.Definition.DefinitionSha256,work.Definition.Value,work.Definition.PermittedExecutablePaths is { Count:>0 }?work.Process.ImagePath:null,work.Definition.ClrReadinessTimeoutMs,work.Definition.InitializationTimeoutMs);
	internal static WatchApplyResult Map(InjectorResult result)=>new(result.Status,result.DefinitionSha256,result.ProbeInstanceId,result.PatchId,result.HooksVersion);
	public async Task RunAsync(CancellationToken cancellation) {
		statusStore?.SetLifecycle("running"); statusStore?.SetDiscoveryMode(processStarts.Mode);
		try { while(!cancellation.IsCancellationRequested) {
			var current=catalog.Current(); tracker.Update(current.Definitions); var controlState=control.Read(); statusStore?.Publish(controlState,current); var processes=snapshot?.Invoke()??ProcessDiscovery.Snapshot(current.Definitions.Select(value=>value.Value.Process!.FileName!)); if(!cancellation.IsCancellationRequested&&!controlState.Paused) Schedule(processes,controlState);
			try { statusStore?.RecordDiscoveryWake(await processStarts.WaitAsync(pollMilliseconds,cancellation)); } catch(OperationCanceledException) { break; }
		} }
		finally { processStarts.Dispose(); PublishLifecycle("stopping"); await DrainAsync(); PublishLifecycle("stopped"); }
	}
	void PublishLifecycle(string lifecycle) { if(statusStore is null) return; try { statusStore.SetLifecycle(lifecycle); statusStore.Publish(control.Read(),catalog.Current()); } catch(Exception ex) { Console.Error.WriteLine("HookLab watcher could not persist "+lifecycle+" lifecycle state: "+ex.Message); } }
	internal void Schedule(IEnumerable<ProcessIdentity> processes,WatchControl? controlState=null) {
		foreach(var work in tracker.Select(processes,controlState)) {
			var key=new WatchKey(work.Definition.Value.Id!,work.Definition.DefinitionSha256,work.Process.ProcessId,work.Process.CreationUtcTicks);
			var task=ApplyAsync(work); running[key]=task;
			_ = task.ContinueWith(completedTask=>running.TryRemove(key,out var removedTask),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
		}
	}
	internal async Task DrainAsync() { while(!running.IsEmpty) await Task.WhenAll(running.Values); }
	async Task ApplyAsync(WatchWork work) {
		await parallel.WaitAsync(); var watch=Stopwatch.StartNew();
		try {
			WatchApplyResult result; Exception? failure=null; AttemptDisposition disposition;
			try { result=await Task.Run(()=>apply(work)); disposition=result.Status=="target_exited"?AttemptDisposition.TargetExited:AttemptDisposition.Completed; }
			catch(Exception ex) { failure=ex; result=new("error",work.Definition.DefinitionSha256,null,null,null); disposition=Classify(ex); }
			tracker.Complete(work,disposition); PersistResult(work,result,watch.ElapsedMilliseconds,failure?.Message); Notify(work,result,failure?.Message);
			if(failure is null) Console.WriteLine("HookLab watcher "+result.Status+" "+work.Definition.Value.Id+" for PID "+work.Process.ProcessId+" in "+watch.ElapsedMilliseconds+" ms.");
			else Console.Error.WriteLine("HookLab watcher failed "+work.Definition.Value.Id+" for PID "+work.Process.ProcessId+": "+failure.Message);
		}
		finally { parallel.Release(); }
	}
	void PersistResult(WatchWork work,WatchApplyResult result,long elapsedMs,string? message) { try { audit.Write(work,result,elapsedMs,message); statusStore?.Publish(control.Read(),catalog.Current(),work,result.Status,elapsedMs,message); } catch(Exception ex) { Console.Error.WriteLine("HookLab watcher could not persist result for "+work.Definition.Value.Id+" PID "+work.Process.ProcessId+": "+ex.Message); } }
	void Notify(WatchWork work,WatchApplyResult result,string? message) { var policy=work.Definition.NotificationPolicy; if(policy=="none"||policy=="errors"&&result.Status!="error") return; try { notifications.Publish(new(work.Definition.ProfileId,work.Definition.Value.Id!,work.Process.ProcessId,result.Status,message)); } catch(Exception ex) { Console.Error.WriteLine("HookLab watcher notification failed for "+work.Definition.Value.Id+" PID "+work.Process.ProcessId+": "+ex.Message); } }
	internal static AttemptDisposition Classify(Exception ex) { var failure=CommandFailure.Classify(ex); return failure.Code switch { "target_exited"=>AttemptDisposition.TargetExited,"deterministic_conflict" or "invalid_input" or "access_denied"=>AttemptDisposition.DeterministicRefusal,"runtime_not_ready"=>AttemptDisposition.Retryable,"timeout_known_state_required"=>AttemptDisposition.AmbiguousNoRetry,_ when ex is IOException=>AttemptDisposition.Retryable,_=>AttemptDisposition.AmbiguousNoRetry }; }
}
