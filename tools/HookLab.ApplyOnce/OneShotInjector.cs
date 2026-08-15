using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HookLab.ApplyOnce;

internal static class OneShotInjector {
	public static string Apply(int processId,HookDefinition definition,string definitionPath,string? payloadDirectory) {
		using var process=Process.GetProcessById(processId);
		string imagePath; long creationTicks;
		try { imagePath=process.MainModule?.FileName ?? throw new InvalidOperationException("The target image path is unavailable."); creationTicks=process.StartTime.ToUniversalTime().Ticks; }
		catch(Win32Exception ex) { throw new InvalidOperationException("Could not read target process identity. Run ApplyOnce elevated when the target is elevated.",ex); }
		if(!String.Equals(Path.GetFileName(imagePath),definition.Process!.FileName,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PID "+processId+" is not "+definition.Process.FileName+"; observed "+imagePath+".");
		EnsureX64(processId);
		var payload=payloadDirectory is null?FindPayload():PayloadFiles.InDirectory(payloadDirectory);
		var staging=Path.Combine(Path.GetTempPath(),"hooklab-apply-once-"+processId.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		var completion=Path.Combine(staging,"completion.txt");
		var completed=false;
		try {
			var nativePath=Path.Combine(staging,"HookLab.NativeBootstrap.x64.dll");
			File.Copy(payload.Native,nativePath,false);
			File.Copy(payload.Managed,Path.Combine(staging,"HookLab.Bootstrap.dll"),false);
			File.WriteAllText(Path.Combine(staging,"initialize.params"),Parameters(imagePath,processId,creationTicks,completion,definition),new UTF8Encoding(false));
			NativeLoader.Load(processId,nativePath);
			var wait=Stopwatch.StartNew();
			var report=WaitForCompletion(process,completion,TimeSpan.FromSeconds(5));
			completed=true;
			var values=ParseReport(report);
			if(!values.TryGetValue("status",out var status)||status!="ok") throw new InvalidOperationException("The target reported: "+report.Replace('\r',' ').Replace('\n',' '));
			if(!values.TryGetValue("patch_id",out var patchId)||String.IsNullOrWhiteSpace(patchId)) throw new InvalidOperationException("The target reported success without a patch_id.");
			var result="status=ok\nprocess_id="+processId.ToString(CultureInfo.InvariantCulture)+"\nprocess_creation_utc_ticks="+creationTicks.ToString(CultureInfo.InvariantCulture)+"\ndefinition_id="+definition.Id+"\ndefinition_path="+definitionPath+"\ndefinition_sha256="+Sha256(definitionPath)+"\npatch_id="+patchId+"\nhooks_version="+(values.TryGetValue("hooks_version",out var version)?version:"unknown")+"\nlauncher_wait_ms="+wait.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)+"\nworker_queue_ms="+(values.TryGetValue("worker_queue_ms",out var queue)?queue:"missing")+"\nbehavior_elapsed_ms="+(values.TryGetValue("behavior_elapsed_ms",out var elapsed)?elapsed:"missing")+"\n";
			WriteResult(processId,result);
			return result;
		}
		finally {
			if(completed) TryDeleteDirectory(staging);
			else Console.Error.WriteLine("Preserved ambiguous HookLab staging directory: "+staging);
		}
	}

	internal static string Parameters(string imagePath,int processId,long creationTicks,string completion,HookDefinition definition) {
		var target=definition.Target!; var hook=definition.Hook!;
		var source=Convert.ToBase64String(Encoding.UTF8.GetBytes(hook.Source!));
		var values=new[] {
			Pair("host_id","apply-once"),Pair("image_path",imagePath),Pair("process_id",processId.ToString(CultureInfo.InvariantCulture)),Pair("process_creation_utc_ticks",creationTicks.ToString(CultureInfo.InvariantCulture)),
			Pair("architecture","x64"),Pair("runtime_id","v4.0.30319"),Pair("appdomain_id","1"),Pair("endpoint","none"),Pair("completion_path",completion),Pair("hook_id",definition.Id!),
			Pair("hook_kind",hook.Kind!),Pair("hook_assembly",target.Assembly!),Pair("hook_type",target.DeclaringType!),Pair("hook_method",target.Method!),Pair("hook_module_mvid",target.ModuleMvid!),
			Pair("hook_metadata_token",target.MetadataToken.ToString(CultureInfo.InvariantCulture)),Pair("hook_declaring_type",target.DeclaringType!),Pair("hook_method_signature",target.Signature!),Pair("hook_il_sha256",target.IlSha256!),
			Pair("hook_source_base64",source),Pair("hook_revision",hook.Revision.ToString(CultureInfo.InvariantCulture)),Pair("maximum_events_per_second",hook.MaximumEventsPerSecond.ToString(CultureInfo.InvariantCulture)),Pair("maximum_string_length",hook.MaximumStringLength.ToString(CultureInfo.InvariantCulture))
		};
		return String.Join("\n",values)+"\n";
	}

	internal static void WriteResult(int processId,string result) { try { File.WriteAllText(ResultPath(processId),result,new UTF8Encoding(false)); } catch { } }
	internal static string ResultPath(int processId)=>Path.Combine(Path.GetTempPath(),"HookLab.ApplyOnce-"+processId.ToString(CultureInfo.InvariantCulture)+".result.txt");
	static string Pair(string key,string value) { if(value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidOperationException("Bootstrap value contains a newline: "+key+"."); return key+"="+value; }
	static string Sha256(string path) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant(); }
	static Dictionary<string,string> ParseReport(string text) { var result=new Dictionary<string,string>(StringComparer.Ordinal); foreach(var line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) { var separator=line.IndexOf('='); if(separator>0) result[line[..separator]]=line[(separator+1)..]; } return result; }
	static string WaitForCompletion(Process process,string path,TimeSpan timeout) { var watch=Stopwatch.StartNew(); while(watch.Elapsed<timeout) { if(process.HasExited) throw new InvalidOperationException("Target exited before HookLab initialization completed."); if(File.Exists(path)) return File.ReadAllText(path); Thread.Sleep(25); } throw new TimeoutException("HookLab did not publish its completion report within "+timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)+" seconds. Staging was preserved; do not retry this PID blindly."); }
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
}

internal static class NativeLoader {
	const uint ProcessAccess=0x0002|0x0008|0x0020|0x0400, CommitReserve=0x1000|0x2000;
	public static void Load(int processId,string libraryPath) { using var process=Process.GetProcessById(processId); var handle=OpenProcess(ProcessAccess,false,processId); if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open target process. Run ApplyOnce elevated if necessary."); try { var bytes=Encoding.Unicode.GetBytes(Path.GetFullPath(libraryPath)+'\0'); var remote=VirtualAllocEx(handle,IntPtr.Zero,(UIntPtr)bytes.Length,CommitReserve,4); if(remote==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not allocate bootstrap storage."); try { if(!WriteProcessMemory(handle,remote,bytes,(UIntPtr)bytes.Length,out var written)||written.ToUInt64()!=(ulong)bytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not write bootstrap path."); var localKernel=GetModuleHandle("kernel32.dll"); var localLoader=GetProcAddress(localKernel,"LoadLibraryW"); var remoteKernel=process.Modules.Cast<ProcessModule>().Single(module=>String.Equals(module.ModuleName,"kernel32.dll",StringComparison.OrdinalIgnoreCase)).BaseAddress; var remoteLoader=new IntPtr(remoteKernel.ToInt64()+localLoader.ToInt64()-localKernel.ToInt64()); var thread=CreateRemoteThread(handle,IntPtr.Zero,UIntPtr.Zero,remoteLoader,remote,0,IntPtr.Zero); if(thread==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not start bootstrap thread."); try { if(WaitForSingleObject(thread,30000)!=0) throw new TimeoutException("Native bootstrap did not load within 30 seconds."); if(!GetExitCodeThread(thread,out var exitCode)||exitCode==0) throw new InvalidOperationException("Native bootstrap DLL did not load."); } finally { CloseHandle(thread); } } finally { VirtualFreeEx(handle,remote,UIntPtr.Zero,0x8000); } } finally { CloseHandle(handle); } }
	[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inheritHandle,int processId);
	[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,UIntPtr size,uint allocationType,uint protection);
	[DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualFreeEx(IntPtr process,IntPtr address,UIntPtr size,uint freeType);
	[DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,UIntPtr size,out UIntPtr written);
	[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,UIntPtr stackSize,IntPtr startAddress,IntPtr parameter,uint flags,IntPtr threadId);
	[DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
	[DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeThread(IntPtr thread,out uint exitCode);
	[DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
	[DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string moduleName);
	[DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
}
