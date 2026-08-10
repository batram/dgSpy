using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using HookLab.Contracts;

namespace HookLab.Host.Transport.Discovery {
	public sealed class ProbeDiscoveryStore {
		const int FormatVersion = 1;
		const int MaximumRecordBytes = 64 * 1024;
		/// <summary>How far ahead a write or a refresh sets <c>ExpiresUtc</c>. It is a *refresh deadline*,
		/// not a validity deadline - see <see cref="Discover"/>.</summary>
		public static readonly TimeSpan DiscoveryRecordLifetime = TimeSpan.FromMinutes(5);
		static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
		readonly string directory;
		readonly string quarantineDirectory;
		readonly string stateRoot;

		public ProbeDiscoveryStore(string? stateRoot = null) {
			this.stateRoot = DgSpyStateRoot.Resolve(stateRoot); directory = DgSpyStateRoot.HookLabDiscovery(this.stateRoot);
			quarantineDirectory = Path.Combine(Path.GetDirectoryName(directory)!, "quarantine");
		}

		public string DirectoryPath => directory;
		public string Write(ProbeDiscoveryRecord record) {
			if (record == null) throw new ArgumentNullException(nameof(record)); EnsureSecureDirectory(directory);
			var name = FileName(record); var path = SafeChild(directory, name); var temporary = SafeChild(directory, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
			var protectedBytes = DiscoveryCredentialProtection.Protect(Serialize(record));
			if (protectedBytes.Length > MaximumRecordBytes) throw new InvalidDataException("Protected discovery record exceeds its limit.");
			try {
				using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(protectedBytes, 0, protectedBytes.Length); stream.Flush(); }
				SecureFile(temporary);
				if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
				SecureFile(path); return path;
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}

		/// <summary>Every valid record for a live target.
		///
		/// <para><b>Expiry is a refresh deadline, not a validity deadline</b>, and it used to be the latter.
		/// Nothing refreshed a record, so a probe alive longer than <see cref="DiscoveryRecordLifetime"/>
		/// reported as "nothing installed" - which is exactly backwards for the mechanism T09's D4 rests on
		/// for idempotency and for reconciliation after a host restart, and invites a second, permanently
		/// unloadable payload generation into a target that already has one. Two things now hold instead:
		/// <see cref="VerifyHealthAndRefresh"/> extends the deadline whenever an authenticated health check
		/// succeeds, and a record past its deadline is still honoured while its target is provably the same
		/// live process.</para>
		///
		/// <para>Nothing about the record's protection changes, and the check that decides validity is the
		/// stronger of the two: <see cref="ILiveTargetIdentity.IsCurrent"/> compares image path, process id,
		/// process creation time, architecture, runtime id and AppDomain id against the live process, so a
		/// record can outlive neither its process nor a pid reuse. The clock never established any of that.
		/// DPAPI CurrentUser protection, the inheritance-broken single-SID ACL on the directory and every
		/// file, reparse rejection on the directory and the file, the escape-proof child path, the
		/// name-matches-identity check and the protocol-version check are all untouched and still run on
		/// every record read here and written by every refresh.</para>
		///
		/// <para>What the deadline still does: a record whose target is gone <b>and</b> whose deadline has
		/// passed is deleted rather than quarantined. Quarantine is for records that fail an integrity
		/// check; a record that simply outlived its process is our own garbage, and archiving it kept a
		/// DPAPI-protected secret on disk indefinitely - strictly longer than the lifetime that was
		/// supposed to bound it.</para></summary>
		public IReadOnlyList<ProbeDiscoveryRecord> Discover(ILiveTargetIdentity liveTargets, DateTime nowUtc) {
			if (liveTargets == null) throw new ArgumentNullException(nameof(liveTargets));
			if (!Directory.Exists(directory)) return Array.Empty<ProbeDiscoveryRecord>();
			RejectReparsePoint(directory); var result = new List<ProbeDiscoveryRecord>();
			foreach (var path in Directory.GetFiles(directory, "*.probe", SearchOption.TopDirectoryOnly)) {
				try {
					RejectReparsePoint(path); var bytes = File.ReadAllBytes(path);
					if (bytes.Length == 0 || bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Discovery record length is invalid.");
					var record = Deserialize(DiscoveryCredentialProtection.Unprotect(bytes));
					if (!string.Equals(Path.GetFileName(path), FileName(record), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Discovery record name does not match its identity.");
					if (record.ProtocolVersion != HookLab.Probe.CorDebug.Transport.ProbeWireProtocol.ProtocolVersion) throw new InvalidDataException("Discovery protocol version is incompatible.");
					if (!liveTargets.IsCurrent(record.Target)) {
						if (record.ExpiresUtc <= nowUtc.ToUniversalTime()) { File.Delete(path); continue; }
						throw new InvalidDataException("Discovery target identity is stale.");
					}
					result.Add(record);
				}
				catch (Exception ex) when (ex is InvalidDataException || ex is CryptographicException || ex is UnauthorizedAccessException || ex is IOException) { Quarantine(path); }
			}
			return result.AsReadOnly();
		}

		public string Rotate(ProbeDiscoveryRecord previous, byte[] rotatedSecret) => Replace(previous, rotatedSecret);

		/// <summary>Extends the refresh deadline of an existing record, keeping its credential.
		///
		/// The write is the same write as any other - DPAPI CurrentUser, the single-SID ACL re-applied to the
		/// temporary file and the replacement, the escape-proof child path, the size bound - so refreshing
		/// cannot produce a record a fresh write could not.</summary>
		public string Refresh(ProbeDiscoveryRecord record) {
			if (record == null) throw new ArgumentNullException(nameof(record));
			return Replace(record, record.Secret);
		}

		/// <summary>The health check T09's idempotency short-circuit runs before it considers installing
		/// anything: it proves the endpoint answers under the record's own credential, and only then extends
		/// the record's refresh deadline. A refresh is therefore never a bare timer - the record survives
		/// exactly as long as the probe keeps proving it is there.
		///
		/// A failed check is left to the caller rather than quarantined here: D4 uses it to decide that a
		/// fresh install is needed, and destroying the record on a transient IO failure would strand a probe
		/// that is still running with no record naming it. <see cref="RecoverAndRotate"/> keeps its own
		/// quarantine-on-failure behaviour, because a recovery that fails has already spent the credential.</summary>
		public ProbeHealthResult VerifyHealthAndRefresh(ProbeDiscoveryRecord record, int timeoutMilliseconds = 5000) {
			if (record == null) throw new ArgumentNullException(nameof(record));
			var health = ProbeTransportClient.VerifyHealth(record, false, timeoutMilliseconds);
			Refresh(record);
			return health;
		}

		string Replace(ProbeDiscoveryRecord previous, byte[] secret) =>
			Write(new ProbeDiscoveryRecord(previous.Target, previous.ProbeInstanceId, previous.PipeName, previous.EndpointNonce,
				secret, previous.ProtocolVersion, DateTime.UtcNow.Add(DiscoveryRecordLifetime)));

		public ProbeHealthResult RecoverAndRotate(ProbeDiscoveryRecord record, int timeoutMilliseconds = 5000) {
			try { var health = ProbeTransportClient.VerifyHealth(record, true, timeoutMilliseconds); Rotate(record, health.Credential); return health; }
			catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is ProbeProtocolMismatchException) { var path = SafeChild(directory, FileName(record)); if (File.Exists(path)) Quarantine(path); throw; }
		}

		public void Delete(ProbeDiscoveryRecord record) { var path = SafeChild(directory, FileName(record)); if (File.Exists(path)) File.Delete(path); }

		static byte[] Serialize(ProbeDiscoveryRecord record) {
			using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, StrictUtf8, true)) {
				writer.Write(FormatVersion); Write(writer, record.Target.HostId, 1024); Write(writer, CanonicalImage(record.Target.ImagePath), 32768);
				writer.Write(record.Target.ProcessId); writer.Write(record.Target.ProcessCreationTimeUtc.ToUniversalTime().Ticks);
				Write(writer, record.Target.Architecture, 64); Write(writer, record.Target.RuntimeId, 1024); Write(writer, record.Target.AppDomainId, 1024);
				Write(writer, record.ProbeInstanceId, 1024); Write(writer, record.PipeName, 1024); writer.Write(record.EndpointNonce); writer.Write(record.Secret);
				writer.Write(record.ProtocolVersion); writer.Write(record.ExpiresUtc.Ticks); writer.Flush(); return stream.ToArray();
			}
		}

		static ProbeDiscoveryRecord Deserialize(byte[] bytes) {
			if (bytes.Length == 0 || bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Discovery record length is invalid.");
			try {
				using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, StrictUtf8, true)) {
					if (reader.ReadInt32() != FormatVersion) throw new InvalidDataException("Discovery record format is unsupported.");
					var target = new TargetIdentity(Read(reader, 1024), CanonicalImage(Read(reader, 32768)), reader.ReadInt32(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
						Read(reader, 64), Read(reader, 1024), Read(reader, 1024));
					var record = new ProbeDiscoveryRecord(target, Read(reader, 1024), Read(reader, 1024), ReadExact(reader, 32), ReadExact(reader, 32), reader.ReadInt32(), new DateTime(reader.ReadInt64(), DateTimeKind.Utc));
					if (stream.Position != stream.Length) throw new InvalidDataException("Discovery record contains trailing bytes."); return record;
				}
			}
			catch (InvalidDataException) { throw; }
			catch (Exception ex) when (ex is EndOfStreamException || ex is DecoderFallbackException || ex is ArgumentException) { throw new InvalidDataException("Discovery record is malformed.", ex); }
		}

		static string FileName(ProbeDiscoveryRecord record) {
			var key = record.Target.HostId + "\0" + CanonicalImage(record.Target.ImagePath) + "\0" + record.Target.ProcessId + "\0" + record.Target.ProcessCreationTimeUtc.ToUniversalTime().Ticks + "\0" +
				record.Target.Architecture + "\0" + record.Target.RuntimeId + "\0" + record.Target.AppDomainId + "\0" + Convert.ToBase64String(record.EndpointNonce);
			using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(StrictUtf8.GetBytes(key)).Select(value => value.ToString("x2"))) + ".probe";
		}

		static string CanonicalImage(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		static string SafeChild(string root, string name) { var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var path = Path.GetFullPath(Path.Combine(root, name)); if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Discovery path escapes its root."); return path; }
		static void RejectReparsePoint(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Discovery paths may not be reparse points."); }
		void EnsureSecureDirectory(string path) { Directory.CreateDirectory(path); RejectReparseParents(path); var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The host has no Windows user SID."); var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); SetDirectorySecurity(path, security); }
		static void SecureFile(string path) { var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The host has no Windows user SID."); var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow)); SetFileSecurity(path, security); }
		static void SetDirectorySecurity(string path, DirectorySecurity security) {
#if NETFRAMEWORK
			Directory.SetAccessControl(path, security);
#else
			new DirectoryInfo(path).SetAccessControl(security);
#endif
		}
		static void SetFileSecurity(string path, FileSecurity security) {
#if NETFRAMEWORK
			File.SetAccessControl(path, security);
#else
			new FileInfo(path).SetAccessControl(security);
#endif
		}
		void Quarantine(string path) { EnsureSecureDirectory(quarantineDirectory); var destination = SafeChild(quarantineDirectory, Path.GetFileName(path) + "." + DateTime.UtcNow.Ticks + ".quarantine"); File.Move(path, destination); SecureFile(destination); }
		void RejectReparseParents(string path) { var root = Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar); var current = new DirectoryInfo(Path.GetFullPath(path)); while (current != null) { RejectReparsePoint(current.FullName); if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) return; current = current.Parent; } throw new InvalidDataException("Discovery path is outside the state root."); }
		static void Write(BinaryWriter writer, string value, int maximum) { var bytes = StrictUtf8.GetBytes(value); if (bytes.Length == 0 || bytes.Length > maximum) throw new InvalidDataException("Discovery string length is invalid."); writer.Write(bytes.Length); writer.Write(bytes); }
		static string Read(BinaryReader reader, int maximum) { var length = reader.ReadInt32(); if (length <= 0 || length > maximum) throw new InvalidDataException("Discovery string length is invalid."); return StrictUtf8.GetString(ReadExact(reader, length)); }
		static byte[] ReadExact(BinaryReader reader, int length) { var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes; }
	}
}
