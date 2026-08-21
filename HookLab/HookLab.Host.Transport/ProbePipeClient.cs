using System;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using HookLab.Contracts;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Host.Transport {
	public sealed class ProbeProtocolMismatchException : IOException {
		public ProbeProtocolMismatchException(int requestedVersion, int supportedVersion) : base($"Probe protocol {requestedVersion} is incompatible; endpoint supports {supportedVersion}.") { RequestedVersion = requestedVersion; SupportedVersion = supportedVersion; }
		public int RequestedVersion { get; }
		public int SupportedVersion { get; }
	}

	public sealed class ProbeConnection : IDisposable {
		readonly NamedPipeClientStream pipe;
		/// <summary>The budget for each handshake read, in milliseconds. The same value the caller gave
		/// for <c>Connect</c>: connecting to a resident that cannot answer and completing a handshake with
		/// one are the same wait as far as a caller is concerned, and only one of them used to be bounded.</summary>
		readonly int handshakeTimeoutMilliseconds = 5000;
		public ProbeConnection(string pipeName, byte[] secret, byte[] endpointNonce, bool rotateCredential = false, int timeoutMilliseconds = 5000, int protocolVersion = ProbeWireProtocol.ProtocolVersion, bool authenticationEnabled = true) {
			if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
			pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
			pipe.Connect(timeoutMilliseconds);
			handshakeTimeoutMilliseconds = timeoutMilliseconds;
			var requested = BitConverter.GetBytes(protocolVersion); pipe.Write(requested, 0, requested.Length); pipe.Flush();
			var compatibility = ReadExactly(pipe, sizeof(int) + 1); var supported = BitConverter.ToInt32(compatibility, 0);
			if (compatibility[sizeof(int)] != 1 || supported != protocolVersion) { pipe.Dispose(); throw new ProbeProtocolMismatchException(protocolVersion, supported); }
			if (!authenticationEnabled) { Credential = Array.Empty<byte>(); return; }
			var serverChallenge = ReadExactly(pipe, ProbeAuthentication.NonceBytes);
			var clientChallenge = ProbeAuthentication.CreateNonce();
			var proof = ProbeAuthentication.ClientProof(secret, serverChallenge, clientChallenge, endpointNonce);
			var request = new byte[1 + clientChallenge.Length + proof.Length]; request[0] = rotateCredential ? (byte)1 : (byte)0;
			Buffer.BlockCopy(clientChallenge, 0, request, 1, clientChallenge.Length); Buffer.BlockCopy(proof, 0, request, 1 + clientChallenge.Length, proof.Length);
			pipe.Write(request, 0, request.Length); pipe.Flush();
			var serverProof = ReadExactly(pipe, 32);
			var expected = ProbeAuthentication.ServerProof(secret, serverChallenge, clientChallenge, endpointNonce);
			if (!ProbeAuthentication.FixedTimeEquals(serverProof, expected)) { pipe.Dispose(); throw new UnauthorizedAccessException("The pipe endpoint did not prove the probe credential."); }
			Credential = rotateCredential ? ProbeAuthentication.DeriveRotatedSecret(secret, serverChallenge, clientChallenge) : (byte[])secret.Clone();
		}

		public byte[] Credential { get; }
		public event Action<ProbeMessage>? EventReceived;
		public ProbeMessage Send(ProbeMessage request) {
			if (request == null) throw new ArgumentNullException(nameof(request));
			ProbeWireProtocol.WriteFrame(pipe, ProbeWireProtocol.Encode(request));
			ProbeMessage response;
			do { response = ProbeWireProtocol.Decode(ProbeWireProtocol.ReadFrame(pipe)); if (response.Kind == ProbeMessageKind.Event) EventReceived?.Invoke(response); }
			while (response.Kind == ProbeMessageKind.Event);
			if (response.Kind != ProbeMessageKind.Response || !string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
				throw new InvalidDataException("Probe response does not match the request.");
			return response;
		}
		public void Dispose() => pipe.Dispose();

		/// <summary>Reads exactly <paramref name="count"/> bytes of the handshake, or gives up.
		///
		/// <para>The bound is the point. A <c>PipeStream</c> cannot be given a read timeout, so this read
		/// blocks for as long as the resident takes to answer - and a resident that is not running does
		/// not answer at all. That is not hypothetical: on a live Unity player whose own breakpoint was
		/// still armed, arrival resumed the target, the target stopped again at that breakpoint, and this
		/// handshake blocked until the whole <c>initialize_hooklab</c> call died on its 130-second
		/// deadline - reported as <c>deadline_exceeded</c>, with nothing to say which read had hung.</para>
		///
		/// <para>The read runs on its own thread and the pipe is disposed on timeout, which releases it
		/// rather than leaking it. Disposing is safe because a handshake that timed out has no usable
		/// connection left to protect.</para></summary>
		byte[] ReadExactly(Stream stream, int count) {
			var read = Task.Run(() => ReadExactlyBlocking(stream, count));
			if (!read.Wait(handshakeTimeoutMilliseconds)) {
				pipe.Dispose();
				throw new TimeoutException("The probe endpoint did not complete its handshake within " +
					handshakeTimeoutMilliseconds + " ms. The target is most likely stopped: a resident can only answer while it is running.");
			}
			return read.GetAwaiter().GetResult();
		}

		static byte[] ReadExactlyBlocking(Stream stream, int count) { var value = new byte[count]; var offset = 0; while (offset < count) { var read = stream.Read(value, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } return value; }
	}
}
