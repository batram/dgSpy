using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HookLab.Contracts;

namespace HookLab.Injector;

public static class OneShotInjector {
	public static string Apply(int processId,HookDefinition definition,string definitionPath,string? payloadDirectory)=>ApplyCore(processId,definition,definitionPath,payloadDirectory,"none",null,HookOwnership.Qualify(HookOwnership.WatcherController,definition.Id!),clrReadinessTimeoutMs:5000,initializationTimeoutMs:5000).Result;
	public static ResidentInjection ApplyResident(int processId,HookDefinition definition,string definitionPath,string? payloadDirectory,byte[] endpointSecret,string residentStagingDirectory,int clrReadinessTimeoutMs=5000,int initializationTimeoutMs=5000) {
		if(endpointSecret is null||endpointSecret.Length!=32) throw new ArgumentException("The resident endpoint secret must be exactly 32 bytes.",nameof(endpointSecret));
		if(String.IsNullOrWhiteSpace(residentStagingDirectory)) throw new ArgumentException("A resident staging directory is required.",nameof(residentStagingDirectory));
		var outcome=ApplyCore(processId,definition,definitionPath,payloadDirectory,"pipe",endpointSecret,HookOwnership.Qualify(HookOwnership.WatcherController,definition.Id!),residentStagingDirectory,clrReadinessTimeoutMs,initializationTimeoutMs);
		return new ResidentInjection(outcome.Result,outcome.ImagePath,outcome.CreationTicks,Required(outcome.Report,"probe_instance_id"),Required(outcome.Report,"pipe_name"),Convert.FromBase64String(Required(outcome.Report,"pipe_nonce_base64")),Required(outcome.Report,"patch_id"),Int64.Parse(Required(outcome.Report,"hooks_version"),CultureInfo.InvariantCulture));
	}
	static InjectionOutcome ApplyCore(int processId,HookDefinition definition,string definitionPath,string? payloadDirectory,string endpoint,byte[]? endpointSecret,string residentHookId,string? residentStagingDirectory=null,int clrReadinessTimeoutMs=5000,int initializationTimeoutMs=5000) {
		if(clrReadinessTimeoutMs is < 250 or > 30000) throw new ArgumentOutOfRangeException(nameof(clrReadinessTimeoutMs));
		if(initializationTimeoutMs is < 1000 or > 30000) throw new ArgumentOutOfRangeException(nameof(initializationTimeoutMs));
		using var process=Process.GetProcessById(processId);
		string imagePath; long creationTicks;
		try { imagePath=process.MainModule?.FileName ?? throw new InvalidOperationException("The target image path is unavailable."); creationTicks=process.StartTime.ToUniversalTime().Ticks; }
		catch(Win32Exception ex) { throw new InvalidOperationException("Could not read target process identity. Run ApplyOnce elevated when the target is elevated.",ex); }
		if(!String.Equals(Path.GetFileName(imagePath),definition.Process!.FileName,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PID "+processId+" is not "+definition.Process.FileName+"; observed "+imagePath+".");
		EnsureX64(processId);
		WaitForClr(process,TimeSpan.FromMilliseconds(clrReadinessTimeoutMs));
		var payload=payloadDirectory is null?FindPayload():PayloadFiles.InDirectory(payloadDirectory);
		var staging=residentStagingDirectory is null?Path.Combine(Path.GetTempPath(),"hooklab-apply-once-"+processId.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")):Path.GetFullPath(residentStagingDirectory);
		Directory.CreateDirectory(staging);
		var completion=Path.Combine(staging,"completion.txt");
		var completed=false; var targetExited=false;
		try {
			var nativePath=Path.Combine(staging,"HookLab.NativeBootstrap.x64.dll");
			File.Copy(payload.Native,nativePath,false);
			File.Copy(payload.Managed,Path.Combine(staging,"HookLab.Bootstrap.dll"),false);
			File.WriteAllText(Path.Combine(staging,"initialize.params"),Parameters(imagePath,processId,creationTicks,completion,definition,endpoint,endpointSecret,residentHookId),new UTF8Encoding(false));
			RemoteLibraryLoader.Load(processId,nativePath);
			var wait=Stopwatch.StartNew();
			var report=WaitForCompletion(process,completion,TimeSpan.FromMilliseconds(initializationTimeoutMs));
			completed=true;
			var values=ParseReport(report);
			if(!values.TryGetValue("status",out var status)||status!="ok") throw new InvalidOperationException("The target reported: "+report.Replace('\r',' ').Replace('\n',' '));
			if(!values.TryGetValue("patch_id",out var patchId)||String.IsNullOrWhiteSpace(patchId)) throw new InvalidOperationException("The target reported success without a patch_id.");
			var result="status=ok\nprocess_id="+processId.ToString(CultureInfo.InvariantCulture)+"\nprocess_creation_utc_ticks="+creationTicks.ToString(CultureInfo.InvariantCulture)+"\ndefinition_id="+definition.Id+"\ndefinition_path="+definitionPath+"\ndefinition_sha256="+Sha256(definitionPath)+"\npatch_id="+patchId+"\nhooks_version="+(values.TryGetValue("hooks_version",out var version)?version:"unknown")+"\nlauncher_wait_ms="+wait.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)+"\nworker_queue_ms="+(values.TryGetValue("worker_queue_ms",out var queue)?queue:"missing")+"\nbehavior_elapsed_ms="+(values.TryGetValue("behavior_elapsed_ms",out var elapsed)?elapsed:"missing")+"\n";
			WriteResult(processId,result);
			return new InjectionOutcome(result,imagePath,creationTicks,values);
		}
		catch(Exception ex) when(HasExited(process)) { targetExited=true; throw new TargetExitedException("Target exited during HookLab initialization.",ex); }
		finally {
			if(residentStagingDirectory is null&&(completed||targetExited)) TryDeleteDirectory(staging);
			else if(residentStagingDirectory is not null&&targetExited) TryDeleteDirectory(staging);
			else if(!completed) Console.Error.WriteLine("Preserved ambiguous HookLab staging directory: "+staging);
		}
	}

	public static string Parameters(string imagePath,int processId,long creationTicks,string completion,HookDefinition definition,string endpoint="none",byte[]? endpointSecret=null,string? residentHookId=null) {
		var target=definition.Target!; var hook=definition.Hook!;
		var source=Convert.ToBase64String(Encoding.UTF8.GetBytes(hook.Source!));
		var values=new[] {
			Pair("host_id",HookLab.Host.Transport.Discovery.DgSpyStateRoot.ResidentHostId),Pair("image_path",imagePath),Pair("process_id",processId.ToString(CultureInfo.InvariantCulture)),Pair("process_creation_utc_ticks",creationTicks.ToString(CultureInfo.InvariantCulture)),
			Pair("architecture","x64"),Pair("runtime_id","v4.0.30319"),Pair("appdomain_id","1"),Pair("endpoint",endpoint),Pair("completion_path",completion),Pair("hook_id",residentHookId??definition.Id!),
			Pair("hook_kind",hook.Kind!),Pair("hook_assembly",target.Assembly!),Pair("hook_type",target.DeclaringType!),Pair("hook_method",target.Method!),Pair("hook_module_mvid",target.ModuleMvid!),
			Pair("hook_metadata_token",target.MetadataToken.ToString(CultureInfo.InvariantCulture)),Pair("hook_declaring_type",target.DeclaringType!),Pair("hook_method_signature",target.Signature!),Pair("hook_il_sha256",target.IlSha256!),
			Pair("hook_source_base64",source),Pair("hook_revision",hook.Revision.ToString(CultureInfo.InvariantCulture)),Pair("maximum_events_per_second",hook.MaximumEventsPerSecond.ToString(CultureInfo.InvariantCulture)),Pair("maximum_string_length",hook.MaximumStringLength.ToString(CultureInfo.InvariantCulture))
		};
		return String.Join("\n",endpointSecret is null?values:values.Concat(new[]{Pair("endpoint_secret_base64",Convert.ToBase64String(endpointSecret))}))+"\n";
	}

	public static void WriteResult(int processId,string result) { try { File.WriteAllText(ResultPath(processId),result,new UTF8Encoding(false)); } catch { } }
	public static string ResultPath(int processId)=>Path.Combine(Path.GetTempPath(),"HookLab.ApplyOnce-"+processId.ToString(CultureInfo.InvariantCulture)+".result.txt");
	static string Pair(string key,string value) { if(value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidOperationException("Bootstrap value contains a newline: "+key+"."); return key+"="+value; }
	static string Sha256(string path) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant(); }
	static Dictionary<string,string> ParseReport(string text) { var result=new Dictionary<string,string>(StringComparer.Ordinal); foreach(var line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) { var separator=line.IndexOf('='); if(separator>0) result[line[..separator]]=line[(separator+1)..]; } return result; }
	static string Required(Dictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!String.IsNullOrWhiteSpace(value)?value:throw new InvalidOperationException("The target completion report omitted "+key+".");
	static void WaitForClr(Process process,TimeSpan timeout) { var watch=Stopwatch.StartNew(); while(watch.Elapsed<timeout) { if(HasExited(process)) throw new InvalidOperationException("Target exited before CLR v4 became ready."); try { if(process.Modules.Cast<ProcessModule>().Any(module=>String.Equals(module.ModuleName,"clr.dll",StringComparison.OrdinalIgnoreCase))) return; } catch(Win32Exception) { } Thread.Sleep(25); } throw new ClrReadinessTimeoutException("Target CLR v4 was not ready within "+timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)+" ms; no native injection was attempted."); }
	static string WaitForCompletion(Process process,string path,TimeSpan timeout) { var watch=Stopwatch.StartNew(); while(watch.Elapsed<timeout) { if(process.HasExited) throw new InvalidOperationException("Target exited before HookLab initialization completed."); if(File.Exists(path)) return File.ReadAllText(path); Thread.Sleep(25); } throw new TimeoutException("HookLab did not publish its completion report within "+timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)+" seconds. Staging was preserved; do not retry this PID blindly."); }
	public static bool HasExited(Process process) { try { return process.HasExited; } catch(InvalidOperationException) { return true; } }
	static void TryDeleteDirectory(string path) { try { Directory.Delete(path,true); } catch { Console.Error.WriteLine("Warning: temporary bootstrap files remain at "+path); } }

	static PayloadFiles FindPayload() {
		foreach(var candidate in new[]{AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"hooklab")}) { var payload=PayloadFiles.TryInDirectory(candidate); if(payload is not null) return payload.Value; }
		for(var current=new DirectoryInfo(Environment.CurrentDirectory);current is not null;current=current.Parent) {
			var managed=Path.Combine(current.FullName,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll");
			var native=Path.Combine(current.FullName,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll");
			if(File.Exists(managed)&&File.Exists(native)) return new PayloadFiles(native,managed);
		}
		throw new FileNotFoundException("Could not find HookLab bootstrap payloads. Build them first or pass --payload-dir.");
	}
	static string RequireFile(string directory,string name) { var path=Path.GetFullPath(Path.Combine(directory,name)); if(!File.Exists(path)) throw new FileNotFoundException("Required payload is missing: "+path,path); return path; }
	static void EnsureX64(int processId) { if(!Environment.Is64BitProcess) throw new InvalidOperationException("ApplyOnce must run as x64."); var handle=OpenProcessForArchitecture(0x1000,false,processId); if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open target architecture."); try { if(!IsWow64Process2(handle,out var processMachine,out var nativeMachine)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not determine target architecture."); if(processMachine!=0||nativeMachine!=0x8664) throw new InvalidOperationException("The target is not a native x64 process."); } finally { CloseArchitectureHandle(handle); } }
	[DllImport("kernel32.dll",SetLastError=true)] static extern bool IsWow64Process2(IntPtr process,out ushort processMachine,out ushort nativeMachine);
	[DllImport("kernel32.dll",SetLastError=true,EntryPoint="OpenProcess")] static extern IntPtr OpenProcessForArchitecture(uint access,bool inheritHandle,int processId);
	[DllImport("kernel32.dll",SetLastError=true,EntryPoint="CloseHandle")] static extern bool CloseArchitectureHandle(IntPtr handle);
	readonly record struct PayloadFiles(string Native,string Managed) { public static PayloadFiles InDirectory(string directory)=>new(RequireFile(directory,"HookLab.NativeBootstrap.x64.dll"),RequireFile(directory,"HookLab.Bootstrap.dll")); public static PayloadFiles? TryInDirectory(string directory) { var native=Path.Combine(directory,"HookLab.NativeBootstrap.x64.dll"); var managed=Path.Combine(directory,"HookLab.Bootstrap.dll"); return File.Exists(native)&&File.Exists(managed)?new PayloadFiles(Path.GetFullPath(native),Path.GetFullPath(managed)):null; } }
	readonly record struct InjectionOutcome(string Result,string ImagePath,long CreationTicks,Dictionary<string,string> Report);
}

public sealed record ResidentInjection(string Result,string ImagePath,long CreationTicks,string ProbeInstanceId,string PipeName,byte[] EndpointNonce,string PatchId,long HooksVersion);
public sealed class TargetExitedException : Exception { public TargetExitedException(string message,Exception innerException):base(message,innerException) { } }
public sealed class ClrReadinessTimeoutException : TimeoutException { public ClrReadinessTimeoutException(string message):base(message) { } }

