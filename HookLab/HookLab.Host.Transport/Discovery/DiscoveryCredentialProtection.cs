using System;
using System.Security.Cryptography;
using System.Text;

namespace HookLab.Host.Transport.Discovery {
	public static class DiscoveryCredentialProtection {
		static readonly byte[] Entropy = new UTF8Encoding(false, true).GetBytes("dgSpy.HookLab.discovery.v1");
		public static byte[] Protect(byte[] plaintext) {
			if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
			return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
		}
		public static byte[] Unprotect(byte[] protectedBytes) {
			if (protectedBytes == null) throw new ArgumentNullException(nameof(protectedBytes));
			return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
		}
	}
}
