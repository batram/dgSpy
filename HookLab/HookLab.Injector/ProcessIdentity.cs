using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HookLab.Injector {
	/// <summary>
	/// Who a process is running as.
	///
	/// HookLab needs this because "the debugger's identity" and "the target's identity" are two
	/// different answers, and every place that asked <see cref="WindowsIdentity.GetCurrent"/> silently
	/// returned the first one. A staging directory in the debugger's <c>%TEMP%</c> is unreadable to a
	/// target running as a service account, and nothing at the call site said a question with two
	/// possible answers had been asked.
	/// </summary>
	public static class ProcessIdentity {
		const uint ProcessQueryLimitedInformation=0x1000;
		const uint TokenQuery=0x0008;
		const int TokenUser=1;

		/// <summary>The SID the process runs as, or null when it cannot be read.
		///
		/// <para>Null is an answer, not an error: a protected process, or one owned by an account this
		/// host cannot open, genuinely cannot be identified from here. Callers report that as a
		/// precondition that could not be evaluated rather than assuming the identities match, because
		/// assuming they match is what produced an unreadable staging directory.</para></summary>
		public static SecurityIdentifier? TryGetUserSid(int processId) {
			var process=OpenProcess(ProcessQueryLimitedInformation,false,processId);
			if(process==IntPtr.Zero) return null;
			var token=IntPtr.Zero;
			try {
				if(!OpenProcessToken(process,TokenQuery,out token)) return null;
				uint size=0;
				GetTokenInformation(token,TokenUser,IntPtr.Zero,0,out size);
				if(size==0) return null;
				var buffer=Marshal.AllocHGlobal((int)size);
				try {
					if(!GetTokenInformation(token,TokenUser,buffer,size,out size)) return null;
					// TOKEN_USER is a SID_AND_ATTRIBUTES whose first field is the SID pointer.
					var sid=Marshal.ReadIntPtr(buffer);
					return sid==IntPtr.Zero?null:new SecurityIdentifier(sid);
				}
				finally { Marshal.FreeHGlobal(buffer); }
			}
			catch(Win32Exception) { return null; }
			catch(ArgumentException) { return null; }
			finally {
				if(token!=IntPtr.Zero) CloseHandle(token);
				CloseHandle(process);
			}
		}

		/// <summary>This process's own SID.</summary>
		public static SecurityIdentifier? TryGetCurrentSid() {
			try { using var identity=WindowsIdentity.GetCurrent(); return identity.User; }
			catch(Exception) { return null; }
		}

		[DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inheritHandle,int processId);
		[DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
		[DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
		[DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int informationClass,IntPtr information,uint length,out uint returnLength);
	}
}
