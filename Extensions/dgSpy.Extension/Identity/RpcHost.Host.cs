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
			// Three conditions, not two, because they need three different responses. A contained fault
			// is history: the dispatcher caught it, kept running, and the host still works — reporting
			// that as "degraded" forever after one recovered fault told callers to stop using a host
			// that was fine. "unavailable" is the one that ends the host: the debugger thread is gone,
			// every control operation now fails immediately, and no session it still names is real.
			var dispatcherState=dispatcher is null ? "healthy" : dispatcher.IsShutdown ? "unavailable" : dispatcher.FaultCount==0 ? "healthy" : "faulted";
			var evaluationState=evaluations.State;
			var connectionState=dispatcherState!="unavailable" && evaluationState!="degraded" ? "connected" : "degraded";
			var lastFault=dispatcher?.LastFault;
			if(lastFault?.Length>4096) lastFault=lastFault.Substring(0,4096);
			var dispatcherRecovery=dispatcherState switch {
				"unavailable" => DispatcherUnavailableRecovery,
				"faulted" => "A debugger-thread callback failed and was contained; the host is still usable. last_dispatcher_fault names it. Re-read session state before trusting anything read around that time.",
				_ => null,
			};
			return new HostInfo {
				HostId=rpcSecurity.HostId,
				DisplayName=$"dgSpy on {Environment.MachineName}",
				MachineName=Environment.MachineName,
				DnSpyVersion=DnSpyVersion(),
				DgSpyVersion=Version,
				ExtensionSha256=ExtensionSha256,
				ExtensionPath=ExtensionPath,
				BuildLabel=BuildLabel,
				BuildTimeUtc=BuildTimeUtc,
				BuildCommit=BuildCommit,
				StaleModuleDocumentsDropped=StaleModuleDocumentsDropped,
				OperatingSystem=Environment.OSVersion.VersionString,
				Architecture=Environment.Is64BitProcess ? "X64" : "X86",
				DnSpyProcessId=System.Diagnostics.Process.GetCurrentProcess().Id,
				ConnectionState=connectionState,
				DispatcherState=dispatcherState,
				DispatcherFaultCount=dispatcher?.FaultCount ?? 0,
				LastDispatcherFaultUtc=dispatcher?.LastFaultUtc,
				LastDispatcherFault=lastFault,
				DispatcherRecovery=dispatcherRecovery,
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
		// Build identity for humans. The hashes above prove which build is running, but nobody holds a
		// hex prefix in their head across a dozen rebuilds in an afternoon, which is the situation this
		// exists for: "is this the one I just compiled?" answered at a glance, in the title bar.
		// The timestamp comes from the assembly file's mtime rather than a compile-time constant so it is
		// still right for a worktree build that was never packaged; the commit comes from the deployment
		// manifest and is therefore absent for exactly those unpackaged builds, which is honest — an
		// unpackaged build has no commit anyone can check out.
		public static DateTime? BuildTimeUtc { get; } = SafeBuildTime(ExtensionPath);
		public static string? BuildCommit { get; } = SafeCommit(ExtensionPath);
		public static string BuildLabel { get; } = FormatBuildLabel(BuildTimeUtc,BuildCommit);
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
		static DateTime? SafeBuildTime(string path) {
			if(string.IsNullOrEmpty(path)) return null;
			try { return System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
			catch { return null; }
		}
		// The manifest sits at the root of a deployed payload, four directories above
		// bin\Extensions\dgSpy\. Walking up instead of hardcoding that depth keeps this working if the
		// packaged layout shifts, and finding nothing is the normal case for a dev build.
		static string? SafeCommit(string path) {
			if(string.IsNullOrEmpty(path)) return null;
			try {
				var dir=System.IO.Path.GetDirectoryName(path);
				for(var i=0;i<6 && !string.IsNullOrEmpty(dir);i++,dir=System.IO.Path.GetDirectoryName(dir)) {
					var manifest=System.IO.Path.Combine(dir!,"deployment-manifest.json");
					if(!System.IO.File.Exists(manifest)) continue;
					using var doc=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
					if(!doc.RootElement.TryGetProperty("packaged",out var packaged)) return null;
					if(!packaged.TryGetProperty("git_commit",out var commit)) return null;
					var sha=commit.GetString();
					if(string.IsNullOrEmpty(sha)) return null;
					sha=sha!.Substring(0,Math.Min(8,sha.Length));
					// A dirty build cannot be checked out again, so saying only the commit would name a
					// tree that never existed.
					var dirty=packaged.TryGetProperty("git_dirty",out var d) && d.ValueKind==System.Text.Json.JsonValueKind.True;
					return dirty ? sha+"-dirty" : sha;
				}
				return null;
			}
			catch { return null; }
		}
		// Local time on purpose: this is compared against "when did I hit build", which happened on
		// this machine's clock.
		static string FormatBuildLabel(DateTime? utc,string? commit) {
			if(utc is null) return commit ?? "unknown";
			var stamp=utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
			return commit is null ? stamp : $"{stamp} ({commit})";
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
