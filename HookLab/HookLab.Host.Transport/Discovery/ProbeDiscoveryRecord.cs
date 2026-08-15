using System;
using HookLab.Contracts;

namespace HookLab.Host.Transport.Discovery {
	public sealed class ProbeDiscoveryRecord {
		public ProbeDiscoveryRecord(TargetIdentity target, string probeInstanceId, string pipeName, byte[] endpointNonce, byte[] secret,
			int protocolVersion, DateTime expiresUtc) {
			Target = target ?? throw new ArgumentNullException(nameof(target));
			ProbeInstanceId = Required(probeInstanceId, nameof(probeInstanceId)); PipeName = Required(pipeName, nameof(pipeName));
			EndpointNonce = CloneExact(endpointNonce, 32, nameof(endpointNonce)); Secret = CloneExact(secret, 32, nameof(secret));
			if (protocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion)); ProtocolVersion = protocolVersion;
			ExpiresUtc = expiresUtc.Kind == DateTimeKind.Utc ? expiresUtc : expiresUtc.ToUniversalTime();
		}
		public TargetIdentity Target { get; }
		public string ProbeInstanceId { get; }
		public string PipeName { get; }
		public byte[] EndpointNonce { get; }
		public byte[] Secret { get; }
		public int ProtocolVersion { get; }
		public DateTime ExpiresUtc { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
		static byte[] CloneExact(byte[] value, int size, string name) { if (value == null || value.Length != size) throw new ArgumentException("Value has an invalid length.", name); return (byte[])value.Clone(); }
	}

	public interface ILiveTargetIdentity {
		bool IsCurrent(TargetIdentity identity);
	}

	/// <summary>Optional stronger classification used by strict reconciliation. False means the exact PID
	/// plus creation-time identity no longer exists, so its otherwise valid record is ordinary lifecycle
	/// garbage. True with <see cref="ILiveTargetIdentity.IsCurrent"/> false is a live identity conflict.</summary>
	public interface ILiveTargetLiveness {
		bool IsAlive(TargetIdentity identity);
	}
}
