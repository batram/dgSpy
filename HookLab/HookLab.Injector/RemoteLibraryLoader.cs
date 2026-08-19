using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HookLab.Injector {
	/// <summary>
	/// Loads a native library into another process and reports why the target's loader refused it.
	///
	/// The predecessor of this type started a remote thread directly at <c>LoadLibraryW</c> and read
	/// the thread's exit code. A thread exit code is a DWORD and an HMODULE is a pointer, so the exit
	/// code was a truncated module handle: every failure collapsed to zero and the target's
	/// <c>GetLastError</c> was unreachable. Access denied, a missing dependency, a bad image and a
	/// corrupt file were one message. That is not a diagnostic inconvenience - on a live IIS worker a
	/// true finding about directory ACLs was taken for the cause of what was actually a missing
	/// Visual C++ runtime, and a build and deploy cycle was spent on it.
	///
	/// So a small stub runs in the target instead: it calls <c>LoadLibraryW</c>, then <c>GetLastError</c>
	/// with no API call in between, and stores both in memory this process allocated and can read back.
	///
	/// The target-side loader stays authoritative for load failure. Nothing here predicts whether a
	/// library will load; it reports what the target decided.
	/// </summary>
	public static class RemoteLibraryLoader {
		// --- Shared page layout -------------------------------------------------------------------
		//
		// One allocation, one page, PAGE_EXECUTE_READWRITE, in the target:
		//
		//   0x0000  RemoteLoadRecord, 32 bytes, layout below
		//   0x0020  zero padding
		//   0x0040  the library path, NUL-terminated UTF-16, at most PathCapacity bytes
		//   0x0800  the stub, written last and never larger than PageSize-StubOffset
		//
		// RemoteLoadRecord, version 1:
		//
		//   0x00  uint32  version         written by this process before injection; 1
		//   0x04  uint32  size            written by this process before injection; 32
		//   0x08  uint64  module_handle   HMODULE from LoadLibraryW, or 0
		//   0x10  uint32  last_error      GetLastError immediately after LoadLibraryW
		//   0x14  uint32  status          0 the stub did not complete, 1 it did
		//   0x18  uint64  reserved        0
		//
		// The version and size are written by this process and read back rather than trusted, so a
		// record that did not come from the page we wrote is detected rather than reported as a load
		// failure. A future revision adds fields at 0x18 and raises version and size together;
		// a reader must accept a size larger than it understands and refuse one smaller.
		const int PageSize=0x1000, RecordOffset=0x0000, RecordSize=32, PathOffset=0x0040, StubOffset=0x0800;
		const int PathCapacity=StubOffset-PathOffset;
		const uint RecordVersion=1;

		const uint MemCommitReserve=0x1000|0x2000, MemRelease=0x8000, PageExecuteReadWrite=0x40;
		const uint ProcessAccess=0x0002|0x0008|0x0010|0x0020|0x0400;  // CREATE_THREAD|VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
		const uint WaitObject0=0, StillActive=259;

		/// <summary>Loads <paramref name="libraryPath"/> into the target, or throws naming the exact
		/// Win32 code the target's loader produced.</summary>
		public static RemoteLoadResult Load(int processId,string libraryPath,int timeoutMilliseconds=30000) {
			var result=TryLoad(processId,libraryPath,timeoutMilliseconds);
			if(result.Loaded) return result;
			throw new RemoteLibraryLoadException(result,libraryPath);
		}

		/// <summary>Attempts the load and reports the outcome instead of throwing. A returned result
		/// with <see cref="RemoteLoadResult.Loaded"/> false is an answer, not an error.</summary>
		public static RemoteLoadResult TryLoad(int processId,string libraryPath,int timeoutMilliseconds=30000) {
			if(String.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException("A library path is required.",nameof(libraryPath));
			if(timeoutMilliseconds<1000||timeoutMilliseconds>120000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
			if(!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The remote loader emits x64 code and must run in an x64 process.");
			var path=Path.GetFullPath(libraryPath);
			var pathBytes=System.Text.Encoding.Unicode.GetBytes(path+"\0");
			if(pathBytes.Length>PathCapacity) throw new ArgumentException("The library path does not fit the shared page: "+pathBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+" bytes of "+PathCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)+".",nameof(libraryPath));

			using(var process=Process.GetProcessById(processId)) {
				EnsureTargetIsX64(processId);
				var handle=OpenProcess(ProcessAccess,false,processId);
				if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not open the target process to load "+Path.GetFileName(path)+".");
				try {
					var loadLibrary=ResolveInTarget(process,"kernel32.dll","LoadLibraryW");
					var lastError=ResolveInTarget(process,"kernel32.dll","GetLastError");
					var page=VirtualAllocEx(handle,IntPtr.Zero,(UIntPtr)PageSize,MemCommitReserve,PageExecuteReadWrite);
					if(page==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not allocate the shared loader page in the target.");
					var pageRetained=false;
					try {
						var image=new byte[PageSize];
						WriteUInt32(image,RecordOffset+0,RecordVersion);
						WriteUInt32(image,RecordOffset+4,RecordSize);
						Array.Copy(pathBytes,0,image,PathOffset,pathBytes.Length);
						var stub=EmitStub(loadLibrary,lastError);
						Array.Copy(stub,0,image,StubOffset,stub.Length);
						UIntPtr written;
						if(!WriteProcessMemory(handle,page,image,(UIntPtr)image.Length,out written)||written.ToUInt64()!=(ulong)image.Length)
							throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not write the shared loader page into the target.");
						var thread=CreateRemoteThread(handle,IntPtr.Zero,UIntPtr.Zero,new IntPtr(page.ToInt64()+StubOffset),page,0,IntPtr.Zero);
						if(thread==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not start the loader stub in the target.");
						try {
							if(WaitForSingleObject(thread,(uint)timeoutMilliseconds)!=WaitObject0) {
								// The stub may still be running, and freeing a page a live thread is
								// executing faults the target. A target is never worth 4 KB: the
								// allocation is retained deliberately, and its address is reported so
								// it is a known leak rather than a mystery. The previous implementation
								// freed it from a finally block on exactly this path.
								uint state;
								if(GetExitCodeThread(thread,out state)&&state==StillActive) pageRetained=true;
								throw new TimeoutException("The loader stub did not finish within "+timeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+" ms."+(pageRetained?" Its page at 0x"+page.ToInt64().ToString("x")+" in the target was deliberately not freed, because the stub may still be running.":String.Empty));
							}
						}
						finally { CloseHandle(thread); }
						return ReadRecord(handle,page,path);
					}
					finally { if(!pageRetained) VirtualFreeEx(handle,page,UIntPtr.Zero,MemRelease); }
				}
				finally { CloseHandle(handle); }
			}
		}

		static RemoteLoadResult ReadRecord(IntPtr process,IntPtr page,string path) {
			var record=new byte[RecordSize];
			UIntPtr read;
			if(!ReadProcessMemory(process,page,record,(UIntPtr)record.Length,out read)||read.ToUInt64()!=(ulong)record.Length)
				throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not read the loader result back from the target.");
			var version=BitConverter.ToUInt32(record,0);
			var size=BitConverter.ToUInt32(record,4);
			if(version!=RecordVersion||size<RecordSize) throw new InvalidOperationException("The target returned a loader record this build does not understand (version "+version.ToString(System.Globalization.CultureInfo.InvariantCulture)+", size "+size.ToString(System.Globalization.CultureInfo.InvariantCulture)+").");
			var moduleHandle=BitConverter.ToUInt64(record,8);
			var error=BitConverter.ToUInt32(record,16);
			var completed=BitConverter.ToUInt32(record,20)==1;
			return new RemoteLoadResult(path,moduleHandle,completed?(int)error:0,completed);
		}

		// --- The stub -----------------------------------------------------------------------------
		//
		// x64 only, and deliberately so: this encoding is not architecture-neutral and an x86 form is
		// a separate, explicit piece of work rather than an assumption this one can carry.
		//
		// On entry RCX holds the page base, as CreateRemoteThread's lpParameter.
		//
		//   push rbx / mov rbx,rcx        keep the page base across two calls in a callee-saved register
		//   push rbp / mov rbp,rsp        establish a frame so the alignment below can be undone
		//   and  rsp,-16 / sub rsp,32     16-byte alignment and the 32 bytes of shadow space the
		//                                 Windows x64 calling convention requires of every caller
		//   lea  rcx,[rbx+PathOffset]     the path, in the page
		//   call LoadLibraryW
		//   mov  [rbx+8],rax              module handle, full 64 bits - the whole point
		//   call GetLastError             immediately after, with no API call in between
		//   mov  [rbx+0x10],eax
		//   mov  dword [rbx+0x14],1       status: the stub reached its end
		//   mov  eax,1                    a thread exit code that means "completed", not a truncated
		//                                 handle that means four different things
		//   leave-equivalent, ret
		static byte[] EmitStub(IntPtr loadLibraryW,IntPtr getLastError) {
			var code=new List<byte>(64);
			code.AddRange(new byte[]{0x53});                                      // push rbx
			code.AddRange(new byte[]{0x48,0x89,0xCB});                            // mov  rbx,rcx
			code.AddRange(new byte[]{0x55});                                      // push rbp
			code.AddRange(new byte[]{0x48,0x89,0xE5});                            // mov  rbp,rsp
			code.AddRange(new byte[]{0x48,0x83,0xE4,0xF0});                       // and  rsp,-16
			code.AddRange(new byte[]{0x48,0x83,0xEC,0x20});                       // sub  rsp,32
			code.AddRange(new byte[]{0x48,0x8D,0x8B});                            // lea  rcx,[rbx+disp32]
			code.AddRange(BitConverter.GetBytes((uint)PathOffset));
			code.AddRange(new byte[]{0x48,0xB8});                                 // mov  rax,imm64
			code.AddRange(BitConverter.GetBytes((ulong)loadLibraryW.ToInt64()));
			code.AddRange(new byte[]{0xFF,0xD0});                                 // call rax
			code.AddRange(new byte[]{0x48,0x89,0x43,0x08});                       // mov  [rbx+8],rax
			code.AddRange(new byte[]{0x48,0xB8});                                 // mov  rax,imm64
			code.AddRange(BitConverter.GetBytes((ulong)getLastError.ToInt64()));
			code.AddRange(new byte[]{0xFF,0xD0});                                 // call rax
			code.AddRange(new byte[]{0x89,0x43,0x10});                            // mov  [rbx+0x10],eax
			code.AddRange(new byte[]{0xC7,0x43,0x14,0x01,0x00,0x00,0x00});        // mov  dword [rbx+0x14],1
			code.AddRange(new byte[]{0xB8,0x01,0x00,0x00,0x00});                  // mov  eax,1
			code.AddRange(new byte[]{0x48,0x89,0xEC});                            // mov  rsp,rbp
			code.AddRange(new byte[]{0x5D});                                      // pop  rbp
			code.AddRange(new byte[]{0x5B});                                      // pop  rbx
			code.AddRange(new byte[]{0xC3});                                      // ret
			if(code.Count>PageSize-StubOffset) throw new InvalidOperationException("The loader stub outgrew its region of the shared page.");
			return code.ToArray();
		}

		/// <summary>The address of a system export as seen by the target.
		///
		/// System modules map at the same base in every process of a boot session, so a local address
		/// plus the difference of the two bases is the remote address. What must not be assumed is
		/// <em>which</em> module the address belongs to: kernel32 forwards a growing number of its
		/// exports to KernelBase, and GetProcAddress resolves forwarders. Computing an offset against
		/// kernel32's base for a function that lives in KernelBase would produce a plausible pointer
		/// into the wrong module, so the containing module is asked for by address rather than
		/// assumed.</summary>
		static IntPtr ResolveInTarget(Process process,string moduleName,string export) {
			var localModule=GetModuleHandleW(moduleName);
			if(localModule==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not find "+moduleName+" in this process.");
			var localAddress=GetProcAddress(localModule,export);
			if(localAddress==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not find "+export+" in "+moduleName+".");
			IntPtr owner;
			if(!GetModuleHandleExW(0x00000004|0x00000002,localAddress,out owner)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not identify the module that provides "+export+".");
			var buffer=new System.Text.StringBuilder(32768);
			if(GetModuleFileNameW(owner,buffer,buffer.Capacity)==0) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not name the module that provides "+export+".");
			var ownerName=Path.GetFileName(buffer.ToString());
			foreach(ProcessModule module in process.Modules)
				if(String.Equals(module.ModuleName,ownerName,StringComparison.OrdinalIgnoreCase))
					return new IntPtr(module.BaseAddress.ToInt64()+(localAddress.ToInt64()-owner.ToInt64()));
			throw new InvalidOperationException("The target has not loaded "+ownerName+", which provides "+export+".");
		}

		static void EnsureTargetIsX64(int processId) {
			var handle=OpenProcess(0x1000,false,processId);  // QUERY_LIMITED_INFORMATION
			if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not read the target's architecture.");
			try {
				ushort processMachine,nativeMachine;
				if(!IsWow64Process2(handle,out processMachine,out nativeMachine)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not determine the target's architecture.");
				if(processMachine!=0||nativeMachine!=0x8664) throw new PlatformNotSupportedException("The target is not a native x64 process, and the loader stub is x64 code.");
			}
			finally { CloseHandle(handle); }
		}

		static void WriteUInt32(byte[] buffer,int at,uint value) { var bytes=BitConverter.GetBytes(value); Array.Copy(bytes,0,buffer,at,4); }

		[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inheritHandle,int processId);
		[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr process,IntPtr address,UIntPtr size,uint allocationType,uint protection);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualFreeEx(IntPtr process,IntPtr address,UIntPtr size,uint freeType);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr process,IntPtr address,byte[] buffer,UIntPtr size,out UIntPtr written);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] buffer,UIntPtr size,out UIntPtr read);
		[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr process,IntPtr attributes,UIntPtr stackSize,IntPtr startAddress,IntPtr parameter,uint flags,IntPtr threadId);
		[DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeThread(IntPtr thread,out uint exitCode);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool IsWow64Process2(IntPtr process,out ushort processMachine,out ushort nativeMachine);
		[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="GetModuleHandleW")] static extern IntPtr GetModuleHandleW(string moduleName);
		[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="GetModuleHandleExW")] static extern bool GetModuleHandleExW(uint flags,IntPtr address,out IntPtr module);
		[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="GetModuleFileNameW")] static extern uint GetModuleFileNameW(IntPtr module,System.Text.StringBuilder fileName,int size);
		[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Ansi,BestFitMapping=false,ThrowOnUnmappableChar=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
	}

	/// <summary>What the target's loader decided about one library.</summary>
	public readonly struct RemoteLoadResult {
		public RemoteLoadResult(string libraryPath,ulong moduleHandle,int lastError,bool stubCompleted) {
			LibraryPath=libraryPath; ModuleHandle=moduleHandle; LastError=lastError; StubCompleted=stubCompleted;
		}
		public string LibraryPath { get; }
		/// <summary>The full HMODULE, not a truncated one. Zero when the loader refused.</summary>
		public ulong ModuleHandle { get; }
		/// <summary>The target's GetLastError, captured immediately after LoadLibraryW.</summary>
		public int LastError { get; }
		/// <summary>False when the stub did not run to completion, in which case nothing else here is
		/// meaningful. It is reported rather than papered over: a stub that did not finish is a
		/// different fact from a library that did not load.</summary>
		public bool StubCompleted { get; }
		public bool Loaded { get { return StubCompleted&&ModuleHandle!=0; } }
		/// <summary>The symbolic name of <see cref="LastError"/>, or a deterministic
		/// <c>WIN32_ERROR_&lt;n&gt;</c> when no reviewed mapping exists.</summary>
		public string LastErrorName { get { return Win32ErrorNames.Name(LastError); } }
		/// <summary>A short statement of what this code means for an injected library, or null.</summary>
		public string? LastErrorMeaning { get { return Win32ErrorNames.Meaning(LastError); } }

		public string Describe() {
			if(!StubCompleted) return "The loader stub did not complete in the target, so the target's loader was never asked about "+Path.GetFileName(LibraryPath)+".";
			if(Loaded) return "The target loaded "+Path.GetFileName(LibraryPath)+".";
			var meaning=LastErrorMeaning;
			return "The target's loader refused "+Path.GetFileName(LibraryPath)+": "+LastErrorName+" ("+LastError.ToString(System.Globalization.CultureInfo.InvariantCulture)+")."+(meaning is null?String.Empty:" "+meaning);
		}
	}

	public sealed class RemoteLibraryLoadException : InvalidOperationException {
		public RemoteLibraryLoadException(RemoteLoadResult result,string libraryPath):base(result.Describe()) {
			Win32Error=result.LastError; Win32ErrorName=result.LastErrorName; StubCompleted=result.StubCompleted; LibraryPath=libraryPath;
		}
		public int Win32Error { get; }
		public string Win32ErrorName { get; }
		public bool StubCompleted { get; }
		public string LibraryPath { get; }
	}

	/// <summary>Symbolic names for the Win32 codes a loader can produce.
	///
	/// The list is reviewed rather than exhaustive, and anything outside it gets a deterministic
	/// <c>WIN32_ERROR_&lt;n&gt;</c> so a message never degrades to a bare number with no shape. The
	/// meanings say what the code implies for a library being injected, which is the question actually
	/// being asked - FormatMessage would answer a more general one, in the host's language, from a
	/// string table that is not ours.</summary>
	static class Win32ErrorNames {
		static readonly Dictionary<int,string> names=new Dictionary<int,string> {
			{2,"ERROR_FILE_NOT_FOUND"},{3,"ERROR_PATH_NOT_FOUND"},{5,"ERROR_ACCESS_DENIED"},{8,"ERROR_NOT_ENOUGH_MEMORY"},
			{11,"ERROR_BAD_FORMAT"},{14,"ERROR_OUTOFMEMORY"},{32,"ERROR_SHARING_VIOLATION"},{87,"ERROR_INVALID_PARAMETER"},
			{126,"ERROR_MOD_NOT_FOUND"},{127,"ERROR_PROC_NOT_FOUND"},{193,"ERROR_BAD_EXE_FORMAT"},{216,"ERROR_EXE_MACHINE_TYPE_MISMATCH"},
			{225,"ERROR_VIRUS_INFECTED"},{226,"ERROR_VIRUS_DELETED"},{267,"ERROR_DIRECTORY"},{577,"ERROR_INVALID_IMAGE_HASH"},
			{998,"ERROR_NOACCESS"},{1114,"ERROR_DLL_INIT_FAILED"},{1157,"ERROR_DLL_NOT_FOUND"},{1455,"ERROR_COMMITMENT_LIMIT"}
		};
		static readonly Dictionary<int,string> meanings=new Dictionary<int,string> {
			{2,"The target does not see a file at that path."},
			{3,"The target does not see that directory."},
			{5,"The target's account cannot read the file or traverse to it. Check the whole path, not only the file."},
			{126,"The file, or one of the libraries it imports, is missing as far as the target is concerned."},
			{127,"A library the file imports is present but does not export something it needs."},
			{193,"The image is not a valid x64 DLL."},
			{216,"The image is built for a different architecture than the target."},
			{577,"Code integrity policy on the target rejected the image's signature."},
			{1114,"The library loaded and its DllMain returned FALSE."},
			{1157,"A library the file depends on could not be found."}
		};
		public static string Name(int code) {
			string? name;
			return names.TryGetValue(code,out name)?name:"WIN32_ERROR_"+code.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
		public static string? Meaning(int code) {
			string? meaning;
			return meanings.TryGetValue(code,out meaning)?meaning:null;
		}
	}
}
