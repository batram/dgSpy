using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HookLab.Bootstrap {
	/// <summary>A payload failed verification, or resolved from somewhere other than the embedded bundle.
	/// Deliberately loud: a resolver that answers null on a digest mismatch lets the CLR fall through to
	/// its normal probing paths, which is the provenance this type exists to forbid.</summary>
	public sealed class BootstrapIntegrityException : Exception {
		public BootstrapIntegrityException(string message) : base(message) { }
	}

	sealed class EmbeddedAssemblyEntry {
		internal EmbeddedAssemblyEntry(string name, string resourceName, string sha256) : this(name, resourceName, sha256, null) { }
		internal EmbeddedAssemblyEntry(string name, string resourceName, string sha256, Version? expectedVersion) {
			Name = name; ResourceName = resourceName; Sha256 = sha256; ExpectedVersion = expectedVersion;
		}
		internal string Name { get; }
		internal string ResourceName { get; }
		internal string Sha256 { get; }
		/// <summary>The version the payload matrix says these bytes carry, or null when the entry was built
		/// without a matrix. A digest proves the bytes are the ones the build hashed; this proves the build
		/// hashed the assembly it meant to, so swapping one pinned dependency for another version of itself
		/// is refused by name rather than by an opaque digest mismatch.</summary>
		internal Version? ExpectedVersion { get; }
	}

	/// <summary>Resolves exactly the identities named in the build-time manifest, from embedded bytes,
	/// after verifying each digest - and nothing else, from nowhere else.
	///
	/// The cache is not an optimization. T00 measured that a byte-loaded assembly lands outside the normal
	/// binding context and therefore does not satisfy another byte-loaded assembly's static references, so
	/// the resolve hook is the only thing that can hand back an already-loaded instance. Returning a second
	/// copy would give the probe a different HookLab.Contracts than the one its callers hold.</summary>
	sealed class EmbeddedAssemblyResolver {
		internal const string ManifestResourceName = PayloadMatrix.ResourceName;
		internal const string PayloadResourcePrefix = "HookLab.Bootstrap.Payloads.";

		readonly object gate = new object();
		readonly Dictionary<string, EmbeddedAssemblyEntry> manifest;
		readonly Dictionary<string, Assembly> resolved = new Dictionary<string, Assembly>(StringComparer.Ordinal);
		readonly Func<string, byte[]?> reader;
		ResolveEventHandler? handler;
		int loadCount;
		int refusedCount;
		string? lastRefusal;

		internal EmbeddedAssemblyResolver(IEnumerable<EmbeddedAssemblyEntry> entries, Func<string, byte[]?> reader) {
			if (entries == null) throw new ArgumentNullException(nameof(entries));
			this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
			manifest = new Dictionary<string, EmbeddedAssemblyEntry>(StringComparer.Ordinal);
			foreach (var entry in entries) {
				if (manifest.ContainsKey(entry.Name)) throw new BootstrapIntegrityException("Duplicate manifest identity: " + entry.Name);
				manifest.Add(entry.Name, entry);
			}
			if (manifest.Count == 0) throw new BootstrapIntegrityException("The embedded payload manifest is empty.");
		}

		/// <summary>Builds the resolver from the build-generated payload matrix, serving exactly the entries
		/// the matrix says this assembly carries. Payloads carried by the probe - the pinned Harmony assets -
		/// are described by the same matrix but deliberately not served here: a second embedded copy would
		/// win the bind and leave two patch engines resident.</summary>
		internal static EmbeddedAssemblyResolver FromEmbeddedManifest() {
			var assembly = typeof(EmbeddedAssemblyResolver).Assembly;
			var matrix = PayloadMatrix.Parse(ReadText(assembly, PayloadMatrix.ResourceName));
			var entries = matrix.Carried(PayloadCarrier.Bootstrap)
				.Select(entry => new EmbeddedAssemblyEntry(entry.AssemblyName, entry.ResourceName, entry.Sha256, entry.AssemblyVersion))
				.ToArray();
			return new EmbeddedAssemblyResolver(entries, name => ReadResource(assembly, name));
		}

		/// <summary>The matrix this assembly was built with, for diagnostics and for tests that need to
		/// compare what the build declared against what the bytes actually are.</summary>
		internal static PayloadMatrix EmbeddedMatrix() =>
			PayloadMatrix.Parse(ReadText(typeof(EmbeddedAssemblyResolver).Assembly, PayloadMatrix.ResourceName));

		internal IReadOnlyList<string> ManifestIdentities => manifest.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
		internal int LoadCount { get { lock (gate) return loadCount; } }
		internal int RefusedCount { get { lock (gate) return refusedCount; } }
		internal string? LastRefusal { get { lock (gate) return lastRefusal; } }
		internal IReadOnlyList<Assembly> ResolvedAssemblies { get { lock (gate) return resolved.Values.ToArray(); } }

		internal void Install() {
			lock (gate) {
				if (handler != null) return;
				handler = OnAssemblyResolve;
				AppDomain.CurrentDomain.AssemblyResolve += handler;
			}
		}

		internal void Uninstall() {
			lock (gate) {
				if (handler == null) return;
				AppDomain.CurrentDomain.AssemblyResolve -= handler;
				handler = null;
			}
		}

		Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args) => Resolve(args.Name);

		internal Assembly? Resolve(string requestedFullName) {
			if (string.IsNullOrEmpty(requestedFullName)) return null;
			AssemblyName requested;
			try { requested = new AssemblyName(requestedFullName); }
			catch (Exception) { Refuse(requestedFullName, "unparsable identity"); return null; }
			var simpleName = requested.Name ?? "";
			lock (gate) {
				if (!manifest.TryGetValue(simpleName, out var entry)) {
					// Not ours. Answering null is correct here and is the only case where falling through to
					// the CLR is right: the request is for something the bundle never claimed to carry.
					Refuse(simpleName, "not in the manifest");
					return null;
				}
				if (resolved.TryGetValue(simpleName, out var cached)) {
					ValidateVersion(requested, cached.GetName(), entry.Name);
					return cached;
				}
				var assembly = LoadEmbedded(entry);
				ValidateVersion(requested, assembly.GetName(), entry.Name);
				return assembly;
			}
		}

		/// <summary>Byte-loads one manifest entry after verifying its digest, and caches it. Callers hold
		/// <see cref="gate"/>.</summary>
		Assembly LoadEmbedded(EmbeddedAssemblyEntry entry) {
			if (resolved.TryGetValue(entry.Name, out var cached)) return cached;
			var bytes = reader(entry.ResourceName) ?? throw new BootstrapIntegrityException("Embedded payload resource is missing: " + entry.ResourceName);
			var actual = Sha256Hex(bytes);
			if (!string.Equals(actual, Normalize(entry.Sha256), StringComparison.Ordinal))
				throw new BootstrapIntegrityException("Embedded payload digest mismatch for " + entry.Name + ": expected " + Normalize(entry.Sha256) + ", actual " + actual + ".");
			var assembly = Assembly.Load(bytes);
			var loadedName = assembly.GetName();
			if (!string.Equals(loadedName.Name, entry.Name, StringComparison.Ordinal))
				throw new BootstrapIntegrityException("Embedded payload identity mismatch: manifest says " + entry.Name + ", assembly says " + loadedName.Name + ".");
			if (entry.ExpectedVersion != null && loadedName.Version != entry.ExpectedVersion)
				throw new BootstrapIntegrityException("Embedded payload identity mismatch for " + entry.Name + ": manifest says version " + entry.ExpectedVersion + ", assembly says " + loadedName.Version + ".");
			resolved.Add(entry.Name, assembly);
			loadCount++;
			return assembly;
		}

		static void ValidateVersion(AssemblyName requested, AssemblyName loaded, string entryName) {
			if (requested.Version != null && loaded.Version != requested.Version)
				throw new BootstrapIntegrityException("Embedded payload version mismatch for " + entryName + ": requested " + requested.Version + ", embedded " + loaded.Version + ".");
		}

		/// <summary>What one expected payload identity bound to, for diagnostics. Never the decision itself:
		/// the decision is instance equality with the digest-verified embedded assembly.</summary>
		internal sealed class PayloadBinding {
			internal PayloadBinding(string expected, string actual, string location, bool verified) {
				Expected = expected; Actual = actual; Location = location; MatchedVerifiedInstance = verified;
			}
			internal string Expected { get; }
			internal string Actual { get; }
			/// <summary>Empty for a byte-loaded assembly. Supporting evidence, not the contract - an empty
			/// Location proves only that something was loaded from memory, not that it was our bytes.</summary>
			internal string Location { get; }
			internal bool MatchedVerifiedInstance { get; }
		}

		/// <summary>Asks the CLR binder to satisfy each payload's <b>exact</b> identity, and requires it to
		/// answer with the digest-verified instance this resolver byte-loaded.
		///
		/// <para>The predicate this replaced asked a different and much broader question - whether any
		/// assembly sharing a simple name with the manifest was loaded from disk - which is only equivalent
		/// in a domain that holds nothing else. Proven wrong live on 2026-08-20: HookLab reached an IIS
		/// application domain and refused there because the hosted application carries its own
		/// System.Collections.Immutable 1.2.1.0, an assembly no payload reference could ever have bound to.
		/// Simple-name coexistence is ordinary in an application domain; it is not evidence about our
		/// bindings.</para>
		///
		/// <para>What is asked instead: for the exact identity of each verified embedded assembly, resolve
		/// it the way payload code's own references resolve - through the binder, after this resolver is
		/// installed - and require the same instance back. A disk copy that can satisfy the exact identity
		/// wins ordinary probing before AssemblyResolve is ever raised, so it comes back as a different
		/// instance and is refused. A foreign assembly at another identity cannot satisfy the request, so
		/// probing fails, the resolve hook runs, and our copy is returned.</para>
		///
		/// <para>Run this after <see cref="Install"/> and before any payload code executes: a binding is
		/// worth verifying only while nothing has acted on it yet. The cost is that every manifest entry is
		/// byte-loaded here rather than on first use.</para></summary>
		internal IReadOnlyList<PayloadBinding> VerifyPayloadBindings() {
			var bindings = new List<PayloadBinding>();
			lock (gate) {
				foreach (var entry in manifest.Values) {
					var embedded = LoadEmbedded(entry);
					// From the digest-verified bytes, not from the manifest text or from anything on disk:
					// this is the identity payload references actually name.
					var expected = embedded.GetName().FullName;
					Assembly bound;
					try { bound = Assembly.Load(new AssemblyName(expected)); }
					catch (Exception ex) {
						throw new BootstrapIntegrityException("Payload dependency '" + expected + "' could not be resolved through the CLR binder: " + ex.GetType().Name + ": " + ex.Message);
					}
					var matched = ReferenceEquals(bound, embedded);
					bindings.Add(new PayloadBinding(expected, Describe(bound), LocationOf(bound), matched));
					if (!matched)
						throw new BootstrapIntegrityException("Payload dependency '" + expected + "' bound to " + Describe(bound) +
							(LocationOf(bound).Length == 0 ? "" : " at '" + LocationOf(bound) + "'") + " instead of the verified embedded payload.");
				}
			}
			return bindings;
		}

		static string Describe(Assembly assembly) {
			try { return assembly.GetName().FullName; }
			catch (Exception) { return "an assembly whose identity could not be read"; }
		}

		static string LocationOf(Assembly assembly) {
			try { return assembly.IsDynamic ? "" : assembly.Location ?? ""; }
			catch (NotSupportedException) { return ""; }
		}

		void Refuse(string what, string why) {
			refusedCount++;
			lastRefusal = what + ": " + why;
		}

		static string Normalize(string value) => PayloadMatrix.NormalizeDigest(value);

		internal static string Sha256Hex(byte[] bytes) {
			using (var sha = SHA256.Create()) {
				var hash = sha.ComputeHash(bytes);
				var builder = new StringBuilder(hash.Length * 2);
				foreach (var value in hash) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
				return builder.ToString();
			}
		}

		static string ReadText(Assembly assembly, string resourceName) {
			var bytes = ReadResource(assembly, resourceName) ?? throw new BootstrapIntegrityException("Embedded payload manifest is missing.");
			return new UTF8Encoding(false, false).GetString(bytes).TrimStart((char)0xFEFF);
		}

		static byte[]? ReadResource(Assembly assembly, string resourceName) {
			using (var stream = assembly.GetManifestResourceStream(resourceName)) {
				if (stream == null) return null;
				var bytes = new byte[stream.Length];
				var offset = 0;
				while (offset != bytes.Length) {
					var read = stream.Read(bytes, offset, bytes.Length - offset);
					if (read == 0) throw new EndOfStreamException("Truncated embedded resource: " + resourceName);
					offset += read;
				}
				return bytes;
			}
		}
	}
}
