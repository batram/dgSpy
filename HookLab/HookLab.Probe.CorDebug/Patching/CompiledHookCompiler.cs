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
		internal CompiledHook(Assembly assembly, MethodInfo method) { Assembly = assembly; Method = method; }
		internal Assembly Assembly { get; }
		internal MethodInfo Method { get; }
	}

	internal static class CompiledHookCompiler {
		const int MaximumDiagnostics = 20;
		const int MaximumDiagnosticLength = 1000;

		internal static CompiledHook CompilePrefix(string source, MethodBase target) {
			if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Hook source is required.", nameof(source));
			if (target == null) throw new ArgumentNullException(nameof(target));
			using (var provider = new CSharpCodeProvider()) {
				var parameters = new CompilerParameters { GenerateExecutable = false, GenerateInMemory = true, TreatWarningsAsErrors = false };
				foreach (var reference in References(target)) parameters.ReferencedAssemblies.Add(reference);
				var result = provider.CompileAssemblyFromSource(parameters, source);
				var errors = result.Errors.Cast<CompilerError>().Where(error => !error.IsWarning).Select(Format).Take(MaximumDiagnostics).ToArray();
				if (errors.Length != 0) throw new HookCompilationException(errors);
				var methods = result.CompiledAssembly.GetTypes()
					.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
					.Where(method => string.Equals(method.Name, "Prefix", StringComparison.Ordinal)).ToArray();
				if (methods.Length != 1) throw new HookCompilationException(new[] { "Source must declare exactly one public static Prefix method." });
				return new CompiledHook(result.CompiledAssembly, methods[0]);
			}
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
