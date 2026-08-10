using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HookLab.Bootstrap.Tests {
	/// <summary>Puts two more assemblies named <c>HookLab.Bootstrap.Tests</c> in the AppDomain, each with its
	/// own module MVID, so hook target selection has to choose between same-named modules.
	///
	/// Byte-loading rewritten copies of this very assembly is the cheapest faithful way to get that shape:
	/// the copies carry the same type, the same method, the same metadata token and the same IL - so every
	/// guard field except the MVID matches all three, which is exactly the situation in which selecting by
	/// simple name picks by enumeration order. The disk-loaded original is a third candidate and enumerates
	/// first, so "the wrong one is first" is a property of the fixture rather than an assumption.</summary>
	public static class DuplicateHookTargets {
		public const string SimpleName = "HookLab.Bootstrap.Tests";
		static Assembly? decoy;
		static Assembly? target;

		/// <summary>mode: <c>distinct</c> gives the two copies different MVIDs; <c>same-mvid</c> gives them
		/// one MVID, which is the ambiguous case. Returns measured evidence rather than assurances:
		/// the MVIDs, whether the two copies really are distinct assemblies, and the MVIDs of every
		/// same-named assembly in enumeration order.</summary>
		internal static string Load(string mode) {
			var path = Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, SimpleName + ".dll");
			var image = File.ReadAllBytes(path);
			var original = typeof(FixtureWorker).Assembly.ManifestModule.ModuleVersionId;
			var decoyMvid = Guid.NewGuid();
			var targetMvid = string.Equals(mode, "same-mvid", StringComparison.Ordinal) ? decoyMvid : Guid.NewGuid();
			// The trailing byte keeps the two images distinct even when their MVIDs are identical. Whether
			// .NET Framework returns one Assembly for two identical byte arrays is not measured here, so the
			// fixture does not depend on the answer; the evidence string reports what it actually got.
			decoy = Assembly.Load(WithMvid(image, original, decoyMvid, 0));
			target = Assembly.Load(WithMvid(image, original, targetMvid, 1));
			return "decoy_mvid=" + Mvid(decoy).ToString("D") +
				";target_mvid=" + Mvid(target).ToString("D") +
				";distinct_assemblies=" + (!ReferenceEquals(decoy, target) ? "true" : "false") +
				";enumeration_order=" + string.Join(",", SameNamed().Select(a => Mvid(a).ToString("D")).ToArray());
		}

		internal static Guid TargetMvid => Mvid(target ?? throw new InvalidOperationException("No duplicates loaded."));

		/// <summary>The hook specification for the target copy's FixtureWorker.Run, with every guard field
		/// computed from that copy. <paramref name="mvidOverride"/> replaces the module MVID, which is how
		/// the zero-match case asks for a module nobody has.</summary>
		internal static string HookLines(string hookId, string mvidOverride) {
			var method = TargetMethod();
			var values = new Dictionary<string, string>(StringComparer.Ordinal) {
				["hook_id"] = hookId,
				["hook_kind"] = "Prefix",
				["hook_assembly"] = SimpleName,
				["hook_type"] = method.DeclaringType!.FullName!,
				["hook_method"] = method.Name,
				["hook_module_mvid"] = mvidOverride.Length != 0 ? mvidOverride : TargetMvid.ToString("D"),
				["hook_metadata_token"] = unchecked((uint)method.MetadataToken).ToString(CultureInfo.InvariantCulture),
				["hook_declaring_type"] = method.DeclaringType!.FullName!,
				["hook_method_signature"] = GuardFacts.Signature(method),
				["hook_il_sha256"] = GuardFacts.IlSha256(method),
			};
			var builder = new StringBuilder();
			foreach (var pair in values) builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
			return builder.ToString();
		}

		/// <summary>Calls Run on an instance of each copy, then drains the probe's event buffer. Which copy
		/// is patched is visible in the event count; that both copies really ran is visible in their own
		/// static counters, so "no events" cannot be mistaken for "the call never happened".</summary>
		internal static string InvokeCopies(int decoyCalls, int targetCalls) {
			var decoyObserved = Invoke(decoy!, decoyCalls);
			var targetObserved = Invoke(target!, targetCalls);
			var runtime = HookLabBootstrap.Runtime ?? throw new InvalidOperationException("No probe runtime.");
			var source = runtime.GetType().GetProperty("Events")!.GetValue(runtime)!;
			var drained = (IEnumerable)source.GetType().GetMethod("Drain")!.Invoke(source, new object[] { 64 })!;
			var events = drained.Cast<object>().Count();
			return "events=" + events.ToString(CultureInfo.InvariantCulture) +
				";decoy_observed=" + decoyObserved.ToString(CultureInfo.InvariantCulture) +
				";target_observed=" + targetObserved.ToString(CultureInfo.InvariantCulture);
		}

		static MethodInfo TargetMethod() {
			var type = (target ?? throw new InvalidOperationException("No duplicates loaded.")).GetType(typeof(FixtureWorker).FullName!, true)!;
			return type.GetMethod(nameof(FixtureWorker.Run))!;
		}

		static int Invoke(Assembly copy, int calls) {
			var type = copy.GetType(typeof(FixtureWorker).FullName!, true)!;
			var method = type.GetMethod(nameof(FixtureWorker.Run))!;
			var instance = Activator.CreateInstance(type);
			for (var index = 0; index < calls; index++) method.Invoke(instance, new object[] { 1 });
			return (int)type.GetField(nameof(FixtureWorker.Observed), BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
		}

		static IEnumerable<Assembly> SameNamed() => AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => string.Equals(SafeName(a), SimpleName, StringComparison.Ordinal));

		static string SafeName(Assembly assembly) { try { return assembly.GetName().Name ?? ""; } catch (Exception) { return ""; } }
		static Guid Mvid(Assembly assembly) => assembly.ManifestModule.ModuleVersionId;

		/// <summary>Replaces the 16 MVID bytes in the #GUID heap and appends <paramref name="padding"/>
		/// ignored trailing bytes. Exactly one occurrence is expected; anything else means the assumption
		/// about where the MVID lives is wrong, and that has to fail loudly rather than quietly patch the
		/// wrong bytes and produce a fixture nobody can trust.</summary>
		static byte[] WithMvid(byte[] image, Guid original, Guid replacement, int padding) {
			var from = original.ToByteArray();
			var to = replacement.ToByteArray();
			var copy = new byte[image.Length + padding];
			Buffer.BlockCopy(image, 0, copy, 0, image.Length);
			var hits = 0;
			for (var index = 0; index + 16 <= image.Length; index++) {
				var match = true;
				for (var offset = 0; offset < 16; offset++) if (copy[index + offset] != from[offset]) { match = false; break; }
				if (!match) continue;
				Buffer.BlockCopy(to, 0, copy, index, 16);
				hits++;
				index += 15;
			}
			if (hits != 1) throw new InvalidOperationException("Expected exactly one MVID occurrence in the image, found " + hits + ".");
			return copy;
		}
	}
}
