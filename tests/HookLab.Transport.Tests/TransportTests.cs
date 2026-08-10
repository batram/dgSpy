using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Probe.CorDebug.Transport;
using Xunit;

namespace HookLab.Transport.Tests;

public sealed class TransportTests {
	[Fact]
	public void CodecRoundTripsOpaqueUnicodePayload() {
		var payload = "{\"text\":\"quote \\\" slash \\\\ emoji \U0001F642\"}";
		var message = new ProbeMessage(1, ProbeMessageKind.Request, "correlation", "observe", payload, 42);
		var decoded = ProbeWireProtocol.Decode(ProbeWireProtocol.Encode(message));
		Assert.Equal(message.ProtocolVersion, decoded.ProtocolVersion); Assert.Equal(message.Kind, decoded.Kind);
		Assert.Equal(message.CorrelationId, decoded.CorrelationId); Assert.Equal(message.Operation, decoded.Operation);
		Assert.Equal(payload, decoded.PayloadJson); Assert.Equal(42, decoded.ExpectedHooksVersion);
	}

	[Fact]
	public void CodecRejectsMalformedBoundariesWithoutFallback() {
		var valid = ProbeWireProtocol.Encode(new ProbeMessage(1, ProbeMessageKind.Request, "c", "o", "{}"));
		Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(valid[..^1]));
		Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(valid.Concat(new byte[] { 0 }).ToArray()));
		var wrongVersion = (byte[])valid.Clone(); BitConverter.GetBytes(2).CopyTo(wrongVersion, 0); Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(wrongVersion));
		var wrongKind = (byte[])valid.Clone(); wrongKind[4] = 255; Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(wrongKind));
		var negativeLength = (byte[])valid.Clone(); BitConverter.GetBytes(-1).CopyTo(negativeLength, 5); Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(negativeLength));
		var overLength = (byte[])valid.Clone(); BitConverter.GetBytes(ProbeWireProtocol.MaximumCorrelationIdBytes + 1).CopyTo(overLength, 5); Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(overLength));
		var marker = (byte[])valid.Clone(); marker[^1] = 2; Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(marker));
		Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(new byte[ProbeWireProtocol.MaximumFrameBytes + 1]));
		var maximum = new string('a', ProbeWireProtocol.MaximumCorrelationIdBytes); Assert.Equal(maximum, ProbeWireProtocol.Decode(ProbeWireProtocol.Encode(new ProbeMessage(1, ProbeMessageKind.Request, maximum, "o", "{}"))).CorrelationId);
		Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Encode(new ProbeMessage(1, ProbeMessageKind.Request, maximum + "a", "o", "{}")));
	}

	[Fact]
	public void CodecRejectsInvalidUtf8() {
		var valid = ProbeWireProtocol.Encode(new ProbeMessage(1, ProbeMessageKind.Request, "c", "o", "{}"));
		valid[9] = 0xc0; Assert.Throws<InvalidDataException>(() => ProbeWireProtocol.Decode(valid));
	}

	[Fact]
	public void ProjectsDeclareFrozenFrameworkAndOrderingIntent() {
		var root = RepositoryRoot();
		var host = XDocument.Load(Path.Combine(root, "HookLab", "HookLab.Host.Transport", "HookLab.Host.Transport.csproj"));
		Assert.Equal("net48;net10.0-windows", Value(host, "TargetFrameworks"));
		var tests = XDocument.Load(Path.Combine(root, "tests", "HookLab.Transport.Tests", "HookLab.Transport.Tests.csproj"));
		var harness = tests.Descendants().Single(x => x.Name.LocalName == "ProjectReference" && ((string?)x.Attribute("Include"))!.Contains("Harness"));
		Assert.Equal("false", (string?)harness.Attribute("ReferenceOutputAssembly")); Assert.Equal("true", (string?)harness.Attribute("SkipGetTargetFrameworkProperties"));
		var harnessProject = XDocument.Load(Path.Combine(root, "tests", "HookLab.Transport.Tests", "Harness", "HookLab.Transport.Harness.csproj"));
		Assert.Equal("net48", Value(harnessProject, "TargetFramework")); Assert.Equal("x64", Value(harnessProject, "PlatformTarget"));
		Assert.Contains("$hookLabHostTransportProject", File.ReadAllText(Path.Combine(root, "build-dgspy.ps1")));
		Assert.Contains("tests\\HookLab.Transport.Tests\\HookLab.Transport.Tests.csproj", File.ReadAllText(Path.Combine(root, "tests", "run-modernization-gate.ps1")));
	}

	[Fact]
	public void DiscoveryLifecycleRoundTripsQuarantinesAndDeletes() {
		using var temporary = new TemporaryDirectory(); var store = new ProbeDiscoveryStore(temporary.Path);
		var process = Process.GetCurrentProcess(); var identity = new TargetIdentity("host", process.MainModule!.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", ".NET", "1");
		var record = new ProbeDiscoveryRecord(identity, "probe", "pipe", ProbeAuthentication.CreateNonce(), ProbeAuthentication.CreateSecret(), 1, DateTime.UtcNow.AddMinutes(1));
		var path = store.Write(record); var found = store.Discover(new ExactIdentity(identity), DateTime.UtcNow).Single(); Assert.Equal(record.ProbeInstanceId, found.ProbeInstanceId);
		var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
		Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected); Assert.All(rules, rule => Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference));
		store.Rotate(found, ProbeAuthentication.CreateSecret());
		var rotated = store.Discover(new ExactIdentity(identity), DateTime.UtcNow).Single();
		Assert.True(rotated.ExpiresUtc > record.ExpiresUtc);
		Assert.Single(store.Discover(new ExactIdentity(identity), record.ExpiresUtc.AddSeconds(1)));
		File.Copy(path, Path.Combine(store.DirectoryPath, "replay.probe")); Assert.Single(store.Discover(new ExactIdentity(identity), DateTime.UtcNow)); Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "replay.probe")));
		store.Delete(record); Assert.Empty(store.Discover(new ExactIdentity(identity), DateTime.UtcNow));
	}

	[Fact]
	public void AStaleTargetIdentityIsQuarantinedWhateverTheRefreshDeadlineSays() {
		using var temporary = new TemporaryDirectory(); var process = Process.GetCurrentProcess(); var identity = new TargetIdentity("host", process.MainModule!.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", ".NET", "1");
		var store = new ProbeDiscoveryStore(temporary.Path); store.Write(new ProbeDiscoveryRecord(identity, "probe", "pipe", ProbeAuthentication.CreateNonce(), ProbeAuthentication.CreateSecret(), 1, DateTime.UtcNow.AddMinutes(1)));
		Assert.Empty(store.Discover(new NeverCurrent(), DateTime.UtcNow));
		Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.probe"));
	}

	/// <summary>A probe that outlives the record lifetime must not report as "nothing installed". That is
	/// what D4's idempotency short-circuit reads, and a false negative there invites a second, permanently
	/// unloadable payload generation into a target that already has one. The deadline is a refresh
	/// deadline; the live-target identity is what decides validity.</summary>
	[Fact]
	public void AProbeOutlivingTheRecordLifetimeIsStillDiscoveredAndRefreshable() {
		using var temporary = new TemporaryDirectory(); var process = Process.GetCurrentProcess();
		var identity = new TargetIdentity("host", process.MainModule!.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", ".NET", "1");
		var store = new ProbeDiscoveryStore(temporary.Path);
		var secret = ProbeAuthentication.CreateSecret();
		// An hour past its deadline: more than ProbeDiscoveryStore.DiscoveryRecordLifetime by an order of
		// magnitude, which is an ordinary rung-2 or rung-3 session length.
		var path = store.Write(new ProbeDiscoveryRecord(identity, "probe", "pipe", ProbeAuthentication.CreateNonce(), secret, 1, DateTime.UtcNow.AddHours(-1)));
		var found = store.Discover(new ExactIdentity(identity), DateTime.UtcNow).Single();
		Assert.Equal("probe", found.ProbeInstanceId);
		Assert.Equal(secret, found.Secret);
		// Discovery is a read: it neither quarantined the record nor rewrote it.
		Assert.True(File.Exists(path));
		Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!, "quarantine")));
		// The refresh a successful health check performs, and its protection is the ordinary one.
		var refreshed = store.Refresh(found);
		var current = store.Discover(new ExactIdentity(identity), DateTime.UtcNow).Single();
		Assert.True(current.ExpiresUtc > DateTime.UtcNow);
		Assert.Equal(secret, current.Secret);
		Assert.True(new FileInfo(refreshed).GetAccessControl().AreAccessRulesProtected);
		Assert.All(new FileInfo(refreshed).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
			rule => Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference));
	}

	/// <summary>Reconciliation after a host restart. Nothing in the host survives it - the action record
	/// store is in-memory - so the record on disk is the whole mechanism, and a second store instance over
	/// the same state root has to find it however long the probe has been up.</summary>
	[Fact]
	public void AHostRestartReconcilesFromTheRecordAlone() {
		using var temporary = new TemporaryDirectory(); var process = Process.GetCurrentProcess();
		var identity = new TargetIdentity("host", process.MainModule!.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", ".NET", "1");
		var nonce = ProbeAuthentication.CreateNonce(); var secret = ProbeAuthentication.CreateSecret();
		new ProbeDiscoveryStore(temporary.Path).Write(new ProbeDiscoveryRecord(identity, "probe", "pipe-name", nonce, secret, 1, DateTime.UtcNow.AddHours(-1)));
		// The restarted host: a fresh store over the same root, with no in-memory state to help it.
		var restarted = new ProbeDiscoveryStore(temporary.Path).Discover(new ExactIdentity(identity), DateTime.UtcNow).Single();
		// Everything a health check needs to prove ownership of the endpoint came back intact.
		Assert.Equal("pipe-name", restarted.PipeName); Assert.Equal(nonce, restarted.EndpointNonce); Assert.Equal(secret, restarted.Secret);
	}

	/// <summary>The deadline still does one thing: a record whose target is gone and whose deadline has
	/// passed is destroyed, not archived. Quarantine kept a DPAPI-protected secret on disk indefinitely -
	/// strictly longer than the lifetime that was supposed to bound it.</summary>
	[Fact]
	public void AnExpiredRecordForADeadTargetIsDeletedRatherThanArchived() {
		using var temporary = new TemporaryDirectory(); var process = Process.GetCurrentProcess();
		var identity = new TargetIdentity("host", process.MainModule!.FileName, process.Id, process.StartTime.ToUniversalTime(), "x64", ".NET", "1");
		var store = new ProbeDiscoveryStore(temporary.Path);
		var path = store.Write(new ProbeDiscoveryRecord(identity, "probe", "pipe", ProbeAuthentication.CreateNonce(), ProbeAuthentication.CreateSecret(), 1, DateTime.UtcNow.AddSeconds(-1)));
		Assert.Empty(store.Discover(new NeverCurrent(), DateTime.UtcNow));
		Assert.False(File.Exists(path));
		var quarantine = Path.Combine(Path.GetDirectoryName(store.DirectoryPath)!, "quarantine");
		Assert.True(!Directory.Exists(quarantine) || Directory.GetFiles(quarantine).Length == 0, "The secret was archived instead of destroyed.");
	}

	[Fact]
	public void Net48ProbeAuthenticatesCommandsRejectsUnauthorizedAndRotates() {
		using var temporary = new TemporaryDirectory(); using var harness = StartHarness("serve", temporary.Path, 20);
		WaitFor(Path.Combine(temporary.Path, "harness-result.txt"));
		var store = new ProbeDiscoveryStore(temporary.Path); var record = store.Discover(new AlwaysCurrent(), DateTime.UtcNow).Single();
		var wrong = (byte[])record.Secret.Clone(); wrong[0] ^= 0xff;
		Assert.Throws<UnauthorizedAccessException>(() => new ProbeConnection(record.PipeName, wrong, record.EndpointNonce, timeoutMilliseconds: 2000));
		var mismatch = Assert.Throws<ProbeProtocolMismatchException>(() => new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce, timeoutMilliseconds: 2000, protocolVersion: 2)); Assert.Equal(1, mismatch.SupportedVersion);
		using (var connection = new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce)) {
			ProbeMessage? observed = null; connection.EventReceived += message => observed = message;
			var status = connection.Send(Request("status", "{}", null)); Assert.Equal(0, status.ExpectedHooksVersion);
			connection.Send(Request("emit", "{}", null)); Assert.NotNull(observed); Assert.Equal(ProbeMessageKind.Event, observed!.Kind); Assert.Contains("observed", observed.PayloadJson);
			var changed = connection.Send(Request("mutate", "{\"value\":1}", 0)); Assert.Equal(1, changed.ExpectedHooksVersion);
			var stale = connection.Send(Request("mutate", "{}", 0)); Assert.Equal("error", stale.Operation);
		}
		// A successful authenticated health check is what extends the record's refresh deadline, against a
		// real probe rather than a hand-written record. The credential is unchanged, so the record the rest
		// of this test uses stays valid.
		var checkedHealth = store.VerifyHealthAndRefresh(record); Assert.Equal("status", checkedHealth.Status.Operation);
		var extended = store.Discover(new AlwaysCurrent(), DateTime.UtcNow).Single();
		Assert.True(extended.ExpiresUtc > record.ExpiresUtc, "A successful health check did not extend the record's refresh deadline.");
		Assert.Equal(record.Secret, extended.Secret);
		var health = store.RecoverAndRotate(record); Assert.Equal("status", health.Status.Operation);
		Assert.Throws<UnauthorizedAccessException>(() => new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce, timeoutMilliseconds: 2000));
		var rotated = store.Discover(new AlwaysCurrent(), DateTime.UtcNow).Single(); using var final = new ProbeConnection(rotated.PipeName, rotated.Secret, rotated.EndpointNonce);
		Assert.Equal("status", final.Send(Request("status", "{}", null)).Operation);
		harness.Kill(); harness.WaitForExit(5000);
	}

	[Fact]
	public void StalledAuthenticationTimesOutAndDoesNotWedgeListener() {
		using var temporary = new TemporaryDirectory();
		using var process = StartHarness("stall-auth", temporary.Path, 1);
		try {
			Assert.True(process.WaitForExit(5000));
			Assert.Equal(0, process.ExitCode);
		}
		finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
	}

	// T09 disposes the probe endpoint on its rollback path, on the target's own thread inside a func-eval whose
	// evaluation timeout is shorter than the two seconds the old Dispose could burn. The harness sweeps the
	// pipe-creation window and requires that every cycle disposes promptly, that the listener thread exits, and
	// that no endpoint is left connectable - a lingering listener is the externally usable endpoint T09 forbids.
	[Fact]
	public void ProbeDisposeIsBoundedAndLeavesNoUsableEndpoint() {
		using var temporary = new TemporaryDirectory();
		using var process = StartHarness("dispose-bounded", temporary.Path, 1);
		try {
			Assert.True(process.WaitForExit(60000), "dispose-bounded harness did not exit.");
			Assert.True(process.ExitCode == 0, "dispose-bounded harness failed: exit " + process.ExitCode + "; " + HarnessReport(temporary.Path));
		}
		finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
	}

	// The host generates the secret and hands it in, so it never crosses the debugger boundary outward and
	// never reaches an activity log, an action record, the RPC wire or an MCP transcript.
	[Fact]
	public void InjectedSecretAuthenticatesIsNotReturnedAndRejectsWeakValues() {
		using var temporary = new TemporaryDirectory();
		using var process = StartHarness("injected-secret", temporary.Path, 1);
		try {
			Assert.True(process.WaitForExit(60000), "injected-secret harness did not exit.");
			Assert.True(process.ExitCode == 0, "injected-secret harness failed: exit " + process.ExitCode + "; " + HarnessReport(temporary.Path));
		}
		finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
	}

	// Dispose closes the transport and returns while a command is still inside the handler, so a rollback
	// that read a completed teardown as "the probe stopped" was reporting a property it had not
	// established - and D5 makes that report the difference between cleanup_outcome completed and
	// ambiguous. The harness blocks inside a side-effecting handler, disposes, and requires that the
	// system keeps saying "in flight" until the handler is cancelled or demonstrably finished.
	[Fact]
	public void AnInFlightCommandIsReportedUntilItIsCancelledOrFinished() {
		using var temporary = new TemporaryDirectory();
		using var process = StartHarness("quiescence", temporary.Path, 1);
		try {
			Assert.True(process.WaitForExit(60000), "quiescence harness did not exit.");
			Assert.True(process.ExitCode == 0, "quiescence harness failed: exit " + process.ExitCode + "; " + HarnessReport(temporary.Path));
		}
		finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
	}

	[Fact]
	public void MutualAuthenticationDetectsPipeNameSquatting() {
		var name = "dgspy-hooklab-squatter-" + Guid.NewGuid().ToString("N"); var nonce = ProbeAuthentication.CreateNonce(); var secret = ProbeAuthentication.CreateSecret();
		using var ready = new ManualResetEventSlim();
		var thread = new Thread(() => { using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1); ready.Set(); pipe.WaitForConnection(); var version = new byte[4]; pipe.ReadExactly(version); pipe.Write(version); pipe.WriteByte(1); var challenge = ProbeAuthentication.CreateNonce(); pipe.Write(challenge); pipe.Flush(); var request = new byte[65]; pipe.ReadExactly(request); pipe.Write(new byte[32]); pipe.Flush(); }) { IsBackground = true };
		thread.Start(); ready.Wait(); Assert.Throws<UnauthorizedAccessException>(() => new ProbeConnection(name, secret, nonce, timeoutMilliseconds: 2000));
	}

	[Fact]
	public void CrossFrameworkDpapiRoundTripsInBothDirectionsWithoutSkip() {
		using (var net48Writer = new TemporaryDirectory()) {
			using var process = StartHarness("dpapi-write", net48Writer.Path, 1); Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode);
			Assert.Single(new ProbeDiscoveryStore(net48Writer.Path).Discover(new AlwaysCurrent(), DateTime.UtcNow));
		}
		using (var net10Writer = new TemporaryDirectory()) {
			var current = Process.GetCurrentProcess(); var identity = new TargetIdentity("cross-tfm", current.MainModule!.FileName, current.Id, current.StartTime.ToUniversalTime(), "x64", ".NET", "1");
			new ProbeDiscoveryStore(net10Writer.Path).Write(new ProbeDiscoveryRecord(identity, "probe", "pipe", ProbeAuthentication.CreateNonce(), ProbeAuthentication.CreateSecret(), 1, DateTime.UtcNow.AddMinutes(1)));
			using var process = StartHarness("dpapi-read", net10Writer.Path, 1); Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode);
		}
	}

	static string Value(XDocument document, string name) => document.Descendants().Single(x => x.Name.LocalName == name).Value;
	static string RepositoryRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dnSpy.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new InvalidOperationException(); }
	sealed class ExactIdentity : ILiveTargetIdentity { readonly TargetIdentity expected; public ExactIdentity(TargetIdentity expected) { this.expected = expected; } public bool IsCurrent(TargetIdentity value) => value.ProcessId == expected.ProcessId && value.ProcessCreationTimeUtc == expected.ProcessCreationTimeUtc && string.Equals(value.ImagePath, expected.ImagePath, StringComparison.OrdinalIgnoreCase); }
	sealed class NeverCurrent : ILiveTargetIdentity { public bool IsCurrent(TargetIdentity identity) => false; }
	sealed class AlwaysCurrent : ILiveTargetIdentity { public bool IsCurrent(TargetIdentity identity) => true; }
	sealed class TemporaryDirectory : IDisposable { public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-transport-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); } public string Path { get; } public void Dispose() { try { Directory.Delete(Path, true); } catch { } } }
	static ProbeMessage Request(string operation, string payload, long? version) => new ProbeMessage(1, ProbeMessageKind.Request, Guid.NewGuid().ToString("N"), operation, payload, version);
	static Process StartHarness(string mode, string root, int seconds) {
		var executable = Path.Combine(RepositoryRoot(), "tests", "HookLab.Transport.Tests", "Harness", "bin", "Release", "net48", "HookLab.Transport.Harness.exe");
		var start = new ProcessStartInfo(executable, $"-Mode {mode} -StateRoot \"{root}\" -Seconds {seconds}") { UseShellExecute = false, CreateNoWindow = true };
		return Process.Start(start) ?? throw new InvalidOperationException("Harness did not start.");
	}
	static string HarnessReport(string root) { var path = Path.Combine(root, "harness-result.txt"); return File.Exists(path) ? File.ReadAllText(path) : "(no harness report written)"; }
	static void WaitFor(string path) { var deadline = DateTime.UtcNow.AddSeconds(5); while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(25); Assert.True(File.Exists(path), "Harness readiness file was not created."); }
}
