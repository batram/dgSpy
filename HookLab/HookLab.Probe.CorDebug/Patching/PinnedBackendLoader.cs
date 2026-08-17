using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HookLab.Probe.CorDebug.Patching {
	internal static class PinnedBackendLoader {
		internal static string ResourceName => string.Equals(typeof(object).Assembly.GetName().Name,"mscorlib",StringComparison.Ordinal)
			? "HookLab.Probe.CorDebug.Backends.Desktop.0Harmony.dll"
			: "HookLab.Probe.CorDebug.Backends.CoreClr.0Harmony.dll";
		static readonly object Gate = new object();
		static Assembly? loadedBackend;
		internal static void EnsureLoaded(BackendInventoryResult inventory) {
			if (inventory.UseResident) return;
			lock (Gate) {
				if (loadedBackend != null) return;
				var assembly = typeof(PinnedBackendLoader).Assembly;
				using (var stream = assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("Pinned Harmony backend resource is missing.")) {
					var bytes = new byte[stream.Length]; var offset = 0;
					while (offset != bytes.Length) { var read = stream.Read(bytes, offset, bytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
					var loaded = Assembly.Load(bytes);
					var name = loaded.GetName();
					if (!string.Equals(name.Name, BackendInventory.PinnedName, StringComparison.Ordinal) || name.Version != BackendInventory.PinnedVersion)
						throw new BackendCompatibilityException("Embedded backend identity mismatch: " + name.FullName);
					loadedBackend = loaded;
					AppDomain.CurrentDomain.AssemblyResolve += ResolvePinnedBackend;
				}
			}
		}
		static Assembly? ResolvePinnedBackend(object sender, ResolveEventArgs eventArgs) {
			var requested = new AssemblyName(eventArgs.Name);
			return string.Equals(requested.Name, BackendInventory.PinnedName, StringComparison.Ordinal) && requested.Version == BackendInventory.PinnedVersion ? loadedBackend : null;
		}
	}
}
