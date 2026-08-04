using System;
using System.Reflection;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	// Host identity and capability advertisement. The identity is stable across restarts and may be
	// explicitly configured when this endpoint is registered behind a tunnel.
	// The static half of the capability contract lives in dgSpy.Protocol.CapabilityCatalog, because the
	// gateway derives its per-tool deadlines from the same table. Anything dynamic — versions, machine,
	// whether a session is live — is filled in here.
	sealed partial class RpcHost {
		public const string Version = "0.1.0";
		HostInfo Host() {
			string? session; lock(sync) session=sessionId;
			return new HostInfo {
				HostId=rpcSecurity.HostId,
				DisplayName=$"dgSpy on {Environment.MachineName}",
				MachineName=Environment.MachineName,
				DnSpyVersion=DnSpyVersion(),
				DgSpyVersion=Version,
				OperatingSystem=Environment.OSVersion.VersionString,
				Architecture=Environment.Is64BitProcess ? "X64" : "X86",
				DnSpyProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,
				ConnectionState="connected",
				Engines=Array.ConvertAll(CapabilityCatalog.Engines,engine=>engine.Engine),
				Authentication="shared-token (loopback-only transport)",
				SessionId=session,
			};
		}
		static string DnSpyVersion() {
			var assembly=Assembly.GetEntryAssembly() ?? typeof(dnSpy.Contracts.Debugger.DbgManager).Assembly;
			return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? assembly.GetName().Version?.ToString() ?? "unknown";
		}
	}
}
