using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class HookCompilationException : InvalidOperationException {
		internal HookCompilationException(IReadOnlyList<string> diagnostics) : base("Hook source did not compile: " + string.Join(" | ", diagnostics)) { Diagnostics = diagnostics; }
		public IReadOnlyList<string> Diagnostics { get; }
	}

	internal sealed class CompiledHook {
		internal CompiledHook(Assembly assembly, MethodInfo? prefix, MethodInfo? postfix, MethodInfo? finalizer, MethodInfo? transpiler) { Assembly = assembly; Prefix = prefix; Postfix = postfix; Finalizer = finalizer; Transpiler = transpiler; }
		internal Assembly Assembly { get; }
		internal MethodInfo? Prefix { get; }
		internal MethodInfo? Postfix { get; }
		internal MethodInfo? Finalizer { get; }
		internal MethodInfo? Transpiler { get; }
		internal MethodInfo[] Methods => new[] { Prefix, Postfix, Finalizer, Transpiler }.Where(method => method != null).Cast<MethodInfo>().ToArray();
	}

	/// <summary>Compiles one hook source and finds the phase methods in the result.
	///
	/// <para>Everything here is runtime-neutral by construction. It gathers references, materialises the
	/// patch-engine reference when the source needs one, asks
	/// <see cref="HookSourceCompilerSelector"/> for this runtime's backend, and validates what came back
	/// - and it names neither CodeDom nor Roslyn in any signature, field, or generic instantiation. That
	/// is the property this type is required to keep: a CLR v4 resident must never be made to resolve
	/// Roslyn, and a CoreCLR resident must never be made to find CodeDom, and both are reached through
	/// this type.</para></summary>
	internal static class CompiledHookCompiler {
		internal static CompiledHook Compile(string source, MethodBase target, HookLab.Contracts.HookKind kind) {
			if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Hook source is required.", nameof(source));
			if (target == null) throw new ArgumentNullException(nameof(target));
			if (kind != HookLab.Contracts.HookKind.Prefix && kind != HookLab.Contracts.HookKind.Postfix && kind != HookLab.Contracts.HookKind.Finalizer && kind != HookLab.Contracts.HookKind.Transpiler) throw new NotSupportedException("Compiled hooks currently support Prefix, Postfix, Finalizer, and Transpiler only.");
			var compiledAssembly=CompileAssembly(source,target,kind);
			var methods = compiledAssembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)).ToArray();
			var prefixes = methods.Where(method => string.Equals(method.Name, "Prefix", StringComparison.Ordinal)).ToArray();
			var postfixes = methods.Where(method => string.Equals(method.Name, "Postfix", StringComparison.Ordinal)).ToArray();
			var finalizers = methods.Where(method => string.Equals(method.Name, "Finalizer", StringComparison.Ordinal)).ToArray();
			var transpilers = methods.Where(method => string.Equals(method.Name, "Transpiler", StringComparison.Ordinal)).ToArray();
			if (prefixes.Length > 1 || postfixes.Length > 1 || finalizers.Length > 1 || transpilers.Length > 1) throw new HookCompilationException(new[] { "Source may declare at most one public static method for each HookLab patch phase." });
			if (kind == HookLab.Contracts.HookKind.Prefix && prefixes.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Prefix method." });
			if (kind == HookLab.Contracts.HookKind.Postfix && postfixes.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Postfix method." });
			if (kind == HookLab.Contracts.HookKind.Finalizer && finalizers.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Finalizer method." });
			if (kind == HookLab.Contracts.HookKind.Transpiler && transpilers.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Transpiler method." });
			return new CompiledHook(compiledAssembly, prefixes.SingleOrDefault(), postfixes.SingleOrDefault(), finalizers.SingleOrDefault(), transpilers.SingleOrDefault());
		}

		static Assembly CompileAssembly(string source,MethodBase target,HookLab.Contracts.HookKind kind) {
			// Transpiler source names HarmonyLib.CodeInstruction. Always compile it against the probe's
			// pinned embedded backend: a target AppDomain can expose a stale, deleted, or otherwise
			// unusable file-backed 0Harmony view after the first compiled revision. Prefix/Postfix/
			// Finalizer source does not name Harmony types and needs no such reference.
			var patchEngineReference = kind == HookLab.Contracts.HookKind.Transpiler ? MaterializeHarmonyReference() : null;
			try {
				var request=new HookCompileRequest("HookLab.Dynamic."+Guid.NewGuid().ToString("N"),source,References(target),patchEngineReference);
				return HookSourceCompilerSelector.ForCurrentRuntime().Compile(request);
			}
			finally { if(patchEngineReference!=null) try { File.Delete(patchEngineReference); } catch { } }
		}

		static string MaterializeHarmonyReference() {
			var path=Path.Combine(Path.GetTempPath(),"dgspy-hooklab-harmony-"+Guid.NewGuid().ToString("N")+".dll");
			using(var input=typeof(CompiledHookCompiler).Assembly.GetManifestResourceStream(PinnedBackendLoader.ResourceName) ?? throw new InvalidOperationException("The pinned Harmony compiler reference is unavailable."))
			using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) input.CopyTo(output);
			return path;
		}

		static string[] References(MethodBase target) {
			var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Concat(new[] { target.Module.Assembly })) {
				try {
					if (!assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location)) references.Add(assembly.Location);
				}
				catch (NotSupportedException) { }
			}
			return references.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
		}
	}
}
