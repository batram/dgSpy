using System;
using System.IO;
using System.Security.Cryptography;

namespace HookLab.Probe.CorDebug.Transport {
	public static class ProbeAuthentication {
		public const int SecretBytes = 32;
		public const int NonceBytes = 32;
		static readonly byte[] ClientLabel = { 0x63, 0x6c, 0x69, 0x65, 0x6e, 0x74 };
		static readonly byte[] ServerLabel = { 0x73, 0x65, 0x72, 0x76, 0x65, 0x72 };

		public static byte[] CreateSecret() { var value = new byte[SecretBytes]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
		public static byte[] CreateNonce() { var value = new byte[NonceBytes]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
		public static byte[] ClientProof(byte[] secret, byte[] serverChallenge, byte[] clientChallenge, byte[] endpointNonce) => Proof(secret, ClientLabel, serverChallenge, clientChallenge, endpointNonce);
		public static byte[] ServerProof(byte[] secret, byte[] serverChallenge, byte[] clientChallenge, byte[] endpointNonce) => Proof(secret, ServerLabel, serverChallenge, clientChallenge, endpointNonce);
		public static byte[] DeriveRotatedSecret(byte[] current, byte[] serverChallenge, byte[] clientChallenge) {
			using (var hmac = new HMACSHA256(current)) {
				var input = new byte[7 + serverChallenge.Length + clientChallenge.Length];
				Buffer.BlockCopy(new byte[] { 0x72, 0x6f, 0x74, 0x61, 0x74, 0x65, 0x31 }, 0, input, 0, 7);
				Buffer.BlockCopy(serverChallenge, 0, input, 7, serverChallenge.Length);
				Buffer.BlockCopy(clientChallenge, 0, input, 7 + serverChallenge.Length, clientChallenge.Length);
				return hmac.ComputeHash(input);
			}
		}

		public static bool FixedTimeEquals(byte[] left, byte[] right) {
			if (left == null || right == null || left.Length != right.Length) return false;
			var difference = 0; for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index]; return difference == 0;
		}

		static byte[] Proof(byte[] secret, byte[] label, byte[] serverChallenge, byte[] clientChallenge, byte[] endpointNonce) {
			if (secret == null || secret.Length != SecretBytes) throw new InvalidDataException("Probe secret length is invalid.");
			if (serverChallenge == null || serverChallenge.Length != NonceBytes || clientChallenge == null || clientChallenge.Length != NonceBytes || endpointNonce == null || endpointNonce.Length != NonceBytes)
				throw new InvalidDataException("Probe authentication nonce length is invalid.");
			using (var hmac = new HMACSHA256(secret)) {
				var input = new byte[label.Length + serverChallenge.Length + clientChallenge.Length + endpointNonce.Length];
				Buffer.BlockCopy(label, 0, input, 0, label.Length);
				Buffer.BlockCopy(serverChallenge, 0, input, label.Length, serverChallenge.Length);
				Buffer.BlockCopy(clientChallenge, 0, input, label.Length + serverChallenge.Length, clientChallenge.Length);
				Buffer.BlockCopy(endpointNonce, 0, input, label.Length + serverChallenge.Length + clientChallenge.Length, endpointNonce.Length);
				return hmac.ComputeHash(input);
			}
		}
	}
}
