using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

if(args.Length!=2 || !Int32.TryParse(args[0],out var processId)) {
	Console.Error.WriteLine("Usage: HookLab.NativeBootstrapProof.Injector <pid> <absolute-dll-path>");
	return 2;
}

var dllPath=Path.GetFullPath(args[1]);
if(!File.Exists(dllPath)) throw new FileNotFoundException("Bootstrap DLL not found.",dllPath);
using var process=Process.GetProcessById(processId);
if(!Environment.Is64BitProcess) throw new InvalidOperationException("The proof injector must run as x64.");

const uint access=0x0002|0x0008|0x0020|0x0400;
var processHandle=Native.OpenProcess(access,false,processId);
if(processHandle==IntPtr.Zero) throw new Win32Exception();
try {
	var bytes=System.Text.Encoding.Unicode.GetBytes(dllPath+'\0');
	var remotePath=Native.VirtualAllocEx(processHandle,IntPtr.Zero,(nuint)bytes.Length,0x1000|0x2000,0x04);
	if(remotePath==IntPtr.Zero) throw new Win32Exception();
	try {
		if(!Native.WriteProcessMemory(processHandle,remotePath,bytes,(nuint)bytes.Length,out var written) || written!=(nuint)bytes.Length) throw new Win32Exception();
		var localKernel32=Native.GetModuleHandleW("kernel32.dll");
		var localLoadLibrary=Native.GetProcAddress(localKernel32,"LoadLibraryW");
		var remoteKernel32=process.Modules.Cast<ProcessModule>().First(module=>String.Equals(module.ModuleName,"kernel32.dll",StringComparison.OrdinalIgnoreCase)).BaseAddress;
		var remoteLoadLibrary=remoteKernel32+(localLoadLibrary-localKernel32);
		var thread=Native.CreateRemoteThread(processHandle,IntPtr.Zero,0,remoteLoadLibrary,remotePath,0,IntPtr.Zero);
		if(thread==IntPtr.Zero) throw new Win32Exception();
		try {
			var wait=Native.WaitForSingleObject(thread,30000);
			if(wait!=0) throw new TimeoutException($"Remote LoadLibraryW wait returned 0x{wait:X8}.");
			if(!Native.GetExitCodeThread(thread,out var exitCode)) throw new Win32Exception();
			if(exitCode==0) throw new InvalidOperationException("Remote LoadLibraryW returned null.");
			Console.WriteLine($"loaded pid={processId} dll={dllPath}");
		}
		finally { Native.CloseHandle(thread); }
	}
	finally { Native.VirtualFreeEx(processHandle,remotePath,0,0x8000); }
}
finally { Native.CloseHandle(processHandle); }
return 0;

static partial class Native {
	[LibraryImport("kernel32.dll",SetLastError=true)] internal static partial IntPtr OpenProcess(uint desiredAccess,[MarshalAs(UnmanagedType.Bool)] bool inheritHandle,int processId);
	[LibraryImport("kernel32.dll",SetLastError=true)] internal static partial IntPtr VirtualAllocEx(IntPtr process,IntPtr address,nuint size,uint allocationType,uint protection);
	[LibraryImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool VirtualFreeEx(IntPtr process,IntPtr address,nuint size,uint freeType);
	[LibraryImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,nuint size,out nuint written);
	[LibraryImport("kernel32.dll",SetLastError=true)] internal static partial IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,nuint stackSize,IntPtr startAddress,IntPtr parameter,uint flags,IntPtr threadId);
	[LibraryImport("kernel32.dll",SetLastError=true)] internal static partial uint WaitForSingleObject(IntPtr handle,uint milliseconds);
	[LibraryImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool GetExitCodeThread(IntPtr thread,out uint exitCode);
	[LibraryImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(IntPtr handle);
	[LibraryImport("kernel32.dll",StringMarshalling=StringMarshalling.Utf16)] internal static partial IntPtr GetModuleHandleW(string moduleName);
	[LibraryImport("kernel32.dll",StringMarshalling=StringMarshalling.Utf8)] internal static partial IntPtr GetProcAddress(IntPtr module,string name);
}
