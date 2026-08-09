extern alias hosttransport;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HookLab.Contracts;
using HookLab.Probe.CorDebug.Transport;
using HookLab.Probe.CorDebug.Patching;
using ILiveTargetIdentity = hosttransport::HookLab.Host.Transport.Discovery.ILiveTargetIdentity;
using ProbeDiscoveryRecord = hosttransport::HookLab.Host.Transport.Discovery.ProbeDiscoveryRecord;
using ProbeDiscoveryStore = hosttransport::HookLab.Host.Transport.Discovery.ProbeDiscoveryStore;
using ProbeConnection = hosttransport::HookLab.Host.Transport.ProbeConnection;
using DiscoveryCredentialProtection = hosttransport::HookLab.Host.Transport.Discovery.DiscoveryCredentialProtection;

internal static class Program {
	static int Main(string[] commandLine) {
		try {
			var mode = Required(commandLine, "-Mode"); var root = Required(commandLine, "-StateRoot");
			var integrity = Integrity(); var result = Optional(commandLine, "-Result") ?? Path.Combine(root, "harness-result.txt"); Directory.CreateDirectory(root);
			if (mode == "dpapi-read") { var records = new ProbeDiscoveryStore(root).Discover(new CurrentTarget(), DateTime.UtcNow); File.WriteAllText(result, integrity + "|" + records.Count); return records.Count == 1 ? 0 : 2; }
			if (mode == "dpapi-write") { var record = Record(ProbeAuthentication.CreateSecret(), ProbeAuthentication.CreateNonce(), "unused"); new ProbeDiscoveryStore(root).Write(record); File.WriteAllText(result, integrity + "|written"); return 0; }
			if (mode == "dpapi-blob-write") { File.WriteAllBytes(Required(commandLine, "-Blob"), DiscoveryCredentialProtection.Protect(Encoding.UTF8.GetBytes("cross-account-secret"))); File.WriteAllText(result, integrity + "|protected"); return 0; }
			if (mode == "dpapi-blob-read") { var plain = DiscoveryCredentialProtection.Unprotect(File.ReadAllBytes(Required(commandLine, "-Blob"))); File.WriteAllText(result, integrity + "|" + Encoding.UTF8.GetString(plain)); return 0; }
			if (mode == "stall-auth") {
				using (var server = new ProbePipeServer((operation, payload, expected) => new ProbeCommandResult("{}", 0), authenticationTimeoutMilliseconds: 200)) {
					var endpoint = server.TakeInitialEndpoint();
					using (var stalled = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut)) {
						stalled.Connect(2000);
						try { if (stalled.ReadByte() != -1) return 4; } catch (IOException) { }
					}
					using (var connection = new ProbeConnection(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce, timeoutMilliseconds: 2000)) {
						var response = connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "stall-recovery", "status", "{}"));
						if (response.Operation != "status") return 4;
					}
				}
				File.WriteAllText(result, integrity + "|pass"); return 0;
			}
			if (mode == "client") {
				var record = Single(new ProbeDiscoveryStore(root).Discover(new CurrentTarget(), DateTime.UtcNow));
				using (var connection = new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce)) {
					var request = new ProbeMessage(1, ProbeMessageKind.Request, Guid.NewGuid().ToString("N"), "status", "{}");
					var response = connection.Send(request); if (response.Operation != "status") return 3;
				}
				File.WriteAllText(result, integrity + "|" + Process.GetCurrentProcess().MainModule.FileName + "|" + record.Target.ProcessId + "|" + record.Target.ImagePath + "|pass"); return 0;
			}
			if (mode != "serve") throw new ArgumentException("Unknown mode.");
			long version = 0;
			ProbePipeServer? activeServer = null;
			using (var server = new ProbePipeServer((operation, payload, expected) => {
				if (operation == "status") return new ProbeCommandResult("{\"status\":\"ready\"}", version);
				if (operation == "emit") { activeServer!.EventsAvailable(new OneEventSource()); return new ProbeCommandResult("{}", version); }
				if (expected != version) throw new InvalidOperationException("stale_hooks_version");
				version++; return new ProbeCommandResult(payload, version);
			})) {
				activeServer = server;
				var endpoint = server.TakeInitialEndpoint(); var record = Record(endpoint.Secret, endpoint.EndpointNonce, endpoint.PipeName); var store = new ProbeDiscoveryStore(root); store.Write(record);
				File.WriteAllText(result, integrity + "|" + Process.GetCurrentProcess().MainModule.FileName + "|" + record.Target.ProcessId + "|ready");
				Thread.Sleep(TimeSpan.FromSeconds(int.Parse(Required(commandLine, "-Seconds")))); store.Delete(record);
			}
			return 0;
		} catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
	}

	static ProbeDiscoveryRecord Record(byte[] secret, byte[] nonce, string pipe) {
		var process = Process.GetCurrentProcess();
		var target = new TargetIdentity("transport-harness", process.MainModule.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", Environment.Version.ToString(), AppDomain.CurrentDomain.Id.ToString());
		return new ProbeDiscoveryRecord(target, "harness-probe", pipe, nonce, secret, 1, DateTime.UtcNow.AddMinutes(5));
	}
	static string Required(string[] values, string name) { var index = Array.IndexOf(values, name); if (index < 0 || index + 1 == values.Length) throw new ArgumentException(name + " is required."); return values[index + 1]; }
	static string? Optional(string[] values, string name) { var index = Array.IndexOf(values, name); return index < 0 || index + 1 == values.Length ? null : values[index + 1]; }
	static T Single<T>(System.Collections.Generic.IReadOnlyList<T> values) { if (values.Count != 1) throw new InvalidOperationException("Expected exactly one discovery record, found " + values.Count + "."); return values[0]; }
	static string Integrity() {
		if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008, out var token)) throw new System.ComponentModel.Win32Exception();
		try { var elevation = new TOKEN_ELEVATION(); var size = Marshal.SizeOf(elevation); if (!GetTokenInformation(token, 20, ref elevation, size, out _)) throw new System.ComponentModel.Win32Exception(); return elevation.TokenIsElevated != 0 ? "elevated" : "medium"; }
		finally { CloseHandle(token); }
	}
	[StructLayout(LayoutKind.Sequential)] struct TOKEN_ELEVATION { public int TokenIsElevated; }
	[DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
	[DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);
	[DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
	sealed class CurrentTarget : ILiveTargetIdentity {
		public bool IsCurrent(TargetIdentity identity) {
			var process = OpenProcess(0x1000, false, identity.ProcessId); if (process == IntPtr.Zero) return false;
			try {
				var capacity = 32768; var image = new StringBuilder(capacity);
				if (!QueryFullProcessImageName(process, 0, image, ref capacity)) return false;
				if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return false;
				return DateTime.FromFileTimeUtc(((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime) == identity.ProcessCreationTimeUtc &&
					string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(identity.ImagePath), StringComparison.OrdinalIgnoreCase);
			} finally { CloseHandle(process); }
		}
	}
	sealed class OneEventSource : IHookEventSource {
		bool delivered;
		public System.Collections.Generic.IReadOnlyList<HookEvent> Drain(int maximumCount) { if (delivered) return Array.Empty<HookEvent>(); delivered = true; return new[] { new HookEvent("harness-probe", "patch", 0, 1, DateTime.UtcNow, Thread.CurrentThread.ManagedThreadId, "{\"observed\":true}", false, 0) }; }
		public long DroppedCount => 0;
	}
	[StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
	[DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
	[DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
}
