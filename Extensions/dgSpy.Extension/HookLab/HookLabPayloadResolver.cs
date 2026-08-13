using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace dgSpy.Extension.PayloadDelivery {
	/// <summary>Which completed DgSpyTool host layout the payload was resolved from. Detected from what
	/// is present in and above the host root, never from a caller-supplied hint: a hint is exactly the thing
	/// a damaged or substituted install would get wrong.</summary>
	enum HookLabPayloadLayout {
		/// <summary>A packaged or directly installed completed layout.</summary>
		PackedInstall,
		/// <summary>A Gateway-deployed completed layout.</summary>
		GatewayDeployment,
	}

	/// <summary>Whether the second, independent digest record was actually consulted.</summary>
	enum HookLabPayloadCrossCheck {
		/// <summary>The independent record was read and agreed with the payload manifest.</summary>
		Verified,
	}

	/// <summary>Every distinguishable reason the payload can be refused. One member per refusal so a caller
	/// - and a test - can tell them apart without matching on message text.</summary>
	enum HookLabPayloadRefusal {
		PayloadMissing,
		PayloadUnreadable,
		PayloadManifestMissing,
		PayloadManifestUnreadable,
		PayloadManifestUnparseable,
		PayloadManifestInvalid,
		PayloadTruncated,
		PayloadDigestMismatch,
		/// <summary>The layout has an independent record and it is absent. Never collapsed into
		/// <see cref="PackageRecordUnreadable"/> and never treated as "no cross-check needed".</summary>
		PackageRecordMissing,
		/// <summary>The independent record exists and could not be read or parsed. Deliberately distinct
		/// from absent: the Gateway learned in 83ddc2452 that collapsing the two turns the cross-check off
		/// for exactly the install most likely to be damaged.</summary>
		PackageRecordUnreadable,
		/// <summary>The payload manifest and the independent record disagree with each other. This is the
		/// property P01 designed the second record for: a payload rewritten together with its own manifest
		/// agrees with itself and disagrees with this.</summary>
		RecordsDisagree,
		ReparsePoint,
		PathEscapesHostRoot,
	}

	sealed class HookLabPayloadRefusedException : Exception {
		public HookLabPayloadRefusal Refusal { get; }
		public HookLabPayloadRefusedException(HookLabPayloadRefusal refusal, string message) : base(message) => Refusal = refusal;
	}

	/// <summary>An open, verified payload handle. The handle is the product, not the path: the caller holds
	/// it across the whole evaluation so that a same-user process cannot delete, rename, replace or
	/// write-open the file underneath the target while the bytes are being delivered.
	///
	/// Dispose closes the handle and nothing else. Nothing is staged and nothing is deleted - the payload is
	/// shipped product and removing it would break the next action and rollback.</summary>
	sealed class ResolvedHookLabPayload : IDisposable {
		public FileStream Handle { get; }
		public string HostRoot { get; }
		public string PayloadPath { get; }
		public string PayloadManifestPath { get; }
		public string Sha256 { get; }
		public long Length { get; }
		public HookLabPayloadLayout Layout { get; }
		public HookLabPayloadCrossCheck CrossCheck { get; }
		/// <summary>Where the independent record was read from, or null in a developer worktree.</summary>
		public string? IndependentRecordPath { get; }
		/// <summary>The digest the independent record carries, or null in a developer worktree.</summary>
		public string? IndependentRecordSha256 { get; }

		internal ResolvedHookLabPayload(FileStream handle, string hostRoot, string payloadPath, string payloadManifestPath, string sha256, long length,
			HookLabPayloadLayout layout, HookLabPayloadCrossCheck crossCheck, string? independentRecordPath, string? independentRecordSha256) {
			Handle = handle; HostRoot = hostRoot; PayloadPath = payloadPath; PayloadManifestPath = payloadManifestPath;
			Sha256 = sha256; Length = length; Layout = layout; CrossCheck = crossCheck;
			IndependentRecordPath = independentRecordPath; IndependentRecordSha256 = independentRecordSha256;
		}

		/// <summary>Re-hashes the payload from the still-open handle. D7 asks for both the before and the
		/// after digest in evidence; this is the "after". It reads the same handle rather than reopening the
		/// path, so it measures the bytes that were actually delivered.</summary>
		public string RehashFromHandle() => HookLabPayloadResolver.HashFromHandle(Handle);

		public void Dispose() => Handle.Dispose();
	}

	/// <summary>Resolves and verifies the single HookLab payload file that ships inside the running host's
	/// own tree.
	///
	/// <para><b>Layout authority.</b> The directory and manifest shape are emitted and hashed by
	/// <c>DgSpyTool</c>; the resolver accepts only that completed layout.</para>
	///
	/// <para><b>Resolution is relative to the running host, never to the Gateway's <c>active_version</c>.</b>
	/// An adopted host can be running a different version than the active one, so resolving by active
	/// version can hand the target a payload from a tree the host is not running.</para>
	///
	/// <para><b>Verification is from the handle, not from the path.</b> The file is opened
	/// <see cref="FileShare.Read"/> first and every digest is computed from that open stream. Adoption never
	/// hashes the bytes currently in the tree - a rebuild in place or a post-deployment edit is invisible to
	/// it - so nothing upstream is trusted to have established payload identity.</para>
	///
	/// <para><b>Threat model.</b> This defends against corruption, against a stale or mismatched deployment,
	/// and against a non-cooperating same-user process racing the read: measured on this tree, a handle held
	/// with <c>FileShare.Read</c> denies another process <c>File.Delete</c>, <c>File.Replace</c> (with and
	/// without a backup), <c>File.Move</c> and any write-open, while leaving read-open working. It does
	/// <b>not</b> defend against a same-user attacker who can write to the install root before the action
	/// starts, nor against one who can inject into the target. That is the same posture the plan already
	/// declares for the discovery store. The target also cannot re-verify the outer digest itself: a
	/// byte-loaded assembly has no <c>Location</c> and no access to its own raw bytes, and passing the bytes
	/// back to hash them defeats the purpose.</para></summary>
	static class HookLabPayloadResolver {
		public const string PayloadDirectoryName = "hooklab";
		public const string PayloadFileName = "hooklab-bootstrap.net48.payload";
		public const string PayloadManifestFileName = "hooklab-payload-manifest.json";
		public const string PayloadEntryId = "hooklab_bootstrap";
		public const string DeploymentManifestFileName = "deployment-manifest.json";
		public const string LayoutManifestFileName = "dgspy-layout.json";
		const int HostRootSearchDepth = 4;

		/// <summary>Opens the payload of the host this code is running in.</summary>
		public static ResolvedHookLabPayload Open() => OpenFrom(ResolveHostRoot(AppDomain.CurrentDomain.BaseDirectory));

		/// <summary>The host root for a given app-base directory. The packaged net48 layout puts the app base
		/// at the host root while the self-contained net10 layout puts it under <c>bin\</c>, so this walks up
		/// a bounded number of levels looking for the <c>hooklab</c> directory rather than hardcoding either
		/// depth. When nothing is found the base directory is returned unchanged, so the refusal is a
		/// truthful "no payload at &lt;expected path&gt;" rather than a guess about the layout.
		///
		/// <para>An ancestor is accepted only on <b>positive evidence</b> - the payload file and its manifest
		/// both present in that ancestor's <c>hooklab</c> directory. Accepting the first ancestor that merely
		/// <i>contains a directory named hooklab</i> let a nested tree, a partial copy, or a host started from
		/// an unusual working directory mask the real host root: resolution stopped at the decoy and every
		/// later check - including the independent cross-check, which is keyed off the resolved root - then
		/// described the wrong tree. Anything short of that evidence is walked past, not stopped at.</para></summary>
		public static string ResolveHostRoot(string baseDirectory) {
			if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
			var start = Normalize(baseDirectory);
			var current = start;
			for (var level = 0; level < HostRootSearchDepth && current is not null; level++) {
				if (CarriesPayload(current)) return current;
				current = Parent(current);
			}
			return start;
		}

		/// <summary>Whether a candidate ancestor actually holds a payload, rather than merely a directory with
		/// the right name. Both files are required: a <c>hooklab</c> directory carrying one without the other
		/// is a partial copy, and stopping there would hide a complete host root further up.</summary>
		static bool CarriesPayload(string candidate) {
			var directory = Path.Combine(candidate, PayloadDirectoryName);
			return File.Exists(Path.Combine(directory, PayloadFileName))
				&& File.Exists(Path.Combine(directory, PayloadManifestFileName));
		}

		/// <summary>Resolves, verifies and hands back an open handle for an explicit host root.</summary>
		public static ResolvedHookLabPayload OpenFrom(string hostRoot) {
			if (string.IsNullOrWhiteSpace(hostRoot)) throw new ArgumentException("A host root is required.", nameof(hostRoot));
			var root = Normalize(hostRoot);
			var payloadDirectory = SafeChild(root, PayloadDirectoryName);
			var payloadPath = SafeChild(payloadDirectory, PayloadFileName);
			var manifestPath = SafeChild(payloadDirectory, PayloadManifestFileName);

			if (!File.Exists(payloadPath))
				throw Refuse(HookLabPayloadRefusal.PayloadMissing, $"the host layout carries no HookLab payload at '{payloadPath}', so there is nothing to deliver");
			// Canonical-path and reparse-point protections on the read path: the file and every parent up to
			// and including the host root.
			RejectReparsePoint(payloadPath);
			RejectReparseParents(payloadPath, root);

			FileStream handle;
			try { handle = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{ throw Refuse(HookLabPayloadRefusal.PayloadUnreadable, $"'{payloadPath}' could not be opened for reading ({ex.GetType().Name}: {ex.Message})"); }

			try {
				var actualSha = HashFromHandle(handle);
				var actualLength = handle.Length;

				if (!File.Exists(manifestPath))
					throw Refuse(HookLabPayloadRefusal.PayloadManifestMissing, $"the payload at '{payloadPath}' has no manifest at '{manifestPath}', so its digest cannot be checked");
				RejectReparsePoint(manifestPath);

				string manifestText;
				try { manifestText = File.ReadAllText(manifestPath); }
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
					{ throw Refuse(HookLabPayloadRefusal.PayloadManifestUnreadable, $"'{manifestPath}' exists but could not be read ({ex.GetType().Name}: {ex.Message})"); }

				var (recordedSha, recordedSize, recordedFile) = ReadPayloadManifest(manifestText, manifestPath);

				// The manifest-declared file name is checked for escape before it is compared, so a manifest
				// naming '..\..\anything' is a path refusal rather than a name mismatch.
				var declared = SafeChild(payloadDirectory, recordedFile);
				if (!string.Equals(Path.GetFileName(declared), PayloadFileName, StringComparison.OrdinalIgnoreCase))
					throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' names payload file '{recordedFile}'; this layout ships '{PayloadFileName}'");

				if (actualLength != recordedSize)
					throw Refuse(HookLabPayloadRefusal.PayloadTruncated, $"'{payloadPath}' is {actualLength.ToString(CultureInfo.InvariantCulture)} bytes and its manifest records {recordedSize.ToString(CultureInfo.InvariantCulture)}, so it is truncated, empty or was modified");
				if (!string.Equals(actualSha, recordedSha, StringComparison.OrdinalIgnoreCase))
					throw Refuse(HookLabPayloadRefusal.PayloadDigestMismatch, $"'{payloadPath}' hashes to {actualSha} and its manifest records {recordedSha}, so the bytes are not the ones that were packaged");

				var record = ReadIndependentRecord(root);
				if (record.CrossCheck == HookLabPayloadCrossCheck.Verified) {
					// Compared against the payload manifest rather than against the bytes, because the bytes
					// already agree with the payload manifest by the time we get here. Rewriting the payload
					// and its own manifest together is precisely the case this catches.
					if (!string.Equals(recordedSha, record.Sha, StringComparison.OrdinalIgnoreCase))
						throw Refuse(HookLabPayloadRefusal.RecordsDisagree, $"'{manifestPath}' records {recordedSha} while '{record.Path}' records {record.Sha}, so the payload and its own manifest were replaced together or come from another package");
				}

				return new ResolvedHookLabPayload(handle, root, payloadPath, manifestPath, actualSha, actualLength, record.Layout, record.CrossCheck, record.Path, record.Sha);
			}
			catch { handle.Dispose(); throw; }
		}

		static (string Sha, long Size, string File) ReadPayloadManifest(string text, string manifestPath) {
			JsonDocument document;
			try { document = JsonDocument.Parse(text); }
			catch (JsonException ex) { throw Refuse(HookLabPayloadRefusal.PayloadManifestUnparseable, $"'{manifestPath}' is not valid JSON ({ex.Message})"); }
			using (document) {
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payloads", out var payloads) || payloads.ValueKind != JsonValueKind.Array)
					throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' has no payloads array");
				JsonElement entry = default; var matches = 0;
				foreach (var candidate in payloads.EnumerateArray()) {
					if (candidate.ValueKind != JsonValueKind.Object) continue;
					if (candidate.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == PayloadEntryId) { entry = candidate; matches++; }
				}
				if (matches != 1) throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' has {matches.ToString(CultureInfo.InvariantCulture)} entries with id '{PayloadEntryId}'; exactly one is required");
				if (!entry.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString()))
					throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' records no payload file name");
				if (!entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var recordedSize) || recordedSize < 0)
					throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' records no usable payload size");
				if (!entry.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sha.GetString()))
					throw Refuse(HookLabPayloadRefusal.PayloadManifestInvalid, $"'{manifestPath}' records no usable payload digest");
				return (sha.GetString()!.Trim(), recordedSize, file.GetString()!);
			}
		}

		/// <summary>Reads the completed layout manifest and finds the payload entry in its complete inventory.
		/// This is independent from the payload-specific manifest and is identical for local packages and
		/// Gateway deployments.</summary>
		static (HookLabPayloadLayout Layout, HookLabPayloadCrossCheck CrossCheck, string? Path, string? Sha) ReadIndependentRecord(string hostRoot) {
			var manifest = SafeChild(hostRoot, LayoutManifestFileName);
			var text = TryReadRecord(manifest, HookLabPayloadRefusal.PackageRecordUnreadable)
				?? throw Refuse(HookLabPayloadRefusal.PackageRecordMissing, $"the completed layout manifest '{manifest}' is absent");
			using var document = ParseRecord(manifest,text,HookLabPayloadRefusal.PackageRecordUnreadable);
			if(document.RootElement.ValueKind!=JsonValueKind.Object || !document.RootElement.TryGetProperty("files",out var files) || files.ValueKind!=JsonValueKind.Array)
				throw Refuse(HookLabPayloadRefusal.PackageRecordUnreadable,$"'{manifest}' has no files inventory");
			var relative=(PayloadDirectoryName+"/"+PayloadFileName).Replace('\\','/'); string? sha=null; var matches=0;
			foreach(var file in files.EnumerateArray()) if(file.ValueKind==JsonValueKind.Object && file.TryGetProperty("path",out var path) && string.Equals(path.GetString(),relative,StringComparison.OrdinalIgnoreCase)) {
				matches++; if(file.TryGetProperty("sha256",out var digest) && digest.ValueKind==JsonValueKind.String) sha=digest.GetString();
			}
			if(matches!=1 || string.IsNullOrWhiteSpace(sha)) throw Refuse(HookLabPayloadRefusal.PackageRecordMissing,$"'{manifest}' does not contain exactly one hashed '{relative}' entry");
			var deployed=File.Exists(SafeChild(hostRoot,DeploymentManifestFileName));
			return (deployed?HookLabPayloadLayout.GatewayDeployment:HookLabPayloadLayout.PackedInstall,HookLabPayloadCrossCheck.Verified,manifest,sha);
		}

		/// <summary>Reads an independent record, returning null <b>only</b> when the file is genuinely absent.
		/// Absence is what the open attempt says it is: <see cref="FileNotFoundException"/> and
		/// <see cref="DirectoryNotFoundException"/> mean absent, while every other
		/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> means the record is there and
		/// unreadable, which refuses. The reparse-point guard is folded in here because it is the same
		/// metadata query, and it must tolerate absence for the same reason.</summary>
		static string? TryReadRecord(string path, HookLabPayloadRefusal unreadable) {
			FileAttributes attributes;
			try { attributes = File.GetAttributes(path); }
			catch (FileNotFoundException) { return null; }
			catch (DirectoryNotFoundException) { return null; }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{ throw Refuse(unreadable, $"the attributes of '{path}' could not be read ({ex.GetType().Name}: {ex.Message}), so whether an independent digest record is present cannot be established"); }
			if ((attributes & FileAttributes.ReparsePoint) != 0)
				throw Refuse(HookLabPayloadRefusal.ReparsePoint, $"'{path}' is a reparse point; the HookLab payload read path may not cross one");
			try { return File.ReadAllText(path); }
			catch (FileNotFoundException) { return null; }
			catch (DirectoryNotFoundException) { return null; }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{ throw Refuse(unreadable, $"'{path}' exists but could not be read ({ex.GetType().Name}: {ex.Message})"); }
		}

		static JsonDocument ParseRecord(string path, string text, HookLabPayloadRefusal refusal) {
			try { return JsonDocument.Parse(text); }
			catch (JsonException ex) { throw Refuse(refusal, $"'{path}' exists but is not valid JSON ({ex.Message}), so the independent digest record cannot be checked"); }
		}

		internal static string HashFromHandle(FileStream handle) {
			handle.Position = 0;
			using var sha = SHA256.Create();
			var digest = sha.ComputeHash(handle);
			handle.Position = 0;
			return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
		}

		// ---------------------------------------------------------------------------------------------
		// Path guards.
		//
		// These three are deliberately duplicated from ProbeDiscoveryStore's SafeChild /
		// RejectReparsePoint / RejectReparseParents
		// (HookLab\HookLab.Host.Transport\Discovery\ProbeDiscoveryStore.cs). They are not shared because
		// they cannot be: all three are private there, RejectReparseParents is bound to that store's
		// private stateRoot, and dgSpy.Extension deliberately does not reference HookLab.Host.Transport -
		// that assembly boundary is what keeps Harmony out of dnSpy's load context. Copying the shapes is
		// the lesser evil; if they are ever extracted into a shared BCL-only helper, both copies should go.
		// ---------------------------------------------------------------------------------------------

		static string SafeChild(string root, string name) {
			var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string path;
			try { path = Path.GetFullPath(Path.Combine(root, name)); }
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
				{ throw Refuse(HookLabPayloadRefusal.PathEscapesHostRoot, $"'{name}' is not a usable path component below '{root}' ({ex.GetType().Name})"); }
			if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
				throw Refuse(HookLabPayloadRefusal.PathEscapesHostRoot, $"the resolved payload path '{path}' escapes '{root}'");
			return path;
		}

		static void RejectReparsePoint(string path) {
			FileAttributes attributes;
			try { attributes = File.GetAttributes(path); }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{ throw Refuse(HookLabPayloadRefusal.PayloadUnreadable, $"the attributes of '{path}' could not be read ({ex.GetType().Name}: {ex.Message})"); }
			if ((attributes & FileAttributes.ReparsePoint) != 0)
				throw Refuse(HookLabPayloadRefusal.ReparsePoint, $"'{path}' is a reparse point; the HookLab payload read path may not cross one");
		}

		static void RejectReparseParents(string path, string hostRoot) {
			var root = Path.GetFullPath(hostRoot).TrimEnd(Path.DirectorySeparatorChar);
			var current = new DirectoryInfo(Path.GetFullPath(path)).Parent;
			while (current is not null) {
				RejectReparsePoint(current.FullName);
				if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) return;
				current = current.Parent;
			}
			throw Refuse(HookLabPayloadRefusal.PathEscapesHostRoot, $"'{path}' is outside the host root '{hostRoot}'");
		}

		static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		static string? Parent(string path) {
			var parent = Path.GetDirectoryName(path);
			return string.IsNullOrEmpty(parent) ? null : Normalize(parent!);
		}

		static HookLabPayloadRefusedException Refuse(HookLabPayloadRefusal refusal, string detail) =>
			new HookLabPayloadRefusedException(refusal, $"The shipped HookLab payload cannot be verified: {detail}.");
	}
}
