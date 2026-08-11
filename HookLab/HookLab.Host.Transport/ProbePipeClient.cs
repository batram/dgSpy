using System;
using System.IO;
using System.IO.Pipes;
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
		public ProbeConnection(string pipeName, byte[] secret, byte[] endpointNonce, bool rotateCredential = false, int timeoutMilliseconds = 5000, int protocolVersion = ProbeWireProtocol.ProtocolVersion, bool authenticationEnabled = true) {
			if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
			pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
			pipe.Connect(timeoutMilliseconds);
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
		static byte[] ReadExactly(Stream stream, int count) { var value = new byte[count]; var offset = 0; while (offset < count) { var read = stream.Read(value, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } return value; }
	}
}
