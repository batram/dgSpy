using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace dgSpy.Extension {
	static class NativeHookLabInitializer {
		const uint ProcessAccess=0x0002|0x0008|0x0020|0x0400;
		const uint CommitReserve=0x1000|0x2000;

		public static void Load(int processId,string libraryPath) {
			using var process=Process.GetProcessById(processId);
			var processHandle=OpenProcess(ProcessAccess,false,processId);
			if(processHandle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"HookLab could not open the target process.");
			try {
				var bytes=System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(libraryPath)+'\0');
				var remotePath=VirtualAllocEx(processHandle,IntPtr.Zero,(UIntPtr)bytes.Length,CommitReserve,0x04);
				if(remotePath==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"HookLab could not allocate initialization storage in the target.");
				try {
					if(!WriteProcessMemory(processHandle,remotePath,bytes,(UIntPtr)bytes.Length,out var written) || written.ToUInt64()!=(ulong)bytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error(),"HookLab could not write its initialization path.");
					var localKernel=GetModuleHandle("kernel32.dll");
					var localLoader=GetProcAddress(localKernel,"LoadLibraryW");
					var remoteKernel=process.Modules.Cast<ProcessModule>().First(module=>String.Equals(module.ModuleName,"kernel32.dll",StringComparison.OrdinalIgnoreCase)).BaseAddress;
					var remoteLoader=new IntPtr(remoteKernel.ToInt64()+(localLoader.ToInt64()-localKernel.ToInt64()));
					var thread=CreateRemoteThread(processHandle,IntPtr.Zero,UIntPtr.Zero,remoteLoader,remotePath,0,IntPtr.Zero);
					if(thread==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"HookLab could not start autonomous initialization.");
					try {
						var wait=WaitForSingleObject(thread,30000);
						if(wait!=0) throw new TimeoutException("HookLab autonomous initialization did not load within 30 seconds.");
						if(!GetExitCodeThread(thread,out var exitCode) || exitCode==0) throw new InvalidOperationException("HookLab autonomous initialization did not load its runtime component.");
					}
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
}
