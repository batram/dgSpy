using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace dgSpy.Extension {
	/// <summary>Reads the x64 integer/control context Windows preserves for a stopped OS thread. CorDebug
	/// and Mono both suspend that same thread; dgSpy's supported target architecture is x64.</summary>
	static class NativeRegisterReader {
		const int ContextSize=1232,ContextFlagsOffset=48,ContextAmd64ControlInteger=0x00100003;
		public static object[] ReadX64(ulong threadId) {
			if(threadId>uint.MaxValue) throw new RpcException("capability_unavailable",$"Thread id {threadId} is not a Windows thread id.");
			var thread=OpenThread(0x0008|0x0040,false,(uint)threadId); if(thread==IntPtr.Zero) throw Failure("OpenThread",threadId);
			// CONTEXT on AMD64 is 16-byte aligned. AllocHGlobal's alignment is an implementation detail,
			// so reserve one alignment unit and align explicitly before asking kernel32 to fill it.
			var allocation=Marshal.AllocHGlobal(ContextSize+15);
			var context=new IntPtr((allocation.ToInt64()+15)&~15L);
			try {
				for(var i=0;i<ContextSize;i++) Marshal.WriteByte(context,i,0); Marshal.WriteInt32(context,ContextFlagsOffset,ContextAmd64ControlInteger);
				if(!GetThreadContext(thread,context)) throw Failure("GetThreadContext",threadId); var result=new List<object>(18);
				Add(result,"rax",context,120); Add(result,"rcx",context,128); Add(result,"rdx",context,136); Add(result,"rbx",context,144); Add(result,"rsp",context,152); Add(result,"rbp",context,160); Add(result,"rsi",context,168); Add(result,"rdi",context,176);
				Add(result,"r8",context,184); Add(result,"r9",context,192); Add(result,"r10",context,200); Add(result,"r11",context,208); Add(result,"r12",context,216); Add(result,"r13",context,224); Add(result,"r14",context,232); Add(result,"r15",context,240); Add(result,"rip",context,248);
				var flags=unchecked((uint)Marshal.ReadInt32(context,68)); result.Add(new { name="rflags",value=(ulong)flags,hex="0x"+flags.ToString("X8"),bits=32 }); return result.ToArray();
			}
			finally { Marshal.FreeHGlobal(allocation); CloseHandle(thread); }
		}
		static void Add(List<object> values,string name,IntPtr context,int offset) { var value=unchecked((ulong)Marshal.ReadInt64(context,offset)); values.Add(new { name,value,hex="0x"+value.ToString("X16"),bits=64 }); }
		static RpcException Failure(string operation,ulong threadId) { var error=Marshal.GetLastWin32Error(); return new RpcException("capability_unavailable",$"{operation} could not read the x64 context of stopped OS thread {threadId}: {new Win32Exception(error).Message} (Win32 {error})."); }
		[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenThread(uint desiredAccess,bool inheritHandle,uint threadId);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool GetThreadContext(IntPtr thread,IntPtr context);
		[DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
	}
}
