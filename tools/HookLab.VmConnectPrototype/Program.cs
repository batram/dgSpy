using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program {
	const string HookSource="public static class H{public static bool Prefix(object __instance,ref bool __result){var t=__instance.GetType();if(!(bool)t.GetProperty(\"FullScreen\",(System.Reflection.BindingFlags)20).GetValue(__instance,null))return __result=false;var c=t.GetField(\"m_RdpClient\",(System.Reflection.BindingFlags)36).GetValue(__instance);if(c==null)return __result=false;var ct=c.GetType();var s=ct.GetMethod(\"SyncSessionDisplaySettings\",(System.Reflection.BindingFlags)20);var l=ct.GetProperty(\"Location\",(System.Reflection.BindingFlags)20);for(int n=0;n<20;n++){try{s.Invoke(c,null);l.SetValue(c,System.Activator.CreateInstance(l.PropertyType),null);__result=true;return false;}catch{System.Threading.Thread.Sleep(250);}}return __result=false;}}";
	const string ExpectedMvid="dc470534-4dc7-403f-a529-c2b0259fa97d";
	const string ExpectedIlSha256="49de3a2203a1a8d79ac4c35e5d14b844b2bd4a4063fc3ba6e62ad951b92d5c1b";
	const int MethodToken=0x06000155;

	public static int Main(string[] arguments) {
		var resultPath=ResultPath(arguments);
		try { var exitCode=Run(arguments,out var result); WriteResult(resultPath,result); return exitCode; }
		catch(Exception ex) { var result="status=error\nmessage="+Sanitize(ex.Message)+"\n"; Console.Error.WriteLine("VMConnect HookLab prototype failed: "+ex.Message); WriteResult(resultPath,result); return 1; }
	}

	static int Run(string[] arguments,out string result) {
		result="status=error\nmessage=invalid arguments\n";
		if(arguments.Length is < 1 or > 3 || !Int32.TryParse(arguments[0],NumberStyles.None,CultureInfo.InvariantCulture,out var processId) || processId<=0 || (arguments.Length>1 && (arguments.Length!=3 || !String.Equals(arguments[1],"--payload-dir",StringComparison.Ordinal)))) {
			Console.Error.WriteLine("Usage: HookLab.VmConnectPrototype.exe <vmconnect-pid> [--payload-dir <directory>]");
			return 2;
		}

		using var process=Process.GetProcessById(processId);
		string imagePath; long creationTicks;
		try { imagePath=process.MainModule?.FileName ?? throw new InvalidOperationException("The target image path is unavailable."); creationTicks=process.StartTime.ToUniversalTime().Ticks; }
		catch(Win32Exception ex) { throw new InvalidOperationException("Could not read VmConnect.exe identity. Run the prototype from an elevated terminal.",ex); }
		if(!String.Equals(Path.GetFileName(imagePath),"VmConnect.exe",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PID "+processId.ToString(CultureInfo.InvariantCulture)+" is not VmConnect.exe; observed "+imagePath+".");
		EnsureX64(processId);

		var payload=arguments.Length==3 ? PayloadFiles.InDirectory(Path.GetFullPath(arguments[2])) : FindPayload();
		var staging=Path.Combine(Path.GetTempPath(),"hooklab-vmconnect-"+processId.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		var completion=Path.Combine(staging,"completion.txt");
		try {
			var nativePath=Path.Combine(staging,"HookLab.NativeBootstrap.x64.dll");
			File.Copy(payload.Native,nativePath,false);
			File.Copy(payload.Managed,Path.Combine(staging,"HookLab.Bootstrap.dll"),false);
			File.WriteAllText(Path.Combine(staging,"initialize.params"),Parameters(imagePath,processId,creationTicks,completion),new UTF8Encoding(false));
			NativeLoader.Load(processId,nativePath);
			var report=WaitForCompletion(process,completion,TimeSpan.FromSeconds(30));
			var values=ParseReport(report);
			if(!values.TryGetValue("status",out var status) || !String.Equals(status,"ok",StringComparison.Ordinal)) throw new InvalidOperationException("The target reported: "+report.Replace('\r',' ').Replace('\n',' '));
			if(!values.TryGetValue("patch_id",out var patchId) || String.IsNullOrWhiteSpace(patchId)) throw new InvalidOperationException("The target reported success without a patch_id.");
			Console.WriteLine("VMConnect fullscreen hook installed.");
			Console.WriteLine("process_id="+processId.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("patch_id="+patchId);
			if(values.TryGetValue("hooks_version",out var hooksVersion)) Console.WriteLine("hooks_version="+hooksVersion);
			result="status=ok\nprocess_id="+processId.ToString(CultureInfo.InvariantCulture)+"\npatch_id="+patchId+"\nhooks_version="+(values.TryGetValue("hooks_version",out var version)?version:"unknown")+"\n";
			return 0;
		}
		finally { TryDeleteDirectory(staging); }
	}

	static string Parameters(string imagePath,int processId,long creationTicks,string completion) {
		var source=Convert.ToBase64String(Encoding.UTF8.GetBytes(HookSource));
		if(source.Length>1024) throw new InvalidOperationException("The embedded hook source exceeds the bootstrap value limit.");
		var values=new[] {
			Pair("host_id","vmconnect-prototype"), Pair("image_path",imagePath), Pair("process_id",processId.ToString(CultureInfo.InvariantCulture)),
			Pair("process_creation_utc_ticks",creationTicks.ToString(CultureInfo.InvariantCulture)), Pair("architecture","x64"), Pair("runtime_id","v4.0.30319"),
			Pair("appdomain_id","1"), Pair("endpoint","none"), Pair("completion_path",completion), Pair("hook_id","vmconnect-fullscreen-sync-v1"),
			Pair("hook_kind","Prefix"), Pair("hook_assembly","vmconnect"), Pair("hook_type","Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl"),
			Pair("hook_method","SyncDisplaySettings"), Pair("hook_module_mvid",ExpectedMvid), Pair("hook_metadata_token",MethodToken.ToString(CultureInfo.InvariantCulture)),
			Pair("hook_declaring_type","Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl"), Pair("hook_method_signature","System.Boolean SyncDisplaySettings()"),
			Pair("hook_il_sha256",ExpectedIlSha256), Pair("hook_source_base64",source), Pair("hook_revision","1"), Pair("maximum_events_per_second","100"), Pair("maximum_string_length","1024")
		};
		return String.Join("\n",values)+"\n";
	}

	static string Pair(string key,string value) {
		if(value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidOperationException("Bootstrap value contains a newline: "+key+".");
		return key+"="+value;
	}

	static PayloadFiles FindPayload() {
		foreach(var candidate in new[]{AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"hooklab")}) {
			var payload=PayloadFiles.TryInDirectory(candidate); if(payload is not null) return payload.Value;
		}
		var current=new DirectoryInfo(Environment.CurrentDirectory);
		while(current is not null) {
			var managed=Path.Combine(current.FullName,"HookLab","HookLab.Bootstrap","bin","Release","net48");
			var native=Path.Combine(current.FullName,"HookLab","HookLab.NativeBootstrap","bin","Release");
			var managedPath=Path.Combine(managed,"HookLab.Bootstrap.dll"); var nativePath=Path.Combine(native,"HookLab.NativeBootstrap.x64.dll");
			if(File.Exists(managedPath) && File.Exists(nativePath)) return new PayloadFiles(nativePath,managedPath);
			current=current.Parent;
		}
		throw new FileNotFoundException("Could not find both HookLab bootstrap payloads. Build them first or pass --payload-dir <directory>.");
	}

	static string RequireFile(string directory,string name) {
		var path=Path.GetFullPath(Path.Combine(directory,name));
		if(!File.Exists(path)) throw new FileNotFoundException("Required payload is missing: "+path,path);
		return path;
	}

	static string WaitForCompletion(Process process,string path,TimeSpan timeout) {
		var stopwatch=Stopwatch.StartNew();
		while(stopwatch.Elapsed<timeout) {
			if(process.HasExited) throw new InvalidOperationException("VmConnect.exe exited before HookLab initialization completed.");
			if(File.Exists(path)) return File.ReadAllText(path);
			Thread.Sleep(50);
		}
		throw new TimeoutException("HookLab did not publish its completion report within "+timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)+" seconds. Do not retry this PID blindly; restart VMConnect for a clean retry.");
	}

	static Dictionary<string,string> ParseReport(string text) {
		var result=new Dictionary<string,string>(StringComparer.Ordinal);
		foreach(var line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) { var separator=line.IndexOf('='); if(separator>0) result[line[..separator]]=line[(separator+1)..]; }
		return result;
	}

	static string? ResultPath(string[] arguments)=>arguments.Length>0 && Int32.TryParse(arguments[0],NumberStyles.None,CultureInfo.InvariantCulture,out var processId) && processId>0 ? Path.Combine(Path.GetTempPath(),"HookLab.VmConnectPrototype-"+processId.ToString(CultureInfo.InvariantCulture)+".result.txt") : null;
	static void WriteResult(string? path,string result) { if(path is null) return; try { File.WriteAllText(path,result,new UTF8Encoding(false)); } catch { } }
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ');

	static void EnsureX64(int processId) {
		if(!Environment.Is64BitProcess) throw new InvalidOperationException("The prototype must run as x64.");
		var processHandle=OpenProcessForArchitecture(0x1000,false,processId);
		if(processHandle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open the target to determine its architecture.");
		try {
			if(!IsWow64Process2(processHandle,out var processMachine,out var nativeMachine)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not determine the target architecture.");
			const ushort Unknown=0, Amd64=0x8664;
			if(processMachine!=Unknown || nativeMachine!=Amd64) throw new InvalidOperationException("The target is not a native x64 process.");
		}
		finally { CloseArchitectureHandle(processHandle); }
	}

	static void TryDeleteDirectory(string path) { try { Directory.Delete(path,true); } catch { Console.Error.WriteLine("Warning: temporary bootstrap files remain at "+path); } }

	[DllImport("kernel32.dll",SetLastError=true)] static extern bool IsWow64Process2(IntPtr process,out ushort processMachine,out ushort nativeMachine);
	[DllImport("kernel32.dll",SetLastError=true,EntryPoint="OpenProcess")] static extern IntPtr OpenProcessForArchitecture(uint access,bool inheritHandle,int processId);
	[DllImport("kernel32.dll",SetLastError=true,EntryPoint="CloseHandle")] static extern bool CloseArchitectureHandle(IntPtr handle);

	readonly record struct PayloadFiles(string Native,string Managed) {
		public static PayloadFiles InDirectory(string directory)=>new(RequireFile(directory,"HookLab.NativeBootstrap.x64.dll"),RequireFile(directory,"HookLab.Bootstrap.dll"));
		public static PayloadFiles? TryInDirectory(string directory) {
			var native=Path.Combine(directory,"HookLab.NativeBootstrap.x64.dll"); var managed=Path.Combine(directory,"HookLab.Bootstrap.dll");
			return File.Exists(native) && File.Exists(managed) ? new PayloadFiles(Path.GetFullPath(native),Path.GetFullPath(managed)) : null;
		}
	}
}

internal static class NativeLoader {
	const uint ProcessAccess=0x0002|0x0008|0x0020|0x0400;
	const uint CommitReserve=0x1000|0x2000;

	public static void Load(int processId,string libraryPath) {
		using var process=Process.GetProcessById(processId);
		var processHandle=OpenProcess(ProcessAccess,false,processId);
		if(processHandle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open VmConnect.exe. Run the prototype elevated if VMConnect is elevated.");
		try {
			var bytes=Encoding.Unicode.GetBytes(Path.GetFullPath(libraryPath)+'\0');
			var remotePath=VirtualAllocEx(processHandle,IntPtr.Zero,(UIntPtr)bytes.Length,CommitReserve,0x04);
			if(remotePath==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not allocate bootstrap storage in VmConnect.exe.");
			try {
				if(!WriteProcessMemory(processHandle,remotePath,bytes,(UIntPtr)bytes.Length,out var written) || written.ToUInt64()!=(ulong)bytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not write the bootstrap path to VmConnect.exe.");
				var localKernel=GetModuleHandle("kernel32.dll");
				var localLoader=GetProcAddress(localKernel,"LoadLibraryW");
				var remoteKernel=process.Modules.Cast<ProcessModule>().Single(module=>String.Equals(module.ModuleName,"kernel32.dll",StringComparison.OrdinalIgnoreCase)).BaseAddress;
				var remoteLoader=new IntPtr(remoteKernel.ToInt64()+(localLoader.ToInt64()-localKernel.ToInt64()));
				var thread=CreateRemoteThread(processHandle,IntPtr.Zero,UIntPtr.Zero,remoteLoader,remotePath,0,IntPtr.Zero);
				if(thread==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not start the bootstrap in VmConnect.exe.");
				try { var wait=WaitForSingleObject(thread,30000); if(wait!=0) throw new TimeoutException("The native bootstrap did not load within 30 seconds."); if(!GetExitCodeThread(thread,out var exitCode) || exitCode==0) throw new InvalidOperationException("The native bootstrap DLL did not load."); }
				finally { CloseHandle(thread); }
			}
			finally { VirtualFreeEx(processHandle,remotePath,UIntPtr.Zero,0x8000); }
		}
		finally { CloseHandle(processHandle); }
	}

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
