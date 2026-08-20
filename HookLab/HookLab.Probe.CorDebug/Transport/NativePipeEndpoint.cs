using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HookLab.Probe.CorDebug.Transport {
	/// <summary>Creates the control endpoint through the Win32 API, and is the only type in this assembly
	/// that names it.
	///
	/// <para>It exists because Unity's Mono implements neither <c>WindowsIdentity.GetCurrent().User</c>
	/// nor <c>PipeSecurity.AddAccessRule</c> - measured on Mono 6.13 with a Unity 2021.3 player's own
	/// class libraries, 2026-08-20 - so a resident there cannot state, let alone build, the access
	/// control that keeps another account off a channel able to patch the process. Everything those two
	/// managed APIs do is available through advapi32, which Mono marshals to perfectly well.</para>
	///
	/// <para>The descriptor this produces is byte-identical to the one the managed path produces on
	/// CLR v4, verified by reading it back off the kernel object rather than by trusting the request:
	/// <c>D:P(A;;0x1f019f;;;&lt;sid&gt;)</c>, plus the controller's ACE when the accounts differ. Identical
	/// is the requirement - a Mono endpoint that were merely "also protected" would be a second, unstated
	/// security policy.</para>
	///
	/// <para>Deliberately a fallback rather than the primary path. CLR v4 and CoreCLR already build this
	/// descriptor through APIs they implement, and replacing working managed code with interop on those
	/// runtimes would be risk bought for nothing.</para></summary>
	static class NativePipeEndpoint {
		/// <summary>PipeAccessRights.FullControl. Spelled out rather than written <c>GA</c> in the SDDL:
		/// Generic-All maps to FILE_ALL_ACCESS (0x1f01ff), which is two bits wider - FILE_EXECUTE and
		/// FILE_DELETE_CHILD, both meaningless on a pipe and both a difference from what CLR v4 grants.</summary>
		const string OwnerMask = "0x1f019f";
		/// <summary>PipeAccessRights.ReadWrite as the managed path actually renders it, standard rights
		/// included. Measured, not derived: READ_CONTROL and SYNCHRONIZE are part of what
		/// <c>AddAccessRule</c> writes and are easy to omit by reasoning about the enum alone.</summary>
		const string ControllerMask = "0x12019b";

		const uint PIPE_ACCESS_DUPLEX = 0x00000003;
		const uint FILE_FLAG_OVERLAPPED = 0x40000000;
		/// <summary>PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT - all zero, and the same byte-mode
		/// blocking pipe the managed constructor creates.</summary>
		const uint PIPE_MODE_BYTE_WAIT = 0x00000000;
		const uint TOKEN_QUERY = 0x0008;
		const int TokenUser = 1;
		const uint SDDL_REVISION_1 = 1;

		[StructLayout(LayoutKind.Sequential)]
		struct SECURITY_ATTRIBUTES { internal int nLength; internal IntPtr lpSecurityDescriptor; internal int bInheritHandle; }

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
			uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SECURITY_ATTRIBUTES securityAttributes);
		[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
		[DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr handle);
		[DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
		[DllImport("advapi32.dll", SetLastError = true)]
		static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
		[DllImport("advapi32.dll", SetLastError = true)]
		static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
		[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
		[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out int size);

		/// <param name="maxInstances">Matches the managed path. Only the first instance's descriptor is
		/// honoured by Windows; later instances of the same name inherit it, exactly as they do today.</param>
		internal static NamedPipeServerStream Create(string pipeName, SecurityIdentifier? controllerSid, int maxInstances, int bufferSize) {
			if (pipeName == null) throw new ArgumentNullException(nameof(pipeName));
			var ownSid = CurrentUserSid();
			var sddl = "D:P(A;;" + OwnerMask + ";;;" + ownSid + ")";
			// The controller is a second principal, never a replacement, and is skipped when it is the same
			// account - the same rule the managed path follows, so the same-user case keeps the same DACL.
			if (controllerSid != null && !string.Equals(controllerSid.Value, ownSid, StringComparison.OrdinalIgnoreCase))
				sddl += "(A;;" + ControllerMask + ";;;" + controllerSid.Value + ")";

			IntPtr descriptor;
			int descriptorSize;
			if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out descriptor, out descriptorSize))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not build the control endpoint's security descriptor.");
			try {
				var attributes = new SECURITY_ATTRIBUTES {
					nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
					lpSecurityDescriptor = descriptor,
					bInheritHandle = 0,
				};
				// FILE_FLAG_OVERLAPPED is what makes the wrapped stream asynchronous; without it the
				// NamedPipeServerStream below would be a synchronous pipe wearing an async flag.
				var handle = CreateNamedPipeW("\\\\.\\pipe\\" + pipeName,
					PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED, PIPE_MODE_BYTE_WAIT,
					(uint)maxInstances, (uint)bufferSize, (uint)bufferSize, 0, ref attributes);
				if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the control endpoint.");
				try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
				catch { handle.Dispose(); throw; }
			}
			finally { LocalFree(descriptor); }
		}

		/// <summary>This process's user SID, the way <c>WindowsIdentity.GetCurrent().User</c> would have
		/// given it.</summary>
		static string CurrentUserSid() {
			IntPtr token;
			if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the process token.");
			try {
				int needed;
				GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out needed);
				if (needed <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the process token's user information.");
				var buffer = Marshal.AllocHGlobal(needed);
				try {
					if (!GetTokenInformation(token, TokenUser, buffer, needed, out needed))
						throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the process token's user information.");
					// TOKEN_USER begins with a SID_AND_ATTRIBUTES whose first field is the SID pointer.
					var sid = Marshal.ReadIntPtr(buffer);
					IntPtr text;
					if (!ConvertSidToStringSidW(sid, out text))
						throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not format the process user's SID.");
					try { return Marshal.PtrToStringUni(text) ?? throw new InvalidOperationException("The process user's SID formatted as nothing."); }
					finally { LocalFree(text); }
				}
				finally { Marshal.FreeHGlobal(buffer); }
			}
			finally { CloseHandle(token); }
		}
	}
}
