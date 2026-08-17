using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class BackendInventoryResult {
		internal BackendInventoryResult(IReadOnlyList<string> identities, string selectedIdentity, bool useResident) {
			LoadedIdentities = identities; SelectedIdentity = selectedIdentity; UseResident = useResident;
		}
		public IReadOnlyList<string> LoadedIdentities { get; }
		public string SelectedIdentity { get; }
		public bool UseResident { get; }
	}

	public sealed class BackendCompatibilityException : InvalidOperationException {
		public BackendCompatibilityException(string message) : base(message) { }
	}

	public static class BackendInventory {
		public const string PinnedName = "0Harmony";
		public static readonly Version PinnedVersion = new Version(2, 4, 2, 0);

		public static BackendInventoryResult InspectLoadedAssemblies() =>
			Inspect(AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName()));

		public static T SelectBeforeResolve<T>(Func<BackendInventoryResult> inventory, Func<BackendInventoryResult, T> resolver) {
			if (inventory == null) throw new ArgumentNullException(nameof(inventory));
			if (resolver == null) throw new ArgumentNullException(nameof(resolver));
			var result = inventory();
			return resolver(result);
		}

		public static BackendInventoryResult Inspect(IEnumerable<AssemblyName> loaded) {
			if (loaded == null) throw new ArgumentNullException(nameof(loaded));
			var candidates = loaded.Where(IsBackend).ToArray();
			var identities = candidates.Select(Format).OrderBy(x => x, StringComparer.Ordinal).ToArray();
			var incompatible = candidates.FirstOrDefault(a => IsPatchEngine(a) && !IsExactPinnedHarmony(a));
			if (incompatible != null)
				throw new BackendCompatibilityException("Incompatible resident patch backend: " + Format(incompatible));
			var exact = candidates.FirstOrDefault(IsExactPinnedHarmony);
			return new BackendInventoryResult(identities, Format(exact ?? PinnedAssemblyName()), exact != null);
		}

		static bool IsBackend(AssemblyName name) {
			var value = name.Name ?? "";
			return value.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
				value.IndexOf("HarmonyX", StringComparison.OrdinalIgnoreCase) >= 0 ||
				value.StartsWith("MonoMod", StringComparison.OrdinalIgnoreCase);
		}
		static bool IsPatchEngine(AssemblyName name) {
			var value = name.Name ?? "";
			return value.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
				value.IndexOf("HarmonyX", StringComparison.OrdinalIgnoreCase) >= 0 ||
				value.Equals("MonoMod.RuntimeDetour", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("MonoMod.Core", StringComparison.OrdinalIgnoreCase);
		}
		static bool IsExactPinnedHarmony(AssemblyName name) =>
			string.Equals(name.Name, PinnedName, StringComparison.Ordinal) && name.Version == PinnedVersion;
		static AssemblyName PinnedAssemblyName() => new AssemblyName(PinnedName) { Version = PinnedVersion };
		static string Format(AssemblyName name) => name.FullName ?? name.Name ?? "<unknown>";
	}
}
