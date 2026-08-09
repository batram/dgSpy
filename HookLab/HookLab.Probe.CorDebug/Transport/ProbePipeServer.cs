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

	public delegate ProbeCommandResult ProbeCommandHandler(string operation, string payloadJson, long? expectedHooksVersion);

	public sealed class ProbePipeServer : IDisposable, IHookEventConsumer {
		readonly object secretGate = new object();
		readonly object sendGate = new object();
		readonly ProbeCommandHandler commandHandler;
		readonly byte[] endpointNonce;
		readonly string pipeName;
		readonly Thread listener;
		readonly ManualResetEvent stopped = new ManualResetEvent(false);
		volatile bool disposed;
		int endpointTaken;
		byte[] secret;
		NamedPipeServerStream? activePipe;
		Stream? authenticatedPipe;

		public ProbePipeServer(ProbeCommandHandler commandHandler) {
			this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
			secret = ProbeAuthentication.CreateSecret(); endpointNonce = ProbeAuthentication.CreateNonce();
			pipeName = "dgspy-hooklab-" + Guid.NewGuid().ToString("N");
			listener = new Thread(Listen) { IsBackground = true, Name = "HookLab probe pipe" }; listener.Start();
		}

		public ProbeEndpoint TakeInitialEndpoint() {
			if (Interlocked.Exchange(ref endpointTaken, 1) != 0) throw new InvalidOperationException("The initial probe credential has already been returned.");
			lock (secretGate) return new ProbeEndpoint(pipeName, secret, endpointNonce);
		}

		void Listen() {
			try {
				while (!disposed) {
					using (var pipe = CreatePipe()) {
						activePipe = pipe;
						try { pipe.WaitForConnection(); if (!disposed) Serve(pipe); }
						catch (Exception) when (disposed) { }
						catch (IOException) { }
						catch (UnauthorizedAccessException) { }
						catch (InvalidDataException) { }
						finally { authenticatedPipe = null; activePipe = null; }
					}
				}
			}
			finally { stopped.Set(); }
		}

		NamedPipeServerStream CreatePipe() {
			var identity = WindowsIdentity.GetCurrent();
			var sid = identity.User ?? throw new InvalidOperationException("The probe process has no Windows user SID.");
			var security = new PipeSecurity();
			security.SetAccessRuleProtection(true, false);
			security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
			return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
				PipeOptions.None, 4096, 4096, security, HandleInheritability.None);
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
					var result = commandHandler(request.Operation, request.PayloadJson, request.ExpectedHooksVersion);
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
			if (!ProbeAuthentication.FixedTimeEquals(supplied, expected)) throw new UnauthorizedAccessException("Probe authentication failed.");
			var serverProof = ProbeAuthentication.ServerProof(current, serverChallenge, clientChallenge, endpointNonce);
			pipe.Write(serverProof, 0, serverProof.Length); pipe.Flush();
			if (request[0] == 1) lock (secretGate) { var rotated = ProbeAuthentication.DeriveRotatedSecret(current, serverChallenge, clientChallenge); Array.Clear(secret, 0, secret.Length); secret = rotated; }
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

		public void Dispose() {
			if (disposed) return; disposed = true;
			try { activePipe?.Dispose(); } catch { }
			stopped.WaitOne(TimeSpan.FromSeconds(2)); stopped.Dispose();
		}

		static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
		static byte[] ReadExactly(Stream stream, int count) { var value = new byte[count]; var offset = 0; while (offset < count) { var read = stream.Read(value, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } return value; }
	}
}
