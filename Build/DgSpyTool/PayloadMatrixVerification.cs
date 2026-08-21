using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using HookLab.Bootstrap;

/// <summary>Thrown by the source-linked payload matrix parser. The resident defines its own with this
/// name and namespace; this tool compiles the same parser and needs the same type to exist.</summary>
namespace HookLab.Bootstrap {
	sealed class BootstrapIntegrityException : Exception {
		public BootstrapIntegrityException(string message) : base(message) { }
	}
}

/// <summary>One assembly's identity, read from its bytes rather than from anything that describes them.</summary>
readonly record struct PayloadIdentity(string Name, Version Version, string PublicKeyToken) {
	public override string ToString() => Name + ", Version=" + Version + ", PublicKeyToken=" + PublicKeyToken;
}

/// <summary>Proves the build-generated payload matrix against the bytes that actually ship.
///
/// <para>The matrix is written by the bootstrap's build from the files it resolved, so on its own it
/// only records what the build believed. This is the step that makes it evidence: every declared slot
/// must exist as an embedded resource with the declared digest and the declared assembly identity, and
/// - the direction that catches the interesting mistake - every embedded payload resource must be
/// declared. A resident DLL nobody manifested is exactly the failure this closes.</para>
///
/// <para>Both carriers are checked from the one packaged payload file: the probe's pinned Harmony assets
/// are resources of the probe, which is itself a resource of the bootstrap, so the nesting is walked
/// rather than trusted.</para></summary>
static class PayloadMatrixVerification {
	internal const string PayloadResourcePrefix = "HookLab.Bootstrap.Payloads.";
	internal const string ProbeResourcePrefix = "HookLab.Probe.CorDebug.Backends.";

	/// <summary>Reads the matrix out of a packaged bootstrap payload and verifies it against that
	/// payload's own bytes. Returns the verified matrix so callers can publish it.</summary>
	internal static PayloadMatrix VerifyPayloadFile(string payloadPath) {
		var image = File.ReadAllBytes(payloadPath);
		return Verify(PayloadMatrix.Parse(ReadMatrixText(image, payloadPath)), image, payloadPath);
	}

	/// <summary>The matrix text a payload carries, unparsed. Split out so a test can state a matrix that
	/// differs from the shipped one and verify it against the real bytes - which is the only way to prove
	/// this rejects a wrong identity or an undeclared payload rather than merely being able to.</summary>
	internal static string ReadMatrixText(byte[] image, string describe) {
		var resources = ReadEmbeddedResources(image, describe);
		if (!resources.TryGetValue(PayloadMatrix.ResourceName, out var matrixBytes))
			throw new InvalidOperationException("The HookLab payload carries no payload matrix (" + PayloadMatrix.ResourceName + "): " + describe);
		return Text(matrixBytes);
	}

	internal static PayloadMatrix Verify(PayloadMatrix matrix, byte[] image, string describe) {
		var bootstrapResources = ReadEmbeddedResources(image, describe);
		// By role, not by name: the matrix guarantees exactly one resident, and the probe-carried payloads
		// are resources of whatever that resident turns out to be.
		var probeEntry = matrix.Entries.Single(entry => entry.Role == PayloadRole.Resident);
		if (!bootstrapResources.TryGetValue(probeEntry.ResourceName, out var probeImage))
			throw new InvalidOperationException("The HookLab payload carries no resident payload: " + probeEntry.ResourceName);
		var probeResources = ReadEmbeddedResources(probeImage, probeEntry.Id);

		VerifyCarrier(matrix, PayloadCarrier.Bootstrap, bootstrapResources, PayloadResourcePrefix, "the bootstrap");
		VerifyCarrier(matrix, PayloadCarrier.Probe, probeResources, ProbeResourcePrefix, "the resident probe");
		return matrix;
	}

	static void VerifyCarrier(PayloadMatrix matrix, PayloadCarrier carrier, IReadOnlyDictionary<string, byte[]> resources, string prefix, string what) {
		var declared = matrix.Carried(carrier).ToArray();
		foreach (var entry in declared) {
			if (!resources.TryGetValue(entry.ResourceName, out var bytes))
				throw new InvalidOperationException("Payload matrix slot '" + entry.Id + "' is empty: " + what + " carries no resource named " + entry.ResourceName + ".");
			var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
			if (digest != entry.Sha256)
				throw new InvalidOperationException("Payload '" + entry.Id + "' differs from the matrix: declared " + entry.Sha256 + ", embedded " + digest + ".");
			var identity = ReadIdentity(bytes, entry.Id);
			if (identity.Name != entry.AssemblyName || identity.Version != entry.AssemblyVersion || identity.PublicKeyToken != entry.PublicKeyToken)
				throw new InvalidOperationException("Payload '" + entry.Id + "' has the wrong identity: the matrix declares " +
					entry.AssemblyName + ", Version=" + entry.AssemblyVersion + ", PublicKeyToken=" + entry.PublicKeyToken + "; the bytes are " + identity + ".");
		}
		// The other direction, and the one that catches an unmanifested resident DLL: anything shaped like
		// a payload resource must be a matrix slot. Without this, adding an embedded assembly to either
		// carrier ships bytes that no manifest, package verification, or document ever mentions.
		var declaredResources = declared.Select(entry => entry.ResourceName).ToHashSet(StringComparer.Ordinal);
		foreach (var name in resources.Keys) {
			if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.Ordinal)) continue;
			if (!declaredResources.Contains(name))
				throw new InvalidOperationException("Unmanifested payload: " + what + " carries " + name + ", which no payload matrix entry declares.");
		}
	}

	/// <summary>Every embedded (not linked) manifest resource of a managed image, by name.</summary>
	internal static IReadOnlyDictionary<string, byte[]> ReadEmbeddedResources(byte[] image, string describe) {
		var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		using var pe = new PEReader(ImmutableArray.Create(image));
		if (!pe.HasMetadata) throw new InvalidOperationException("Not a managed assembly: " + describe);
		var metadata = pe.GetMetadataReader();
		var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
		foreach (var handle in metadata.ManifestResources) {
			var resource = metadata.GetManifestResource(handle);
			// A non-nil Implementation means the bytes live in another file or assembly. Nothing dgSpy
			// ships is shaped that way, and silently treating one as embedded would read the wrong offset.
			if (!resource.Implementation.IsNil) continue;
			if (directory.Size == 0) throw new InvalidOperationException("Managed resources are declared but absent: " + describe);
			var section = pe.GetSectionData(directory.RelativeVirtualAddress);
			var reader = section.GetReader((int)resource.Offset, section.Length - (int)resource.Offset);
			var length = reader.ReadInt32();
			resources[metadata.GetString(resource.Name)] = reader.ReadBytes(length);
		}
		return resources;
	}

	internal static PayloadIdentity ReadIdentity(byte[] image, string describe) {
		using var pe = new PEReader(ImmutableArray.Create(image));
		if (!pe.HasMetadata) throw new InvalidOperationException("Not a managed assembly: " + describe);
		var metadata = pe.GetMetadataReader();
		if (!metadata.IsAssembly) throw new InvalidOperationException("Not an assembly, only a module: " + describe);
		var definition = metadata.GetAssemblyDefinition();
		var publicKey = metadata.GetBlobBytes(definition.PublicKey);
		return new PayloadIdentity(metadata.GetString(definition.Name), definition.Version, PublicKeyToken(publicKey));
	}

	/// <summary>The last eight bytes of the public key's SHA-1, reversed - the standard token derivation.
	/// "none" for an unsigned assembly, matching how the matrix spells it.</summary>
	static string PublicKeyToken(byte[] publicKey) {
		if (publicKey.Length == 0) return "none";
		var hash = SHA1.HashData(publicKey);
		var token = new byte[8];
		for (var index = 0; index < token.Length; index++) token[index] = hash[hash.Length - 1 - index];
		return Convert.ToHexString(token).ToLowerInvariant();
	}

	static string Text(byte[] bytes) => new System.Text.UTF8Encoding(false, false).GetString(bytes).TrimStart((char)0xFEFF);

	/// <summary>The published, human-readable projection of the verified matrix. The embedded copy stays
	/// authoritative; this exists so the layout states its resident payload inventory without anyone
	/// having to open a PE file.</summary>
	internal static object Publishable(PayloadMatrix matrix) => new {
		format_version = 2,
		payloads = matrix.Entries.Select(entry => new {
			id = entry.Id,
			role = Spell(entry.Role),
			carrier = entry.Carrier == PayloadCarrier.Bootstrap ? "bootstrap" : "probe",
			resource = entry.ResourceName,
			target_framework = entry.TargetFramework,
			architecture = entry.Architecture,
			runtimes = Spell(entry.Runtimes),
			assembly = entry.AssemblyName,
			assembly_version = entry.AssemblyVersion.ToString(),
			public_key_token = entry.PublicKeyToken,
			provenance = entry.Provenance,
			requires = entry.Requires,
			sha256 = entry.Sha256,
		}).ToArray(),
	};

	static string Spell(PayloadRole role) => role switch {
		PayloadRole.Contracts => "contracts",
		PayloadRole.Resident => "resident",
		PayloadRole.Compiler => "compiler",
		PayloadRole.CompilerSupport => "compiler-support",
		_ => "patch-engine",
	};

	/// <summary>Every family the flags can carry. A family missing here does not fail anything - it simply
	/// vanishes from the published projection, which is how the layout's matrix JSON came to describe a
	/// payload set the shipped payload did not have.</summary>
	static string[] Spell(PayloadRuntimes runtimes) {
		var names = new List<string>();
		if ((runtimes & PayloadRuntimes.ClrV4) != 0) names.Add("clrv4");
		if ((runtimes & PayloadRuntimes.CoreClr) != 0) names.Add("coreclr");
		if ((runtimes & PayloadRuntimes.Mono) != 0) names.Add("mono");
		return names.ToArray();
	}
}
