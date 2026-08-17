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
		internal EmbeddedAssemblyEntry(string name, string resourceName, string sha256) {
			Name = name; ResourceName = resourceName; Sha256 = sha256;
		}
		internal string Name { get; }
		internal string ResourceName { get; }
		internal string Sha256 { get; }
	}

	/// <summary>Resolves exactly the identities named in the build-time manifest, from embedded bytes,
	/// after verifying each digest - and nothing else, from nowhere else.
	///
	/// The cache is not an optimization. T00 measured that a byte-loaded assembly lands outside the normal
	/// binding context and therefore does not satisfy another byte-loaded assembly's static references, so
	/// the resolve hook is the only thing that can hand back an already-loaded instance. Returning a second
	/// copy would give the probe a different HookLab.Contracts than the one its callers hold.</summary>
	sealed class EmbeddedAssemblyResolver {
		internal const string ManifestResourceName = "HookLab.Bootstrap.Payloads.manifest.txt";
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

		internal static EmbeddedAssemblyResolver FromEmbeddedManifest() {
			var assembly = typeof(EmbeddedAssemblyResolver).Assembly;
			return new EmbeddedAssemblyResolver(ParseManifest(ReadText(assembly, ManifestResourceName)), name => ReadResource(assembly, name));
		}

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
				var bytes = reader(entry.ResourceName) ?? throw new BootstrapIntegrityException("Embedded payload resource is missing: " + entry.ResourceName);
				var actual = Sha256Hex(bytes);
				if (!string.Equals(actual, Normalize(entry.Sha256), StringComparison.Ordinal))
					throw new BootstrapIntegrityException("Embedded payload digest mismatch for " + entry.Name + ": expected " + Normalize(entry.Sha256) + ", actual " + actual + ".");
				var assembly = Assembly.Load(bytes);
				var loadedName = assembly.GetName();
				if (!string.Equals(loadedName.Name, entry.Name, StringComparison.Ordinal))
					throw new BootstrapIntegrityException("Embedded payload identity mismatch: manifest says " + entry.Name + ", assembly says " + loadedName.Name + ".");
				ValidateVersion(requested, loadedName, entry.Name);
				resolved.Add(simpleName, assembly);
				loadCount++;
				return assembly;
			}
		}

		static void ValidateVersion(AssemblyName requested, AssemblyName loaded, string entryName) {
			if (requested.Version != null && loaded.Version != requested.Version)
				throw new BootstrapIntegrityException("Embedded payload version mismatch for " + entryName + ": requested " + requested.Version + ", embedded " + loaded.Version + ".");
		}

		/// <summary>The CLR probes the application directory before it ever raises AssemblyResolve, so this
		/// resolver cannot prevent a stray copy on disk from winning - it can only refuse to proceed once it
		/// has. A byte-loaded assembly has an empty Location; a probed one does not.</summary>
		internal void VerifyNoDiskProvenance(IEnumerable<Assembly> loadedAssemblies) {
			if (loadedAssemblies == null) throw new ArgumentNullException(nameof(loadedAssemblies));
			foreach (var assembly in loadedAssemblies) {
				string name;
				try { name = assembly.GetName().Name ?? ""; }
				catch (Exception) { continue; }
				if (!manifest.ContainsKey(name)) continue;
				string location;
				try { location = assembly.IsDynamic ? "" : assembly.Location; }
				catch (NotSupportedException) { location = ""; }
				if (!string.IsNullOrEmpty(location))
					throw new BootstrapIntegrityException("Payload '" + name + "' resolved from disk at '" + location + "' rather than from the embedded bundle.");
			}
		}

		void Refuse(string what, string why) {
			refusedCount++;
			lastRefusal = what + ": " + why;
		}

		internal static IReadOnlyList<EmbeddedAssemblyEntry> ParseManifest(string text) {
			if (text == null) throw new ArgumentNullException(nameof(text));
			var entries = new List<EmbeddedAssemblyEntry>();
			foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
				var line = raw.Trim();
				if (line.Length == 0) continue;
				var parts = line.Split('|');
				if (parts.Length != 3) throw new BootstrapIntegrityException("Malformed manifest line: " + line);
				var digest = Normalize(parts[2]);
				if (digest.Length != 64 || !digest.All(IsHex)) throw new BootstrapIntegrityException("Malformed manifest digest: " + parts[2]);
				entries.Add(new EmbeddedAssemblyEntry(parts[0].Trim(), parts[1].Trim(), digest));
			}
			return entries;
		}

		static bool IsHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
		static string Normalize(string value) => (value ?? "").Replace("-", "").Trim().ToLowerInvariant();

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
