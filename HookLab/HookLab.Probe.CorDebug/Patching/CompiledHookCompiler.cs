using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CSharp;

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

	internal static class CompiledHookCompiler {
		const int MaximumDiagnostics = 20;
		const int MaximumDiagnosticLength = 1000;
		const string HarmonyResourceName = "HookLab.Probe.CorDebug.Backends.0Harmony.dll";

		internal static CompiledHook Compile(string source, MethodBase target, HookLab.Contracts.HookKind kind) {
			if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Hook source is required.", nameof(source));
			if (target == null) throw new ArgumentNullException(nameof(target));
			if (kind != HookLab.Contracts.HookKind.Prefix && kind != HookLab.Contracts.HookKind.Postfix && kind != HookLab.Contracts.HookKind.Finalizer && kind != HookLab.Contracts.HookKind.Transpiler) throw new NotSupportedException("Compiled hooks currently support Prefix, Postfix, Finalizer, and Transpiler only.");
			using (var provider = new CSharpCodeProvider()) {
				var parameters = new CompilerParameters { GenerateExecutable = false, GenerateInMemory = true, TreatWarningsAsErrors = false };
				foreach (var reference in References(target)) parameters.ReferencedAssemblies.Add(reference);
				string? temporaryHarmonyReference = null;
				if (kind == HookLab.Contracts.HookKind.Transpiler && !parameters.ReferencedAssemblies.Cast<string>().Any(IsHarmonyReference)) {
					temporaryHarmonyReference = MaterializeHarmonyReference();
					parameters.ReferencedAssemblies.Add(temporaryHarmonyReference);
				}
				CompilerResults result;
				try { result = provider.CompileAssemblyFromSource(parameters, source); }
				finally { if (temporaryHarmonyReference != null) try { File.Delete(temporaryHarmonyReference); } catch { } }
				var errors = result.Errors.Cast<CompilerError>().Where(error => !error.IsWarning).Select(Format).Take(MaximumDiagnostics).ToArray();
				if (errors.Length != 0) throw new HookCompilationException(errors);
				var methods = result.CompiledAssembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)).ToArray();
				var prefixes = methods.Where(method => string.Equals(method.Name, "Prefix", StringComparison.Ordinal)).ToArray();
				var postfixes = methods.Where(method => string.Equals(method.Name, "Postfix", StringComparison.Ordinal)).ToArray();
				var finalizers = methods.Where(method => string.Equals(method.Name, "Finalizer", StringComparison.Ordinal)).ToArray();
				var transpilers = methods.Where(method => string.Equals(method.Name, "Transpiler", StringComparison.Ordinal)).ToArray();
				if (prefixes.Length > 1 || postfixes.Length > 1 || finalizers.Length > 1 || transpilers.Length > 1) throw new HookCompilationException(new[] { "Source may declare at most one public static method for each HookLab patch phase." });
				if (kind == HookLab.Contracts.HookKind.Prefix && prefixes.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Prefix method." });
				if (kind == HookLab.Contracts.HookKind.Postfix && postfixes.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Postfix method." });
				if (kind == HookLab.Contracts.HookKind.Finalizer && finalizers.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Finalizer method." });
				if (kind == HookLab.Contracts.HookKind.Transpiler && transpilers.Length != 1) throw new HookCompilationException(new[] { "Source must declare one public static Transpiler method." });
				return new CompiledHook(result.CompiledAssembly, prefixes.SingleOrDefault(), postfixes.SingleOrDefault(), finalizers.SingleOrDefault(), transpilers.SingleOrDefault());
			}
		}

		static bool IsHarmonyReference(string path) => string.Equals(Path.GetFileName(path), "0Harmony.dll", StringComparison.OrdinalIgnoreCase);
		static string MaterializeHarmonyReference() {
			var path=Path.Combine(Path.GetTempPath(),"dgspy-hooklab-harmony-"+Guid.NewGuid().ToString("N")+".dll");
			using(var input=typeof(CompiledHookCompiler).Assembly.GetManifestResourceStream(HarmonyResourceName) ?? throw new InvalidOperationException("The pinned Harmony compiler reference is unavailable."))
			using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) input.CopyTo(output);
			return path;
		}

		static IEnumerable<string> References(MethodBase target) {
			var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Concat(new[] { target.Module.Assembly })) {
				try {
					if (!assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location)) references.Add(assembly.Location);
				}
				catch (NotSupportedException) { }
			}
			return references.OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
		}

		static string Format(CompilerError error) {
			var text = error.ErrorNumber + " (" + error.Line + "," + error.Column + "): " + error.ErrorText.Replace("\r", " ").Replace("\n", " ");
			return text.Length <= MaximumDiagnosticLength ? text : text.Substring(0, MaximumDiagnosticLength);
		}
	}
}
