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
				using (var server = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{}", 0), authenticationTimeoutMilliseconds: 200)) {
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
			if (mode == "dispose-bounded") return DisposeBounded(result, integrity, int.Parse(Optional(commandLine, "-Iterations") ?? "200"));
			if (mode == "injected-secret") return InjectedSecret(result, integrity);
			if (mode == "quiescence") return Quiescence(result, integrity);
			if (mode == "dispatch-gate") return DispatchGate(result, integrity);
			if (mode == "dispose-window") return DisposeWindow(result, integrity);
			if (mode == "cancel-callback") return CancelCallback(result, integrity);
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
			using (var server = new ProbePipeServer((operation, payload, expected, cancellation) => {
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

	// T09 calls Dispose on its rollback path, on the target's own thread inside a func-eval. Three things must
	// hold on every cycle: Dispose returns promptly, the listener thread actually exits, and no endpoint is left
	// connectable. The spin sweeps the window between pipe creation and its publication, which is where a
	// Dispose used to dispose nothing and leave the listener parked in WaitForConnection for good.
	static int DisposeBounded(string result, string integrity, int iterations) {
		const int boundMilliseconds = 250;
		long worst = 0; var wedged = 0; var failures = 0; var names = new System.Collections.Generic.List<string>();
		for (var index = 0; index < iterations; index++) {
			var server = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{}", 0));
			names.Add(server.PipeName);
			Thread.SpinWait(index % 200 * 8);
			var watch = Stopwatch.StartNew();
			server.Dispose();
			var elapsed = watch.ElapsedMilliseconds;
			if (elapsed > worst) worst = elapsed;
			if (!server.WaitForShutdown(3000)) wedged++;
			if (server.ListenerFailure != null) { failures++; Console.Error.WriteLine(server.ListenerFailure); }
		}
		var connectable = 0;
		foreach (var name in names) {
			try { using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) { client.Connect(20); connectable++; } }
			catch (Exception) { }
		}
		File.WriteAllText(result, integrity + "|worst_ms=" + worst + "|wedged=" + wedged + "|listener_failures=" + failures + "|connectable=" + connectable);
		if (worst > boundMilliseconds) return 10;
		if (wedged != 0) return 11;
		if (failures != 0) return 12;
		if (connectable != 0) return 13;
		return 0;
	}

	// The host generates the secret and passes it in, so it never travels outward and there is nothing
	// downstream to redact. Proves the injected credential authenticates, that the probe refuses to hand it
	// back, that the array is copied, and that a weak or wrong-sized secret is rejected at construction.
	static int InjectedSecret(string result, string integrity) {
		var hostSecret = ProbeAuthentication.CreateSecret();
		var handed = (byte[])hostSecret.Clone();
		using (var server = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{\"status\":\"ready\"}", 7), 5000, handed)) {
			if (!server.SecretWasInjected) return 20;
			try { server.TakeInitialEndpoint(); return 21; } catch (InvalidOperationException) { }
			// The probe must have copied it: clearing the caller's array cannot break authentication.
			Array.Clear(handed, 0, handed.Length);
			using (var connection = new ProbeConnection(server.PipeName, hostSecret, server.EndpointNonce, timeoutMilliseconds: 2000)) {
				var response = connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "injected", "status", "{}"));
				if (response.Operation != "status" || response.ExpectedHooksVersion != 7) return 22;
			}
			var wrong = (byte[])hostSecret.Clone(); wrong[0] ^= 0xff;
			try { new ProbeConnection(server.PipeName, wrong, server.EndpointNonce, timeoutMilliseconds: 2000).Dispose(); return 23; }
			catch (UnauthorizedAccessException) { }
		}
		if (!RejectsSecret(new byte[0])) return 24;
		if (!RejectsSecret(new byte[ProbeAuthentication.SecretBytes - 1])) return 25;
		if (!RejectsSecret(new byte[ProbeAuthentication.SecretBytes + 1])) return 26;
		if (!RejectsSecret(new byte[ProbeAuthentication.SecretBytes])) return 27;                 // right length, all zero
		if (!RejectsSecret(Uniform(ProbeAuthentication.SecretBytes, 0xab))) return 28;            // right length, uniform
		// Self-generation stays the default, so every pre-existing caller is unaffected.
		using (var server = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{}", 0))) {
			if (server.SecretWasInjected) return 29;
			var endpoint = server.TakeInitialEndpoint();
			if (endpoint.Secret.Length != ProbeAuthentication.SecretBytes) return 30;
			using (var connection = new ProbeConnection(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce, timeoutMilliseconds: 2000))
				if (connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "self", "status", "{}")).Operation != "status") return 31;
		}
		File.WriteAllText(result, integrity + "|pass");
		return 0;
	}

	// Dispose closes the transport and returns while a command is still inside the handler, so "the endpoint
	// is unreachable" and "the probe has stopped working" are different facts. A rollback that reported the
	// first as the second was reporting a property it had not established. Two cases here, because the
	// distinction only exists if both hold: a handler that ignores cancellation keeps the system saying
	// ambiguous until it demonstrably finishes, and a handler that honours cancellation is actually reached
	// by it. The first handler deliberately does not observe the token - a patch part-way through cannot
	// always stop, and that is precisely the case the report has to stay honest about.
	static int Quiescence(string result, string integrity) {
		var entered = new ManualResetEvent(false);
		var release = new ManualResetEvent(false);
		// The token as the handler received it. Read directly rather than through anything the handler
		// reports back, because TryQuiesce cancels too: a check that ran after one of those calls would pass
		// whether or not Dispose ever cancelled anything, which is exactly how this test was vacuous first
		// time round.
		var handlerToken = default(CancellationToken);
		var server = new ProbePipeServer((operation, payload, expected, cancellation) => {
			handlerToken = cancellation;
			entered.Set();
			release.WaitOne();
			return new ProbeCommandResult("{}", 0);
		});
		var endpoint = server.TakeInitialEndpoint();
		StartClient(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce);
		if (!entered.WaitOne(10000)) return 40;
		if (server.InFlightCommands != 1) return 41;
		var watch = Stopwatch.StartNew();
		server.Dispose();
		// Unchanged contract: Dispose is still bounded and still does not wait for the handler.
		if (watch.ElapsedMilliseconds > 250) return 42;
		if (server.InFlightCommands != 1) return 43;
		// Dispose cancelled the handler's token by itself. Checked before any TryQuiesce call, which would
		// have cancelled it too and made this pass regardless.
		//
		// Bounded poll rather than an immediate read: Dispose posts the cancellation to a thread of its own
		// instead of running the registered callbacks on the caller's, so delivery is no longer synchronous
		// with the return. The assertion is unchanged in substance - a Dispose that cancels nothing never
		// satisfies it - and nothing else here signals this token, so a pass still comes only from Dispose.
		if (!SpinUntil(() => handlerToken.IsCancellationRequested, 5000)) return 44;
		// The honest answer while a side-effecting command is still running.
		if (server.TryQuiesce(200)) return 45;
		// Now it demonstrably finishes, and only then may a caller call its cleanup completed.
		release.Set();
		if (!server.TryQuiesce(10000)) return 46;
		if (server.InFlightCommands != 0) return 47;

		// The cooperative half: a handler that observes the token is released by the cancellation Dispose
		// requests, with no second signal from anyone - so this waits for it to finish before asking
		// TryQuiesce anything.
		var cooperativeEntered = new ManualResetEvent(false);
		var cooperativeFinished = new ManualResetEvent(false);
		var observed = 0;
		var cooperative = new ProbePipeServer((operation, payload, expected, cancellation) => {
			cooperativeEntered.Set();
			cancellation.WaitHandle.WaitOne(10000);
			if (cancellation.IsCancellationRequested) Interlocked.Exchange(ref observed, 1);
			cooperativeFinished.Set();
			return new ProbeCommandResult("{}", 0);
		});
		var second = cooperative.TakeInitialEndpoint();
		StartClient(second.PipeName, second.Secret, second.EndpointNonce);
		if (!cooperativeEntered.WaitOne(10000)) return 48;
		cooperative.Dispose();
		if (!cooperativeFinished.WaitOne(5000)) return 49;
		if (Volatile.Read(ref observed) != 1) return 50;
		if (!cooperative.TryQuiesce(5000)) return 51;
		// And an idle endpoint quiesces immediately, so the bound is not paid by the ordinary path.
		using (var idle = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{}", 0))) {
			idle.Dispose();
			if (!idle.TryQuiesce(0)) return 52;
		}
		File.WriteAllText(result, integrity + "|pass");
		return 0;
	}

	// The quiescence mode above catches a handler already inside the handler. It structurally cannot catch the
	// other half: a request the listener has already decoded but not yet admitted is work in hand that nothing
	// counts, so a TryQuiesce landing there saw an idle count, answered quiesced, and the handler then started
	// its side effects after the system had reported no probe work was running. The only observation point is
	// inside Serve, hence the internal seam - the window is not reachable from a client.
	//
	// Every handler here deliberately ignores its cancellation token and does nothing but record that it ran.
	// A cooperative handler would refuse the work on its own and hide whether the gate did anything, which is
	// precisely the shape that made an earlier quiescence assertion vacuous.
	static int DispatchGate(string result, string integrity) {
		// Case 1: quiesce, then dispose - the rollback ordering. The decoded request must never reach the
		// handler, because cleanup already reported completed on the strength of the quiesced answer.
		var sideEffect = 0;
		var decoded = new ManualResetEvent(false);
		var release = new ManualResetEvent(false);
		var server = new ProbePipeServer((operation, payload, expected, cancellation) => { Interlocked.Exchange(ref sideEffect, 1); return new ProbeCommandResult("{}", 0); });
		server.DispatchAdmissionProbeForTest = () => { decoded.Set(); release.WaitOne(20000); };
		var endpoint = server.TakeInitialEndpoint();
		StartClient(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce);
		if (!decoded.WaitOne(10000)) return 60;
		// The window is real: the listener holds a decoded request and the count says nothing is running.
		if (server.InFlightCommands != 0) return 61;
		// So the system answers quiesced, and a caller is entitled to call its cleanup completed.
		if (!server.TryQuiesce(1000)) return 62;
		server.Dispose();
		release.Set();
		// The listener now leaves the window. Waiting for it to unwind is what makes the next assertion mean
		// "the handler never ran" rather than "the handler had not run yet".
		if (!server.WaitForShutdown(10000)) return 63;
		if (Volatile.Read(ref sideEffect) != 0) return 64;
		if (server.InFlightCommands != 0) return 65;

		// Case 2: quiesce without dispose. The transport is still open, so a refusal that silently dropped the
		// request would leave the client waiting out its own timeout for a command that will never run. It is
		// answered instead, and the answer names the shutdown rather than some incidental exception type.
		var openEffect = 0;
		var openDecoded = new ManualResetEvent(false);
		var openRelease = new ManualResetEvent(false);
		var answered = new ManualResetEvent(false);
		var answer = "none";
		var open = new ProbePipeServer((operation, payload, expected, cancellation) => { Interlocked.Exchange(ref openEffect, 1); return new ProbeCommandResult("{}", 0); });
		open.DispatchAdmissionProbeForTest = () => { openDecoded.Set(); openRelease.WaitOne(20000); };
		var openEndpoint = open.TakeInitialEndpoint();
		var client = new Thread(() => {
			try {
				using (var connection = new ProbeConnection(openEndpoint.PipeName, openEndpoint.Secret, openEndpoint.EndpointNonce, timeoutMilliseconds: 5000)) {
					var response = connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "refused", "mutate", "{}"));
					answer = response.Operation + "|" + response.PayloadJson;
				}
			}
			catch (Exception ex) { answer = "threw:" + ex.GetType().Name; }
			finally { answered.Set(); }
		}) { IsBackground = true };
		client.Start();
		if (!openDecoded.WaitOne(10000)) return 66;
		if (!open.TryQuiesce(1000)) return 67;
		openRelease.Set();
		if (!answered.WaitOne(10000)) return 68;
		if (answer != "error|{\"error\":\"probe_shutting_down\"}") { File.WriteAllText(result, integrity + "|answer=" + answer); return 69; }
		if (Volatile.Read(ref openEffect) != 0) return 70;
		open.Dispose();

		// Case 3: dispose alone, with nobody asking for quiescence. Dispose's contract is that the endpoint is
		// unreachable by the time it returns; a request already decoded would otherwise still be dispatched and
		// start mutating the target after its caller was told the endpoint was gone. Closing the transport is
		// not by itself an answer about work in hand - that is the same conflation TryQuiesce exists to fix.
		var closingEffect = 0;
		var closingDecoded = new ManualResetEvent(false);
		var closingRelease = new ManualResetEvent(false);
		var closing = new ProbePipeServer((operation, payload, expected, cancellation) => { Interlocked.Exchange(ref closingEffect, 1); return new ProbeCommandResult("{}", 0); });
		closing.DispatchAdmissionProbeForTest = () => { closingDecoded.Set(); closingRelease.WaitOne(20000); };
		var closingEndpoint = closing.TakeInitialEndpoint();
		StartClient(closingEndpoint.PipeName, closingEndpoint.Secret, closingEndpoint.EndpointNonce);
		if (!closingDecoded.WaitOne(10000)) return 72;
		// Nothing calls TryQuiesce here, deliberately: it closes the gate too, and asking it would make this
		// pass whether or not Dispose closed anything.
		closing.Dispose();
		closingRelease.Set();
		if (!closing.WaitForShutdown(10000)) return 73;
		if (Volatile.Read(ref closingEffect) != 0) return 74;

		// And the gate is closed only by shutdown: an endpoint nobody has quiesced still serves commands, so a
		// gate that refused everything could not pass here.
		using (var serving = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{\"status\":\"ready\"}", 3))) {
			var servingEndpoint = serving.TakeInitialEndpoint();
			using (var connection = new ProbeConnection(servingEndpoint.PipeName, servingEndpoint.Secret, servingEndpoint.EndpointNonce, timeoutMilliseconds: 5000)) {
				var response = connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "admitted", "status", "{}"));
				if (response.Operation != "status" || response.ExpectedHooksVersion != 3) return 71;
			}
		}
		File.WriteAllText(result, integrity + "|pass");
		return 0;
	}

	// Case 3 of dispatch-gate holds the listener at admission across a whole Dispose: it is released only after
	// Dispose has returned, so the gate is necessarily already closed by then and the case passes under any
	// ordering of Dispose's steps. That is the blind spot this mode exists for. Here the listener is released
	// from *inside* Dispose, after the transport is closed and before cancellation - the interval in which the
	// gate used to still be open - and the requirement is unchanged: once Dispose has returned, no command
	// decoded before or during it may have begun executing.
	//
	// Nothing here calls TryQuiesce, at any point, deliberately: TryQuiesce closes the gate itself, so a single
	// call anywhere before the assertion would supply the very property the assertion is meant to detect. That
	// mistake has already made two assertions in this file vacuous. For the same reason the handler ignores its
	// cancellation token and does nothing but record that it ran: a cooperative handler would refuse the work on
	// its own account and hide whether the gate did anything.
	static int DisposeWindow(string result, string integrity) {
		const int iterations = 5;
		for (var index = 0; index < iterations; index++) {
			var sideEffect = 0;
			var decoded = new ManualResetEvent(false);
			var release = new ManualResetEvent(false);
			var handlerEntered = new ManualResetEvent(false);
			var server = new ProbePipeServer((operation, payload, expected, cancellation) => {
				Interlocked.Exchange(ref sideEffect, 1); handlerEntered.Set();
				return new ProbeCommandResult("{}", 0);
			});
			server.DispatchAdmissionProbeForTest = () => { decoded.Set(); release.WaitOne(20000); };
			// Releases the listener in the middle of Dispose and then gives it a real chance to be admitted:
			// the wait returns as soon as the handler is entered, so a Dispose that still admits it loses the
			// race deterministically rather than by luck, and one that refuses it pays this bound once.
			server.ShutdownProbeForTest = () => { release.Set(); handlerEntered.WaitOne(500); };
			var endpoint = server.TakeInitialEndpoint();
			StartClient(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce);
			if (!decoded.WaitOne(10000)) return 80;
			// The window is real: a request is in the listener's hand and nothing counts it.
			if (server.InFlightCommands != 0) return 81;
			server.Dispose();
			// Nothing may have started by the time Dispose returned; waiting for the listener to unwind is what
			// makes the assertion "the handler never ran" rather than "it had not run yet".
			if (Volatile.Read(ref sideEffect) != 0) { File.WriteAllText(result, integrity + "|admitted_during_dispose|iteration=" + index); return 82; }
			release.Set();
			if (!server.WaitForShutdown(10000)) return 83;
			if (Volatile.Read(ref sideEffect) != 0) { File.WriteAllText(result, integrity + "|admitted_after_dispose|iteration=" + index); return 84; }
			if (server.InFlightCommands != 0) return 85;
			if (server.ListenerFailure != null) { File.WriteAllText(result, integrity + "|listener_failure=" + server.ListenerFailure); return 86; }
		}
		// And the reordering did not turn Dispose into a gate that refuses on a healthy endpoint: one with no
		// shutdown in progress still serves commands.
		using (var serving = new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{\"status\":\"ready\"}", 5))) {
			var servingEndpoint = serving.TakeInitialEndpoint();
			using (var connection = new ProbeConnection(servingEndpoint.PipeName, servingEndpoint.Secret, servingEndpoint.EndpointNonce, timeoutMilliseconds: 5000)) {
				var response = connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "admitted", "status", "{}"));
				if (response.Operation != "status" || response.ExpectedHooksVersion != 5) return 87;
			}
		}
		File.WriteAllText(result, integrity + "|pass");
		return 0;
	}

	// CancellationTokenSource.Cancel runs every registered callback inline on the thread that calls it, so for as
	// long as the probe cancelled on its caller's thread, a handler that registered a blocking cleanup callback
	// owned both of this class's bounds: Dispose - whose caller can be the target's own thread inside a func-eval
	// with a shorter evaluation timeout than the callback - and TryQuiesce, which would overrun its caller's
	// timeout before reaching the wait that timeout describes. The two bounds are different promises and are
	// measured separately here: Dispose must not wait at all, TryQuiesce may wait up to what it was given.
	//
	// The existing quiescence mode cannot see this: its handlers wait on the token's wait handle and register
	// nothing, so Cancel has no callback to run and returns instantly whichever thread it is on.
	static int CancelCallback(string result, string integrity) {
		const int callbackBlockMilliseconds = 3000;
		const int disposeBoundMilliseconds = 250;
		const int quiesceBoundMilliseconds = 250;
		var entered = new ManualResetEvent(false);
		var callbackEntered = new ManualResetEvent(false);
		var releaseCallback = new ManualResetEvent(false);
		var releaseHandler = new ManualResetEvent(false);
		var callbackThread = 0;
		var server = new ProbePipeServer((operation, payload, expected, cancellation) => {
			// The shape a patching command takes: cleanup registered against the token, and cleanup that can
			// take longer than a func-eval is willing to wait.
			cancellation.Register(() => { Interlocked.Exchange(ref callbackThread, Thread.CurrentThread.ManagedThreadId); callbackEntered.Set(); releaseCallback.WaitOne(callbackBlockMilliseconds); });
			entered.Set();
			releaseHandler.WaitOne(20000);
			return new ProbeCommandResult("{}", 0);
		});
		var endpoint = server.TakeInitialEndpoint();
		StartClient(endpoint.PipeName, endpoint.Secret, endpoint.EndpointNonce);
		if (!entered.WaitOne(10000)) return 90;
		var disposingThread = Thread.CurrentThread.ManagedThreadId;
		var watch = Stopwatch.StartNew();
		server.Dispose();
		var disposeElapsed = watch.ElapsedMilliseconds;
		if (disposeElapsed > disposeBoundMilliseconds) { File.WriteAllText(result, integrity + "|dispose_ms=" + disposeElapsed); return 91; }
		// The cancellation was genuinely delivered, not merely skipped to keep the bound - and it ran somewhere
		// other than the thread that called Dispose, which is the whole mechanism.
		if (!callbackEntered.WaitOne(5000)) return 92;
		if (Volatile.Read(ref callbackThread) == disposingThread) return 93;
		// TryQuiesce's own bound, measured while the same callback is still blocking. It may wait up to what it
		// was given and no longer, and it must not inherit Dispose's promise of not waiting at all either: the
		// handler is still inside, so the honest answer is false.
		var quiesceWatch = Stopwatch.StartNew();
		var quiesced = server.TryQuiesce(quiesceBoundMilliseconds);
		var quiesceElapsed = quiesceWatch.ElapsedMilliseconds;
		if (quiesced) return 94;
		if (quiesceElapsed > quiesceBoundMilliseconds + 250) { File.WriteAllText(result, integrity + "|quiesce_ms=" + quiesceElapsed); return 95; }
		releaseCallback.Set();
		releaseHandler.Set();
		if (!server.TryQuiesce(10000)) return 96;
		if (server.InFlightCommands != 0) return 97;
		if (server.ListenerFailure != null) { File.WriteAllText(result, integrity + "|listener_failure=" + server.ListenerFailure); return 98; }
		File.WriteAllText(result, integrity + "|pass|dispose_ms=" + disposeElapsed + "|quiesce_ms=" + quiesceElapsed);
		return 0;
	}

	static bool SpinUntil(Func<bool> condition, int millisecondsTimeout) {
		var deadline = Stopwatch.StartNew();
		while (deadline.ElapsedMilliseconds < millisecondsTimeout) { if (condition()) return true; Thread.Sleep(5); }
		return condition();
	}

	static void StartClient(string pipeName, byte[] secret, byte[] nonce) {
		var thread = new Thread(() => {
			// The response never arrives - the transport is disposed under the handler - so every failure
			// here is expected. The point of the client is only to get a command into the handler.
			try {
				using (var connection = new ProbeConnection(pipeName, secret, nonce, timeoutMilliseconds: 5000))
					connection.Send(new ProbeMessage(1, ProbeMessageKind.Request, "quiesce", "mutate", "{}"));
			}
			catch (Exception) { }
		}) { IsBackground = true };
		thread.Start();
	}

	static bool RejectsSecret(byte[] candidate) {
		try { new ProbePipeServer((operation, payload, expected, cancellation) => new ProbeCommandResult("{}", 0), 5000, candidate).Dispose(); return false; }
		catch (ArgumentException) { return true; }
	}

	static byte[] Uniform(int length, byte value) { var buffer = new byte[length]; for (var index = 0; index < length; index++) buffer[index] = value; return buffer; }

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
