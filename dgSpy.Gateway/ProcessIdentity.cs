using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace dgSpy.Gateway;

/// <summary>Identity questions about another process that <see cref="Process"/> cannot answer across an
/// integrity boundary. <c>Process.MainModule</c> walks the target's module list, which needs
/// <c>PROCESS_VM_READ</c>; a medium-integrity Gateway is refused that against an elevated dnSpy owned by
/// the same user, and the refusal arrives as an exception that reads exactly like "no such process".
/// That mattered once the host could be elevated: the running-host scan silently stopped seeing the very
/// process it exists to find, and the caller was then told to start a second dnSpy into an endpoint the
/// first still owned. <c>QueryFullProcessImageName</c> needs only
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which the same-user DACL grants across integrity levels, so
/// it answers in both directions.</summary>
static class ProcessIdentity {
	const int PROCESS_QUERY_LIMITED_INFORMATION=0x1000;
	const uint TOKEN_QUERY=0x0008;
	const int TokenElevation=20;

	[DllImport("kernel32.dll",SetLastError=true)]
	static extern IntPtr OpenProcess(int access,bool inheritHandle,int processId);
	[DllImport("kernel32.dll",SetLastError=true)]
	[return:MarshalAs(UnmanagedType.Bool)]
	static extern bool CloseHandle(IntPtr handle);
	[DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="QueryFullProcessImageNameW")]
	[return:MarshalAs(UnmanagedType.Bool)]
	static extern bool QueryFullProcessImageName(IntPtr process,int flags,StringBuilder buffer,ref int size);
	[DllImport("advapi32.dll",SetLastError=true)]
	[return:MarshalAs(UnmanagedType.Bool)]
	static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
	[DllImport("advapi32.dll",SetLastError=true)]
	[return:MarshalAs(UnmanagedType.Bool)]
	static extern bool GetTokenInformation(IntPtr token,int informationClass,out int information,int length,out int returned);

	/// <summary>Full path of a running process's executable, or null when it cannot be determined. Null
	/// means "unknown", never "not ours": every caller has to decide what to do about a dnSpy it cannot
	/// identify rather than quietly treating it as absent.</summary>
	internal static string? ImagePath(Process process) {
		var byHandle=Query(process,handle=>{
			var size=1024; var buffer=new StringBuilder(size);
			return QueryFullProcessImageName(handle,0,buffer,ref size) ? buffer.ToString() : null;
		});
		if(byHandle is not null) return byHandle;
		// Same-integrity fallback. It answers nothing the call above cannot, but it keeps the scan working
		// if OpenProcess is ever denied by policy rather than by integrity.
		try { return process.MainModule?.FileName; } catch { return null; }
	}

	/// <summary>Whether a running process holds an elevated token, or null when that cannot be
	/// determined. Callers that asked for elevation must treat null as "no": reporting an adopted host as
	/// elevated on the strength of a failed query is the one answer that cannot be checked afterwards.
	/// </summary>
	internal static bool? IsElevated(int processId) => Query(processId,handle=>{
		if(!OpenProcessToken(handle,TOKEN_QUERY,out var token)) return null;
		try { return GetTokenInformation(token,TokenElevation,out var elevation,sizeof(int),out _) ? elevation!=0 : (bool?)null; }
		finally { CloseHandle(token); }
	});

	static T? Query<T>(Process process,Func<IntPtr,T?> read) { try { return Query(process.Id,read); } catch(InvalidOperationException) { return default; } }
	static T? Query<T>(int processId,Func<IntPtr,T?> read) {
		if(processId<=0) return default;
		var handle=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,false,processId);
		if(handle==IntPtr.Zero) return default;
		try { return read(handle); }
		catch(Win32Exception) { return default; }
		finally { CloseHandle(handle); }
	}
}
