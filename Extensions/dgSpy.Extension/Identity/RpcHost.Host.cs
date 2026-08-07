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
		// Version is content-derived on purpose. A hand-maintained constant cannot go stale in a way anyone
		// notices: a deployment three commits behind still reported "0.1.0", so a black-box tester spent two
		// rounds reproducing bugs that were already fixed. Appending the assembly hash makes every answer
		// name the build that produced it.
		// Computed on read, not in a field initializer: static initializers run in textual order and this
		// one reads fields declared further down, which would silently capture an empty hash.
		public static string Version => $"{ExtensionInformationalVersion()}+{ExtensionSha256.Substring(0,Math.Min(12,ExtensionSha256.Length))}";
		HostInfo Host() {
			string? session; lock(sync) session=sessionId;
			var dispatcher=manager.Dispatcher as dnSpy.Contracts.Debugger.IDbgDispatcherDiagnostics;
			var dispatcherState=dispatcher is null || dispatcher.FaultCount==0 ? "healthy" : "degraded";
			var evaluationState=evaluations.State;
			var connectionState=dispatcherState=="healthy" && evaluationState!="degraded" ? "connected" : "degraded";
			var lastFault=dispatcher?.LastFault;
			if(lastFault?.Length>4096) lastFault=lastFault.Substring(0,4096);
			return new HostInfo {
				HostId=rpcSecurity.HostId,
				DisplayName=$"dgSpy on {Environment.MachineName}",
				MachineName=Environment.MachineName,
				DnSpyVersion=DnSpyVersion(),
				DgSpyVersion=Version,
				ExtensionSha256=ExtensionSha256,
				ExtensionPath=ExtensionPath,
				OperatingSystem=Environment.OSVersion.VersionString,
				Architecture=Environment.Is64BitProcess ? "X64" : "X86",
				DnSpyProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,
				ConnectionState=connectionState,
				DispatcherState=dispatcherState,
				DispatcherFaultCount=dispatcher?.FaultCount ?? 0,
				LastDispatcherFaultUtc=dispatcher?.LastFaultUtc,
				LastDispatcherFault=lastFault,
				EvaluationQueueState=evaluationState,
				EvaluationActiveSinceUtc=evaluations.ActiveSinceUtc,
				EvaluationPending=evaluations.Pending,
				Engines=Array.ConvertAll(CapabilityCatalog.Engines,engine=>engine.Engine),
				Authentication="shared-token (loopback-only transport)",
				SessionId=session,
			};
		}
		// Identity of the assembly this process actually loaded, not of the tree someone believes is
		// deployed. Diagnostics must never be the thing that breaks diagnostics, so every failure here
		// degrades to "unknown" rather than throwing out of get_host_info.
		static readonly Assembly ExtensionAssembly = typeof(RpcHost).Assembly;
		public static string ExtensionPath { get; } = SafeLocation();
		public static string ExtensionSha256 { get; } = SafeHash(ExtensionPath);
		static string SafeLocation() {
			try { return ExtensionAssembly.Location ?? ""; } catch { return ""; }
		}
		static string SafeHash(string path) {
			if(string.IsNullOrEmpty(path)) return "unknown";
			try {
				using var stream=System.IO.File.OpenRead(path);
				using var sha=System.Security.Cryptography.SHA256.Create();
				return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
			}
			catch { return "unknown"; }
		}
		static string ExtensionInformationalVersion() =>
			ExtensionAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? ExtensionAssembly.GetName().Version?.ToString() ?? "unknown";

		static string DnSpyVersion() {
			var assembly=Assembly.GetEntryAssembly() ?? typeof(dnSpy.Contracts.Debugger.DbgManager).Assembly;
			return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? assembly.GetName().Version?.ToString() ?? "unknown";
		}
	}
}
