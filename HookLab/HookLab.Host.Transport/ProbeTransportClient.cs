using System;
using HookLab.Contracts;
using HookLab.Host.Transport.Discovery;

namespace HookLab.Host.Transport {
	public sealed class ProbeHealthResult {
		public ProbeHealthResult(ProbeMessage status, byte[] credential) { Status = status ?? throw new ArgumentNullException(nameof(status)); Credential = (byte[])credential.Clone(); }
		public ProbeMessage Status { get; }
		public byte[] Credential { get; }
	}

	public static class ProbeTransportClient {
		public static ProbeMessage Send(ProbeDiscoveryRecord record, string operation, string payloadJson, long? expectedHooksVersion, int timeoutMilliseconds = 5000) {
			if (record == null) throw new ArgumentNullException(nameof(record));
			using (var connection = new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce, false, timeoutMilliseconds, record.ProtocolVersion)) {
				var response=connection.Send(new ProbeMessage(record.ProtocolVersion,ProbeMessageKind.Request,Guid.NewGuid().ToString("N"),operation,payloadJson,expectedHooksVersion));
				if(string.Equals(response.Operation,"error",StringComparison.Ordinal)) throw new InvalidOperationException("Resident operation failed: "+response.PayloadJson);
				if(!string.Equals(response.Operation,operation,StringComparison.Ordinal)) throw new InvalidOperationException("Resident response operation does not match the request.");
				return response;
			}
		}
		/// <summary>T09 calls this after discovery/bootstrap to prove endpoint ownership and obtain bounded probe status.</summary>
		public static ProbeHealthResult VerifyHealth(ProbeDiscoveryRecord record, bool recovery, int timeoutMilliseconds = 5000) {
			if (record == null) throw new ArgumentNullException(nameof(record));
			using (var connection = new ProbeConnection(record.PipeName, record.Secret, record.EndpointNonce, recovery, timeoutMilliseconds, record.ProtocolVersion)) {
				var request = new ProbeMessage(record.ProtocolVersion, ProbeMessageKind.Request, Guid.NewGuid().ToString("N"), "status", "{}");
				var response = connection.Send(request);
				if (!string.Equals(response.Operation, "status", StringComparison.Ordinal)) throw new InvalidOperationException("Probe health request failed.");
				return new ProbeHealthResult(response, connection.Credential);
			}
		}
	}
}
