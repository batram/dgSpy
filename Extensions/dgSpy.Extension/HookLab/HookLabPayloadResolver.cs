using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace dgSpy.Extension.HookLab {
	/// <summary>Which of the three supported host layouts the payload was resolved from. Detected from what
	/// is present in and above the host root, never from a caller-supplied hint: a hint is exactly the thing
	/// a damaged or substituted install would get wrong.</summary>
	enum HookLabPayloadLayout {
		/// <summary>A developer worktree: build-dgspy.ps1 staged the payload into the dnSpy build output.
		/// Neither independent record exists, so the cross-check is skipped and says so.</summary>
		DeveloperWorktree,
		/// <summary>A packed or directly installed tree - host root <c>&lt;install&gt;\cli</c>, package
		/// manifest at <c>..\manifest.json</c> carrying a top-level <c>hooklab_payload_sha256</c>.</summary>
		PackedInstall,
		/// <summary>A Gateway-deployed version directory - <c>deployment-manifest.json</c> in the host root,
		/// with the original package manifest recorded verbatim under <c>packaged</c>. This is the layout
		/// <c>launch_local_host</c> actually produces, so it is the one that matters most.</summary>
		GatewayDeployment,
	}

	/// <summary>Whether the second, independent digest record was actually consulted.</summary>
	enum HookLabPayloadCrossCheck {
		/// <summary>The independent record was read and agreed with the payload manifest.</summary>
		Verified,
		/// <summary>No independent record exists because this is a developer worktree. Reported rather than
		/// silently omitted: a caller that cannot tell "checked" from "not checked" will assume the
		/// stronger one.</summary>
		SkippedDeveloperWorktree,
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
	/// <para><b>Layout authority.</b> The directory name, the payload file name, the manifest file name and
	/// the manifest shape are owned by <c>packaging\HookLabPayload.ps1</c>. The constants below were read
	/// from it; if it changes, this must follow.</para>
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
		// Read from packaging\HookLabPayload.ps1, which is the authority on all five.
		public const string PayloadDirectoryName = "hooklab";
		public const string PayloadFileName = "hooklab-bootstrap.net48.payload";
		public const string PayloadManifestFileName = "hooklab-payload-manifest.json";
		public const string PayloadEntryId = "hooklab_bootstrap";
		// Read from dgSpy.Gateway\DeploymentService.cs and install-dgspy.ps1.
		public const string DeploymentManifestFileName = "deployment-manifest.json";
		public const string PackageManifestFileName = "manifest.json";
		public const string PackagedSectionName = "packaged";
		public const string PackageDigestPropertyName = "hooklab_payload_sha256";
		const string InstallerScriptName = "install-dgspy.ps1";
		const string PackedHostRootName = "cli";
		const int HostRootSearchDepth = 4;

		/// <summary>Opens the payload of the host this code is running in.</summary>
		public static ResolvedHookLabPayload Open() => OpenFrom(ResolveHostRoot(AppDomain.CurrentDomain.BaseDirectory));

		/// <summary>The host root for a given app-base directory. The packaged net48 layout puts the app base
		/// at the host root while the self-contained net10 layout puts it under <c>bin\</c>, so this walks up
		/// a bounded number of levels looking for the <c>hooklab</c> directory rather than hardcoding either
		/// depth. When nothing is found the base directory is returned unchanged, so the refusal is a
		/// truthful "no payload at &lt;expected path&gt;" rather than a guess about the layout.</summary>
		public static string ResolveHostRoot(string baseDirectory) {
			if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
			var start = Normalize(baseDirectory);
			var current = start;
			for (var level = 0; level < HostRootSearchDepth && current is not null; level++) {
				if (Directory.Exists(Path.Combine(current, PayloadDirectoryName))) return current;
				current = Parent(current);
			}
			return start;
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

		/// <summary>Finds and reads the second, independent digest record. Which file that is depends on the
		/// layout, and getting this wrong is the failure mode a spec review specifically called out: an
		/// implementation that only knows the packed convention either refuses every Gateway-deployed host or
		/// silently skips the cross-check on it, while still passing a packed-layout acceptance test.
		///
		/// The Gateway-deployed marker is checked first because a deployed tree is a copy of a packed host
		/// root and could in principle carry either.</summary>
		static (HookLabPayloadLayout Layout, HookLabPayloadCrossCheck CrossCheck, string? Path, string? Sha) ReadIndependentRecord(string hostRoot) {
			var deployment = SafeChild(hostRoot, DeploymentManifestFileName);
			if (File.Exists(deployment)) {
				RejectReparsePoint(deployment);
				var packaged = ReadJson(deployment, HookLabPayloadRefusal.PackageRecordUnreadable);
				using (packaged) {
					if (packaged.RootElement.ValueKind != JsonValueKind.Object || !packaged.RootElement.TryGetProperty(PackagedSectionName, out var section) || section.ValueKind != JsonValueKind.Object)
						throw Refuse(HookLabPayloadRefusal.PackageRecordMissing, $"'{deployment}' records no '{PackagedSectionName}' package manifest, so this deployment cannot prove what package it came from");
					var sha = ReadDigest(section, deployment);
					return (HookLabPayloadLayout.GatewayDeployment, HookLabPayloadCrossCheck.Verified, deployment, sha);
				}
			}

			var parent = Parent(hostRoot);
			var packageManifest = parent is null ? null : Path.Combine(parent, PackageManifestFileName);
			// A packed install is recognised by more than the record itself, so deleting manifest.json
			// downgrades to "packed install with a missing record" (a refusal) rather than to "developer
			// worktree" (a skipped cross-check). install-dgspy.ps1 copies both the manifest and itself beside
			// the 'cli' host root, so any one of the three surviving is enough to identify the layout.
			var looksPacked = (packageManifest is not null && File.Exists(packageManifest))
				|| (parent is not null && File.Exists(Path.Combine(parent, InstallerScriptName)))
				|| string.Equals(Path.GetFileName(hostRoot), PackedHostRootName, StringComparison.OrdinalIgnoreCase);
			if (looksPacked) {
				if (packageManifest is null || !File.Exists(packageManifest))
					throw Refuse(HookLabPayloadRefusal.PackageRecordMissing, $"this is a packed or directly installed host root and its package manifest '{packageManifest ?? PackageManifestFileName}' is absent, so the independent digest record cannot be checked");
				RejectReparsePoint(packageManifest);
				var document = ReadJson(packageManifest, HookLabPayloadRefusal.PackageRecordUnreadable);
				using (document) {
					if (document.RootElement.ValueKind != JsonValueKind.Object)
						throw Refuse(HookLabPayloadRefusal.PackageRecordUnreadable, $"'{packageManifest}' is not a JSON object");
					var sha = ReadDigest(document.RootElement, packageManifest);
					return (HookLabPayloadLayout.PackedInstall, HookLabPayloadCrossCheck.Verified, packageManifest, sha);
				}
			}

			return (HookLabPayloadLayout.DeveloperWorktree, HookLabPayloadCrossCheck.SkippedDeveloperWorktree, null, null);
		}

		static string ReadDigest(JsonElement section, string path) {
			if (!section.TryGetProperty(PackageDigestPropertyName, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
				throw Refuse(HookLabPayloadRefusal.PackageRecordMissing, $"'{path}' carries no '{PackageDigestPropertyName}', so the independent digest record is absent");
			return value.GetString()!.Trim();
		}

		static JsonDocument ReadJson(string path, HookLabPayloadRefusal refusal) {
			string text;
			try { text = File.ReadAllText(path); }
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
				{ throw Refuse(refusal, $"'{path}' exists but could not be read ({ex.GetType().Name}: {ex.Message})"); }
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
