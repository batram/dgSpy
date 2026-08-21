using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
		/// <summary>Shared by all three creation paths - the CoreCLR ACL helper, the .NET Framework
		/// constructor, and the native one Mono needs - because an endpoint that differed in shape
		/// depending on which runtime built it would be three endpoints wearing one name.</summary>
		const int MaximumInstances = 254;
		const int BufferSize = 4096;

		readonly object secretGate = new object();
		readonly object sendGate = new object();
		/// <summary>Serializes publication of listening and connected pipes against <see cref="Dispose"/>. See
		/// the comment on <see cref="Listen"/> for why a plain assignment inside the loop body was not enough.</summary>
		readonly object pipeGate = new object();
		readonly ProbeCommandHandler commandHandler;
		readonly byte[] endpointNonce;
		readonly string pipeName;
		readonly SecurityIdentifier? controllerSid;
		readonly Thread listener;
		readonly int authenticationTimeoutMilliseconds;
		readonly bool authenticationEnabled;
		readonly bool secretWasInjected;
		readonly ManualResetEvent stopped = new ManualResetEvent(false);
		readonly ManualResetEvent listening = new ManualResetEvent(false);
		/// <summary>Serializes the in-flight command count, the dispatch gate and <see cref="commandsIdle"/>
		/// against each other. Admission and quiescence observation must be one atomic decision: see
		/// <see cref="TryEnterCommand"/>.</summary>
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
		/// <summary>Guarded by <see cref="commandGate"/>. Once set, no further command is admitted to the
		/// handler. Closed by <see cref="TryQuiesce"/> and <see cref="Dispose"/>.</summary>
		bool commandGateClosed;
		/// <summary>Set once, by whichever of <see cref="Dispose"/> and <see cref="TryQuiesce"/> gets there
		/// first, so exactly one thread is ever started to run the cancellation callbacks.</summary>
		int cancellationRequested;
		volatile bool disposed;
		int endpointTaken;
		byte[] secret;
		readonly HashSet<NamedPipeServerStream> activePipes = new HashSet<NamedPipeServerStream>();
		volatile Stream? authenticatedPipe;

		/// <param name="injectedSecret">When supplied, the endpoint credential the host has already generated,
		/// so it never has to travel outward from the target and there is nothing downstream to redact. Must be
		/// exactly <see cref="ProbeAuthentication.SecretBytes"/> bytes and not a single repeated byte value. The
		/// array is copied, so the caller may clear its own. When null (the default) the probe generates its own
		/// secret and hands it out once through <see cref="TakeInitialEndpoint"/>, which is the behaviour every
		/// pre-existing caller gets.</param>
		public ProbePipeServer(ProbeCommandHandler commandHandler, int authenticationTimeoutMilliseconds = 5000, byte[]? injectedSecret = null, bool authenticationEnabled = true, SecurityIdentifier? controllerSid = null) {
			this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
			this.controllerSid = controllerSid;
			if (authenticationTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(authenticationTimeoutMilliseconds));
			this.authenticationTimeoutMilliseconds = authenticationTimeoutMilliseconds;
			this.authenticationEnabled = authenticationEnabled;
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

		public bool WaitUntilListening(int timeoutMilliseconds) {
			if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
			return listening.WaitOne(timeoutMilliseconds) && ListenerFailure == null;
		}

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

		/// <summary>True where closing the endpoint does not release a listener parked in
		/// <c>WaitForConnection</c>. Mono, and asked by runtime type rather than by corlib name, which it
		/// shares with CLR v4.</summary>
		static bool ListenerNeedsWakeup => Type.GetType("Mono.Runtime") is not null;

		/// <summary>Connects to this endpoint once, so a parked listener returns and can see that the server
		/// is disposed. Best-effort and bounded: if the connect fails the listener was not parked, or is
		/// already gone, and either way there is nothing here worth failing a teardown over.
		///
		/// <para>It reaches the resident's own listener rather than a client's, so it proves nothing and
		/// authenticates nothing - the connection is dropped immediately and the listener discards it
		/// because <c>disposed</c> is already set before this runs.</para></summary>
		void WakeListener() {
			try {
				using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
					client.Connect(WakeupTimeoutMilliseconds);
			}
			catch (Exception) { }
		}

		const int WakeupTimeoutMilliseconds = 250;

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
						activePipes.Add(pipe);
						listening.Set();
					}
					try {
						// If Dispose ran between the gate release and here it has already disposed this pipe,
						// so WaitForConnection throws instead of parking. Measured on net48: disposing the
						// server stream releases a thread already parked in WaitForConnection in 0 ms, with
						// IOException "The pipe has been ended" - no client connect is needed to wake it.
						pipe.WaitForConnection();
						if (disposed) continue;
						// A connected controller must not own the accept loop. The resident is shared by dgSpy
						// and the watcher, so keep this pipe in the active set and serve it independently while
						// this thread immediately publishes the next listener instance.
						var connected = pipe;
						new Thread(() => ServeConnected(connected)) { IsBackground = true, Name = "HookLab probe client" }.Start();
						pipe = null!;
					}
					catch (Exception) when (disposed) { }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
					catch (InvalidDataException) { }
					finally {
						if (pipe != null) {
							lock (pipeGate) activePipes.Remove(pipe);
							try { pipe.Dispose(); } catch { }
						}
					}
				}
			}
			// Nothing may escape this delegate. An unhandled exception on a Thread terminates the process on
			// .NET Framework, and this process is the debuggee - measured, by exactly that route. It is recorded
			// rather than discarded, for the same reason ProbeRuntime counts consumer dispatch failures: a
			// listener that died looks identical to one that shut down cleanly otherwise.
			catch (Exception ex) { ListenerFailure = ex.GetType().FullName + ": " + ex.Message; listening.Set(); }
			// Never disposed by Dispose(): a Dispose that stopped owning this handle while the listener may
			// still reach this line would make Set() throw ObjectDisposedException out of a thread delegate,
			// which terminates the process on .NET Framework - and that process is the debuggee. Measured.
			// The SafeWaitHandle finalizer reclaims the handle.
			finally { stopped.Set(); }
		}

		void ServeConnected(NamedPipeServerStream pipe) {
			try { if (!disposed) Serve(pipe); }
			catch (Exception) when (disposed) { }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
			catch (InvalidDataException) { }
			finally {
				Interlocked.CompareExchange(ref authenticatedPipe, null, pipe);
				lock (pipeGate) activePipes.Remove(pipe);
				try { pipe.Dispose(); } catch { }
			}
		}

		NamedPipeServerStream CreatePipe() {
			SecurityIdentifier sid;
			var security = new PipeSecurity();
			try {
				var identity = WindowsIdentity.GetCurrent();
				sid = identity.User ?? throw new InvalidOperationException("The probe process has no Windows user SID.");
				security.SetAccessRuleProtection(true, false);
				security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
			}
			// Unity's Mono implements neither WindowsIdentity.User nor PipeSecurity.AddAccessRule -
			// measured on Mono 6.13 with a Unity 2021.3 player's own class libraries, 2026-08-20. The
			// endpoint's access control is not decoration there is a fallback for: it is what keeps another
			// account off a channel that can patch this process. So rather than create an endpoint whose
			// protection nobody stated, build the identical descriptor through the Win32 API, which that
			// runtime does implement. An endpoint that cannot be protected is still refused - by
			// NativePipeEndpoint, naming the call that failed.
			catch (NotImplementedException) {
				return NativePipeEndpoint.Create(pipeName, controllerSid, MaximumInstances, BufferSize);
			}
			// The controller is a second principal, never a replacement: the DACL stays protected and fully
			// enumerated. Without this, a target running under a different account than the debugger - an IIS
			// worker under a service account, say - creates a pipe its own controller cannot open, and the
			// initialization succeeds right up to the point where the host tries to talk to it. Skipped when it
			// is the same account, so the same-user case keeps exactly the DACL it had.
			if (controllerSid != null && controllerSid != sid)
				security.AddAccessRule(new PipeAccessRule(controllerSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
			Type? aclType = null;
			try {
				var aclAssemblyPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.IO.Pipes.AccessControl.dll");
				if (File.Exists(aclAssemblyPath)) aclType = Assembly.LoadFile(aclAssemblyPath).GetType("System.IO.Pipes.NamedPipeServerStreamAcl", false);
				else aclType = Assembly.Load(new AssemblyName("System.IO.Pipes.AccessControl")).GetType("System.IO.Pipes.NamedPipeServerStreamAcl", false);
			}
			catch (FileNotFoundException) { }
			if (aclType != null) {
				var create = aclType.GetMethods(BindingFlags.Public | BindingFlags.Static);
				foreach (var method in create) {
					var parameters = method.GetParameters();
					if (method.Name != "Create" || parameters.Length != 10) continue;
					return (NamedPipeServerStream)method.Invoke(null, new object[] { pipeName, PipeDirection.InOut, MaximumInstances,
						PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferSize, BufferSize, security,
						HandleInheritability.None, (PipeAccessRights)0 })!;
				}
				throw new MissingMethodException(aclType.FullName, "Create");
			}
			return CreateFrameworkPipe(pipeName, security);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		static NamedPipeServerStream CreateFrameworkPipe(string name, PipeSecurity security) =>
			new NamedPipeServerStream(name, PipeDirection.InOut, MaximumInstances, PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous, BufferSize, BufferSize, security, HandleInheritability.None);

		void Serve(Stream pipe) {
			if (authenticationEnabled) Authenticate(pipe); else NegotiateVersion(pipe);
			authenticatedPipe = pipe;
			while (!disposed) {
				ProbeMessage request;
				try { request = ProbeWireProtocol.Decode(ProbeWireProtocol.ReadFrame(pipe)); }
				catch (EndOfStreamException) { return; }
				if (request.Kind != ProbeMessageKind.Request) throw new InvalidDataException("Only request messages are accepted from clients.");
				// Test-only seam. Decoding a request and admitting it are separate steps, and the whole point
				// of the gate is what happens to a shutdown that lands between them - a window no test can
				// reach from outside. Null in production; see the property.
				DispatchAdmissionProbeForTest?.Invoke();
				ProbeMessage response;
				try {
					// Counted around the handler, not around the read: quiescence is a question about the
					// handler's side effects, and a listener parked waiting for the next request is idle by
					// any definition a rollback cares about.
					//
					// Admission is a gate, not just a count. A request decoded before a shutdown began is work
					// this thread already has in hand while nothing is counted, so a TryQuiesce running here
					// would see commandsIdle set, answer quiesced, and let the handler start its side effects
					// after the system reported that no probe work was running. TryEnterCommand closes that
					// window from the other side: it and the gate-close happen under one lock, so a command
					// either entered first - and quiescence waits for it - or is refused.
					if (!TryEnterCommand()) {
						// Answered rather than dropped. The refusal is a fact the caller needs: its command did
						// not run, which is different from a command that ran and whose response was lost. When
						// Dispose already closed the transport this write throws and the listener unwinds as it
						// would have anyway, so naming the shutdown costs nothing and is the only thing a client
						// on a still-open pipe (TryQuiesce without Dispose) ever gets instead of hanging to its
						// own timeout.
						try { lock (sendGate) ProbeWireProtocol.WriteFrame(pipe, ProbeWireProtocol.Encode(new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Response, request.CorrelationId, "error", "{\"error\":\"probe_shutting_down\"}", null))); }
						catch (IOException) { }
						catch (ObjectDisposedException) { }
						return;
					}
					ProbeCommandResult result;
					try { result = commandHandler(request.Operation, request.PayloadJson, request.ExpectedHooksVersion, commandCancellation.Token); }
					finally { ExitCommand(); }
					response = new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Response, request.CorrelationId,
						request.Operation, result.PayloadJson, result.HooksVersion);
				}
				catch (Exception ex) {
					response = new ProbeMessage(ProbeWireProtocol.ProtocolVersion, ProbeMessageKind.Response, request.CorrelationId,
						"error", "{\"error\":\"" + Escape(ex.GetType().Name) + "\",\"message\":\"" + Escape(ExceptionMessage(ex)) + "\"}", null);
				}
				lock (sendGate) ProbeWireProtocol.WriteFrame(pipe, ProbeWireProtocol.Encode(response));
			}
		}

		static string ExceptionMessage(Exception exception) {
			var parts=new List<string>();
			for(var current=exception;current!=null && parts.Count<4;current=current.InnerException) parts.Add(current.GetType().Name+": "+current.Message);
			return string.Join(" -> ",parts);
		}

		void Authenticate(Stream pipe) {
			var timedOut = 0;
			using (var deadline = new Timer(_ => { Interlocked.Exchange(ref timedOut, 1); try { pipe.Dispose(); } catch { } }, null, authenticationTimeoutMilliseconds, Timeout.Infinite)) {
				try {
					NegotiateVersion(pipe);
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

		static void NegotiateVersion(Stream pipe) {
			var clientVersion = BitConverter.ToInt32(ReadExactly(pipe, sizeof(int)), 0);
			var compatible = clientVersion == ProbeWireProtocol.ProtocolVersion;
			var versionResponse = new byte[sizeof(int) + 1]; BitConverter.GetBytes(ProbeWireProtocol.ProtocolVersion).CopyTo(versionResponse, 0); versionResponse[sizeof(int)] = compatible ? (byte)1 : (byte)0;
			pipe.Write(versionResponse, 0, versionResponse.Length); pipe.Flush();
			if (!compatible) throw new InvalidDataException("Client protocol version is incompatible.");
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

		/// <summary>Admits one command to the handler, or refuses it because shutdown has begun. The count and
		/// the gate flag are read and written under the same lock that <see cref="CloseCommandGate"/> takes, and
		/// that is the whole invariant: an admitted command has already reset <see cref="commandsIdle"/> before
		/// any close can observe it, and a command that finds the gate closed never runs at all. There is no
		/// third outcome, so there is no interval in which a quiesced answer and a running handler coexist.</summary>
		bool TryEnterCommand() {
			lock (commandGate) {
				if (commandGateClosed) return false;
				if (inFlightCommands++ == 0) commandsIdle.Reset();
				return true;
			}
		}

		void ExitCommand() { lock (commandGate) { if (--inFlightCommands == 0) commandsIdle.Set(); } }

		/// <summary>Refuses every command not already admitted. Idempotent, and deliberately one-way: both
		/// callers are shutting the endpoint down, and an endpoint that resumed admitting commands after a
		/// quiesced answer would make that answer retroactively false.</summary>
		void CloseCommandGate() { lock (commandGate) commandGateClosed = true; }

		/// <summary>Test-only seam, invoked on the listener thread after a request is decoded and before it is
		/// offered to <see cref="TryEnterCommand"/>. Internal, never set in production, and the only way to hold
		/// a listener inside the exact window this gate exists to close.</summary>
		internal Action? DispatchAdmissionProbeForTest { get; set; }

		/// <summary>Test-only seam, invoked on the disposing thread from inside <see cref="Dispose"/> after the
		/// transport is closed and before cancellation is requested. Internal, never set in production. It exists
		/// because the admission window this class has now been fixed for twice lives *inside* Dispose: a test
		/// that releases the listener from outside releases it either before Dispose begins or after it returns,
		/// and neither of those can distinguish a correct ordering from a broken one.</summary>
		internal Action? ShutdownProbeForTest { get; set; }

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
		/// It also permanently closes the dispatch gate, before observing anything. Cancelling and then looking
		/// at an idle count is not enough on its own: a listener that had already decoded a request but not yet
		/// entered the handler is work in hand that nothing counts, so the observation would answer quiesced and
		/// the handler would start its side effects afterwards. Closing the gate under the same lock the count
		/// is kept under removes that window rather than narrowing it. One-way by design - both callers are
		/// tearing the endpoint down, and this method is only ever reached on a cleanup path.
		///
		/// Safe to call before, after, or instead of <see cref="Dispose"/>, and repeatedly: cancellation and the
		/// gate close are idempotent, and the wait observes rather than mutates.</summary>
		public bool TryQuiesce(int millisecondsTimeout) {
			if (millisecondsTimeout < 0) throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
			// Same first step as Dispose, and for the same reason: the wait below is the observation, and an
			// admission that could still succeed after it would be exactly the race this closes. Both shutdown
			// entry points now close the gate before doing anything else, so there is one ordering to reason
			// about rather than two.
			CloseCommandGate();
			// Posted, not run here - see CancelCommands. The caller's bound is the wait below and nothing else;
			// it is not extended by however long a handler's cancellation callback takes.
			CancelCommands();
			return commandsIdle.WaitOne(millisecondsTimeout);
		}

		/// <summary>Requests cancellation without ever running a cancellation callback on the caller's thread,
		/// and returns as soon as the request is posted.
		///
		/// <see cref="CancellationTokenSource.Cancel()"/> runs every registered callback inline on the thread
		/// that calls it. Doing that here handed both bounds in this class to arbitrary handler code: a handler
		/// that registers a blocking cleanup callback - which is exactly what a patching command will do - makes
		/// <see cref="Dispose"/> block, and <see cref="Dispose"/> is the one method whose caller may be the
		/// target's own thread inside a func-eval with a shorter timeout than the callback; and it makes
		/// <see cref="TryQuiesce"/> overrun its caller's bound before it has even reached the wait. Neither bound
		/// can be enforced by the caller, because neither can interrupt an inline callback once it starts.
		///
		/// So the Cancel itself runs on a thread of its own, started once however many times this is called. The
		/// weakening is deliberate and small: cancellation was always a request rather than a guarantee, and the
		/// only thing that changes is that its delivery is no longer synchronous with the request. Nothing reads
		/// the token's state to decide anything - <see cref="TryQuiesce"/> answers from
		/// <see cref="commandsIdle"/>, so a late delivery makes it answer false, which is the conservative
		/// "still in flight" answer, never a false quiesced.
		///
		/// Never throws, for the same reason as before: a callback that throws must not become the caller's
		/// problem, must not stop the endpoint being closed, and - now that it runs on a thread this class owns -
		/// must not escape a thread delegate, which on .NET Framework terminates the debuggee.</summary>
		void CancelCommands() {
			if (Interlocked.Exchange(ref cancellationRequested, 1) != 0) return;
			// A dedicated thread rather than the pool: a blocking callback would otherwise occupy a pool thread
			// in the debuggee, and a saturated pool would delay the very delivery this is posting.
			try { new Thread(RunCancellation) { IsBackground = true, Name = "HookLab probe cancel" }.Start(); return; }
			catch (Exception) { }
			try { if (ThreadPool.QueueUserWorkItem(_ => RunCancellation())) return; }
			catch (Exception) { }
			// Both ways of leaving this thread failed, which on this platform means the process is already out of
			// threads or memory. Delivering the cancellation matters more than the bound in that state, and the
			// alternative is a handler that is never told to stop at all.
			RunCancellation();
		}

		void RunCancellation() { try { commandCancellation.Cancel(); } catch (Exception) { } }

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
		/// chooses.
		///
		/// What it does establish about work not yet started is exact: no command that was not already inside
		/// the handler when this began can ever start. The dispatch gate is closed as the first step, before the
		/// transport is closed and before cancellation is requested, so a listener holding a decoded request is
		/// refused whether it reaches admission before, during or after this call. Closing the gate last left it
		/// open across both of those steps, and a request decoded in that window still ran.</summary>
		public void Dispose() {
			// Disposal begins and the gate shuts in ONE lock acquisition. Adjacent statements were not enough:
			// with `disposed = true` set outside the lock, a listener that had decoded a request could be
			// admitted in the gap between the two, enter the handler, and still be mutating the target after
			// this returned. That window was two instructions wide and it was still a window - the third
			// iteration of this same ordering defect, each previous fix having narrowed it rather than closed
			// it. Making the two atomic is what actually establishes "no command not already admitted can
			// start after disposal begins", because there is no longer an interval to race.
			//
			// Still neither blocking nor unbounded: one lock acquisition, against critical sections that only
			// touch a counter and an event.
			lock (commandGate) {
				if (disposed) return;
				disposed = true;
				commandGateClosed = true;
			}
			// Wake the listener before disposing anything, on the runtimes where disposal alone does not.
			// On .NET Framework, disposing the server stream releases a thread parked in WaitForConnection
			// in 0 ms - see Listen. Mono does not: its endpoint is a synchronous pipe (an asynchronous one
			// faults in the overlapped completion callback there), and a thread blocked in a synchronous
			// WaitForConnection cannot be released by closing the handle underneath it, so the listener
			// stays parked and the process will not exit. Measured on mono-project 6.12 x64: the target hung
			// for the full retirement timeout rather than crashing, which is a quieter failure and a worse
			// one. One connect from here ends the wait, the listener observes disposed, and it returns.
			if (ListenerNeedsWakeup) WakeListener();
			NamedPipeServerStream[] current;
			lock (pipeGate) current = new List<NamedPipeServerStream>(activePipes).ToArray();
			// Disposing the server stream both closes the endpoint and releases a listener already parked in
			// WaitForConnection. Taking the gate first is what guarantees there is something here to dispose:
			// either the listener published its pipe and this sees it, or it has not reached the gate yet and
			// will observe disposed and exit without creating one.
			foreach (var pipe in current) try { pipe.Dispose(); } catch { }
			// Test-only seam, between closing the endpoint and cancelling. It is the only point from which a
			// test can release a listener *inside* Dispose and so observe what an admission racing the shutdown
			// resolves to; from outside, the release necessarily lands before Dispose starts or after it
			// returns, and both of those are already safe whatever the ordering. Null in production.
			try { ShutdownProbeForTest?.Invoke(); } catch (Exception) { }
			// Still strictly after the endpoint is closed - that relative order is unchanged, so a handler woken
			// by cancellation still cannot be handed a client connection made in between. Moving the gate close
			// ahead of both cannot re-introduce that: it only removes admissions, never creates one.
			// Cancellation is a request, not a guarantee: whether the handler actually left is what TryQuiesce
			// answers, and this method deliberately does not wait for it - the caller can be the target's own
			// thread inside a func-eval, and the bound belongs to them.
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
