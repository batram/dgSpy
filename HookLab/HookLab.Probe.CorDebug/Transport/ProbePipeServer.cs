using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using HookLab.Contracts;
using HookLab.Probe.CorDebug.Patching;

namespace HookLab.Probe.CorDebug.Transport {
	public sealed class ProbeEndpoint {
		internal ProbeEndpoint(string pipeName, byte[] secret, byte[] endpointNonce) { PipeName = pipeName; Secret = (byte[])secret.Clone(); EndpointNonce = (byte[])endpointNonce.Clone(); }
		public string PipeName { get; }
		public byte[] Secret { get; }
		public byte[] EndpointNonce { get; }
	}

	public sealed class ProbeCommandResult {
		public ProbeCommandResult(string payloadJson, long hooksVersion) { PayloadJson = payloadJson ?? throw new ArgumentNullException(nameof(payloadJson)); HooksVersion = hooksVersion; }
		public string PayloadJson { get; }
		public long HooksVersion { get; }
	}

	/// <summary>Serves one client command.
	///
	/// <paramref name="cancellationToken"/> is cancelled when the endpoint is disposed or asked to quiesce.
	/// A handler that mutates the target - patching, unpatching, anything with an effect that outlives the
	/// response - must observe it, because <see cref="ProbePipeServer.Dispose"/> closes the transport and
	/// returns while this delegate is still on the stack: without it, a rollback reports a completed
	/// teardown over a probe that is still changing the target, and only the response write fails.
	///
	/// Cancellation is cooperative, so it cannot be the whole answer on its own - a handler part-way
	/// through a patch may be unable to stop. <see cref="ProbePipeServer.TryQuiesce"/> is the other half:
	/// it reports, under a bound, whether the handler actually left. Completed cleanup is the two together;
	/// either one alone is ambiguous cleanup.</summary>
	public delegate ProbeCommandResult ProbeCommandHandler(string operation, string payloadJson, long? expectedHooksVersion, CancellationToken cancellationToken);

	public sealed class ProbePipeServer : IDisposable, IHookEventConsumer {
		readonly object secretGate = new object();
		readonly object sendGate = new object();
		/// <summary>Serializes publication of <see cref="activePipe"/> against <see cref="Dispose"/>. See
		/// the comment on <see cref="Listen"/> for why a plain assignment inside the loop body was not enough.</summary>
		readonly object pipeGate = new object();
		readonly ProbeCommandHandler commandHandler;
		readonly byte[] endpointNonce;
		readonly string pipeName;
		readonly Thread listener;
		readonly int authenticationTimeoutMilliseconds;
		readonly bool secretWasInjected;
		readonly ManualResetEvent stopped = new ManualResetEvent(false);
		/// <summary>Serializes the in-flight command count against <see cref="commandsIdle"/>.</summary>
		readonly object commandGate = new object();
		/// <summary>Set exactly while no command is inside the handler. Never disposed, for the same reason
		/// <see cref="stopped"/> is not: a Set racing a Dispose would throw ObjectDisposedException out of the
		/// listener thread delegate, and an unhandled exception on a Thread terminates the process on .NET
		/// Framework - and that process is the debuggee.</summary>
		readonly ManualResetEvent commandsIdle = new ManualResetEvent(true);
		/// <summary>Cancelled by <see cref="Dispose"/> and by <see cref="TryQuiesce"/>. Never disposed: the
		/// listener thread reads its Token, and a disposed source throws there.</summary>
		readonly CancellationTokenSource commandCancellation = new CancellationTokenSource();
		int inFlightCommands;
		volatile bool disposed;
		int endpointTaken;
		byte[] secret;
		NamedPipeServerStream? activePipe;
		volatile Stream? authenticatedPipe;

		/// <param name="injectedSecret">When supplied, the endpoint credential the host has already generated,
		/// so it never has to travel outward from the target and there is nothing downstream to redact. Must be
		/// exactly <see cref="ProbeAuthentication.SecretBytes"/> bytes and not a single repeated byte value. The
		/// array is copied, so the caller may clear its own. When null (the default) the probe generates its own
		/// secret and hands it out once through <see cref="TakeInitialEndpoint"/>, which is the behaviour every
		/// pre-existing caller gets.</param>
		public ProbePipeServer(ProbeCommandHandler commandHandler, int authenticationTimeoutMilliseconds = 5000, byte[]? injectedSecret = null) {
			this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
			if (authenticationTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(authenticationTimeoutMilliseconds));
			this.authenticationTimeoutMilliseconds = authenticationTimeoutMilliseconds;
			secretWasInjected = injectedSecret != null;
			secret = injectedSecret == null ? ProbeAuthentication.CreateSecret() : ValidateInjectedSecret(injectedSecret);
			endpointNonce = ProbeAuthentication.CreateNonce();
			pipeName = "dgspy-hooklab-" + Guid.NewGuid().ToString("N");
			listener = new Thread(Listen) { IsBackground = true, Name = "HookLab probe pipe" }; listener.Start();
		}

		/// <summary>The endpoint the host connects to. Not a credential: knowing it without the secret proves nothing.</summary>
		public string PipeName => pipeName;

		/// <summary>The per-endpoint nonce mixed into every challenge proof. Not a credential either - it is an
		/// HMAC input, and without the secret it does not let anyone compute a proof - so unlike the secret it
		/// may be reported outward. Returned as a copy so a caller cannot mutate the live value.</summary>
		public byte[] EndpointNonce => (byte[])endpointNonce.Clone();

		/// <summary>True when the host supplied the secret, in which case it was never generated here and
		/// <see cref="TakeInitialEndpoint"/> refuses rather than sending a known value back out.</summary>
		public bool SecretWasInjected => secretWasInjected;

		/// <summary>Set when the listener thread ended on an exception rather than on shutdown. Null is the
		/// normal case; a non-null value means the endpoint stopped accepting connections without being disposed.</summary>
		public string? ListenerFailure { get; private set; }

		public ProbeEndpoint TakeInitialEndpoint() {
			if (secretWasInjected) throw new InvalidOperationException("The probe secret was injected by the host, which already holds it; it is deliberately not returned outward.");
			if (Interlocked.Exchange(ref endpointTaken, 1) != 0) throw new InvalidOperationException("The initial probe credential has already been returned.");
			lock (secretGate) return new ProbeEndpoint(pipeName, secret, endpointNonce);
		}

		static byte[] ValidateInjectedSecret(byte[] injectedSecret) {
			if (injectedSecret.Length != ProbeAuthentication.SecretBytes) throw new ArgumentException("The injected probe secret must be exactly " + ProbeAuthentication.SecretBytes + " bytes.", nameof(injectedSecret));
			// An all-zero or otherwise uniform buffer is what an uninitialized or truncated caller produces, and
			// it is the one weakness this side can detect without keeping a history of past secrets.
			var difference = 0; for (var index = 0; index < injectedSecret.Length; index++) difference |= injectedSecret[index] ^ injectedSecret[0];
			if (difference == 0) throw new ArgumentException("The injected probe secret is weak: every byte has the same value.", nameof(injectedSecret));
			return (byte[])injectedSecret.Clone();
		}

		void Listen() {
			try {
				while (!disposed) {
					NamedPipeServerStream pipe;
					// Publish under the gate and re-check disposed inside it. Assigning activePipe after
					// CreatePipe() returned left a window in which a Dispose saw a null activePipe, disposed
					// nothing, and left this thread parked in WaitForConnection() on a pipe nobody would ever
					// connect to. Measured, not reasoned: 180 of 400 construct-then-dispose cycles wedged.
					lock (pipeGate) {
						if (disposed) return;
						pipe = CreatePipe();
						activePipe = pipe;
					}
					try {
						// If Dispose ran between the gate release and here it has already disposed this pipe,
						// so WaitForConnection throws instead of parking. Measured on net48: disposing the
						// server stream releases a thread already parked in WaitForConnection in 0 ms, with
						// IOException "The pipe has been ended" - no client connect is needed to wake it.
						pipe.WaitForConnection(); if (!disposed) Serve(pipe);
					}
					catch (Exception) when (disposed) { }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
					catch (InvalidDataException) { }
					finally {
						authenticatedPipe = null;
						lock (pipeGate) activePipe = null;
						try { pipe.Dispose(); } catch { }
					}
				}
			}
			// Nothing may escape this delegate. An unhandled exception on a Thread terminates the process on
			// .NET Framework, and this process is the debuggee - measured, by exactly that route. It is recorded
			// rather than discarded, for the same reason ProbeRuntime counts consumer dispatch failures: a
			// listener that died looks identical to one that shut down cleanly otherwise.
			catch (Exception ex) { ListenerFailure = ex.GetType().FullName + ": " + ex.Message; }
			// Never disposed by Dispose(): a Dispose that stopped owning this handle while the listener may
			// still reach this line would make Set() throw ObjectDisposedException out of a thread delegate,
			// which terminates the process on .NET Framework - and that process is the debuggee. Measured.
			// The SafeWaitHandle finalizer reclaims the handle.
			finally { stopped.Set(); }
		}

		NamedPipeServerStream CreatePipe() {
			var identity = WindowsIdentity.GetCurrent();
			var sid = identity.User ?? throw new InvalidOperationException("The probe process has no Windows user SID.");
			var security = new PipeSecurity();
			security.SetAccessRuleProtection(true, false);
			security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
			return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous, 4096, 4096, security, HandleInheritability.None);
		}

		void Serve(Stream pipe) {
			Authenticate(pipe); authenticatedPipe = pipe;
			while (!disposed) {
				ProbeMessage request;
				try { request = ProbeWireProtocol.Decode(ProbeWireProtocol.ReadFrame(pipe)); }
				catch (EndOfStreamException) { return; }
				if (request.Kind != ProbeMessageKind.Request) throw new InvalidDataException("Only request messages are accepted from clients.");
				ProbeMessage response;
				try {
					// Counted around the handler, not around the read: quiescence is a question about the
					// handler's side effects, and a listener parked waiting for the next request is idle by
					// any definition a rollback cares about.
					EnterCommand();
					ProbeCommandResult result;
					try { result = commandHandler(request.Operation, request.PayloadJson, request.ExpectedHooksVersion, commandCancellation.Token); }
					finally { ExitCommand(); }
					response = new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Response, request.CorrelationId,
						request.Operation, result.PayloadJson, result.HooksVersion);
				}
				catch (Exception ex) {
					response = new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Response, request.CorrelationId,
						"error", "{\"error\":\"" + Escape(ex.GetType().Name) + "\"}", null);
				}
				lock (sendGate) ProbeWireProtocol.WriteFrame(pipe, ProbeWireProtocol.Encode(response));
			}
		}

		void Authenticate(Stream pipe) {
			var timedOut = 0;
			using (var deadline = new Timer(_ => { Interlocked.Exchange(ref timedOut, 1); try { pipe.Dispose(); } catch { } }, null, authenticationTimeoutMilliseconds, Timeout.Infinite)) {
				try {
					var clientVersion = BitConverter.ToInt32(ReadExactly(pipe, sizeof(int)), 0);
					var compatible = clientVersion == ProbeWireProtocol.ProtocolVersion;
					var versionResponse = new byte[sizeof(int) + 1]; BitConverter.GetBytes(ProbeWireProtocol.ProtocolVersion).CopyTo(versionResponse, 0); versionResponse[sizeof(int)] = compatible ? (byte)1 : (byte)0;
					pipe.Write(versionResponse, 0, versionResponse.Length); pipe.Flush();
					if (!compatible) throw new InvalidDataException("Client protocol version is incompatible.");
					var serverChallenge = ProbeAuthentication.CreateNonce(); pipe.Write(serverChallenge, 0, serverChallenge.Length); pipe.Flush();
					var request = ReadExactly(pipe, 1 + ProbeAuthentication.NonceBytes + 32);
					if (request[0] > 1) throw new InvalidDataException("Invalid authentication mode.");
					var clientChallenge = new byte[ProbeAuthentication.NonceBytes]; Buffer.BlockCopy(request, 1, clientChallenge, 0, clientChallenge.Length);
					var supplied = new byte[32]; Buffer.BlockCopy(request, 1 + clientChallenge.Length, supplied, 0, supplied.Length);
					byte[] current; lock (secretGate) current = (byte[])secret.Clone();
					var expected = ProbeAuthentication.ClientProof(current, serverChallenge, clientChallenge, endpointNonce);
					if (!ProbeAuthentication.FixedTimeEquals(supplied, expected)) {
						// Complete the fixed-size proof exchange so the client reports authentication rejection,
						// not an ambiguous EOF or connect failure.
						var rejectedProof = new byte[32]; pipe.Write(rejectedProof, 0, rejectedProof.Length); pipe.Flush();
						throw new UnauthorizedAccessException("Probe authentication failed.");
					}
					var serverProof = ProbeAuthentication.ServerProof(current, serverChallenge, clientChallenge, endpointNonce);
					pipe.Write(serverProof, 0, serverProof.Length); pipe.Flush();
					if (request[0] == 1) lock (secretGate) { var rotated = ProbeAuthentication.DeriveRotatedSecret(current, serverChallenge, clientChallenge); Array.Clear(secret, 0, secret.Length); secret = rotated; }
				}
				catch (ObjectDisposedException ex) when (Volatile.Read(ref timedOut) != 0) { throw new IOException("Probe authentication timed out.", ex); }
			}
		}

		public void EventsAvailable(IHookEventSource source) {
			// T04 invokes this away from hook callbacks. T05 deliberately performs no polling; the T04-owned
			// notification re-arm defect must be fixed at its source before delivery acceptance can be claimed.
			while (true) {
				var target = authenticatedPipe; if (target == null) return;
				var events = source.Drain(1); if (events.Count == 0) return;
				foreach (var item in events) {
					var payload = "{\"probe_instance_id\":\"" + Escape(item.ProbeInstanceId) + "\",\"patch_id\":\"" + Escape(item.PatchId) +
						"\",\"hooks_version\":" + item.HooksVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
						",\"sequence\":" + item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
						",\"timestamp_utc\":\"" + item.TimestampUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) +
						"\",\"managed_thread_id\":" + item.ManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
						",\"truncated\":" + (item.Truncated ? "true" : "false") + ",\"dropped_count\":" + item.DroppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
						",\"payload\":" + item.PayloadJson + "}";
					var message = new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Event, item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), "hook_event", payload, item.HooksVersion);
					try { lock (sendGate) ProbeWireProtocol.WriteFrame(target, ProbeWireProtocol.Encode(message)); }
					catch (IOException) { authenticatedPipe = null; return; }
				}
			}
		}

		void EnterCommand() { lock (commandGate) { if (inFlightCommands++ == 0) commandsIdle.Reset(); } }

		void ExitCommand() { lock (commandGate) { if (--inFlightCommands == 0) commandsIdle.Set(); } }

		/// <summary>How many client commands are inside the handler right now. Zero does not by itself mean
		/// the endpoint is finished - use <see cref="TryQuiesce"/>, which cancels first and then observes.</summary>
		public int InFlightCommands { get { lock (commandGate) return inFlightCommands; } }

		/// <summary>Requests cancellation of every in-flight command and waits, bounded, for them to leave
		/// the handler. True means no command is running: a caller may say its cleanup completed. False means
		/// one is still inside the handler and may still be mutating the target, which is ambiguous cleanup
		/// with a reconciliation to follow, never a completed one.
		///
		/// This exists because <see cref="Dispose"/> alone cannot answer the question. It closes the
		/// transport and returns; a side-effecting handler keeps running and only its response write fails.
		/// So "the endpoint is unreachable" and "no probe work is running" are different facts, and a
		/// rollback that reported the first as the second was reporting a property it had not established.
		///
		/// Safe to call before, after, or instead of <see cref="Dispose"/>, and repeatedly: cancellation is
		/// idempotent, and the wait observes rather than mutates.</summary>
		public bool TryQuiesce(int millisecondsTimeout) {
			if (millisecondsTimeout < 0) throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
			CancelCommands();
			return commandsIdle.WaitOne(millisecondsTimeout);
		}

		/// <summary>Never throws. Cancel runs registered callbacks on this thread, and this thread can be the
		/// target's own inside a func-eval; a callback that throws must not become the caller's problem, and
		/// must not stop the endpoint being closed.</summary>
		void CancelCommands() { try { commandCancellation.Cancel(); } catch (Exception) { } }

		/// <summary>Bounded and non-blocking: it closes the listening endpoint and returns. It deliberately does
		/// not wait for the listener thread and does not dispose <c>stopped</c>.
		///
		/// The caller can be the target's own thread inside a debugger func-eval, where the previous two-second
		/// wait outran the evaluation timeout and destroyed a correct in-target guard report on its way out. The
		/// endpoint stops accepting connections before this returns, so a rollback path does not leave an
		/// externally usable endpoint behind; only the thread's own unwind is unobserved, and
		/// <see cref="WaitForShutdown"/> exists for tests that need to observe it.
		///
		/// It also cancels any command inside the handler, and deliberately does not wait for it to leave.
		/// A returned Dispose therefore establishes that the endpoint is unreachable - not that the probe has
		/// stopped working. <see cref="TryQuiesce"/> is what establishes the second, under a bound its caller
		/// chooses.</summary>
		public void Dispose() {
			if (disposed) return; disposed = true;
			NamedPipeServerStream? current;
			lock (pipeGate) current = activePipe;
			// Disposing the server stream both closes the endpoint and releases a listener already parked in
			// WaitForConnection. Taking the gate first is what guarantees there is something here to dispose:
			// either the listener published its pipe and this sees it, or it has not reached the gate yet and
			// will observe disposed and exit without creating one.
			try { current?.Dispose(); } catch { }
			// After the endpoint is closed, so a handler woken by cancellation can never be handed a client
			// connection made in between. Cancellation is a request, not a guarantee: whether the handler
			// actually left is what TryQuiesce answers, and this method deliberately does not wait for it -
			// the caller can be the target's own thread inside a func-eval, and the bound belongs to them.
			CancelCommands();
		}

		/// <summary>Waits for the listener thread to finish unwinding. Nothing in production calls this - it
		/// exists so tests can assert the thread exited without any caller paying for the wait. Returns false on
		/// timeout rather than throwing.</summary>
		public bool WaitForShutdown(int millisecondsTimeout) {
			if (millisecondsTimeout < 0) throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
			return stopped.WaitOne(millisecondsTimeout);
		}

		static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
		static byte[] ReadExactly(Stream stream, int count) { var value = new byte[count]; var offset = 0; while (offset < count) { var read = stream.Read(value, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } return value; }
	}
}
