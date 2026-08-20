using System;
using System.CodeDom.Compiler;
using System.Linq;
using System.Reflection;

namespace HookLab.Probe.CorDebug.Patching {
	/// <summary>The CLR v4 compiler backend, and the only type in this assembly that names CodeDom.
	///
	/// <para>Keeping it alone in its own type is the point. CodeDom is a .NET Framework facility; a
	/// CoreCLR target has no <c>Microsoft.CSharp.CSharpCodeProvider</c> to find, so every mention of one
	/// has to sit somewhere a CoreCLR resident never prepares. That is a property of where the code
	/// lives, not of how carefully it is called.</para></summary>
	sealed class DesktopCodeDomHookCompiler : IHookSourceCompiler {
		const int MaximumDiagnostics = 20;
		const int MaximumDiagnosticLength = 1000;

		public Assembly Compile(HookCompileRequest request) {
			if (request == null) throw new ArgumentNullException(nameof(request));
			using (var provider = new Microsoft.CSharp.CSharpCodeProvider()) {
				var parameters = new CompilerParameters { GenerateExecutable = false, GenerateInMemory = true, TreatWarningsAsErrors = false };
				foreach (var reference in request.ReferencePaths) parameters.ReferencedAssemblies.Add(reference);
				if (request.PatchEngineReferencePath != null) parameters.ReferencedAssemblies.Add(request.PatchEngineReferencePath);
				var result = provider.CompileAssemblyFromSource(parameters, request.Source);
				var errors = result.Errors.Cast<CompilerError>().Where(error => !error.IsWarning).Select(Format).Take(MaximumDiagnostics).ToArray();
				if (errors.Length != 0) throw new HookCompilationException(errors);
				return result.CompiledAssembly;
			}
		}

		/// <summary>Private, and takes a CodeDom type. It used to live on the shared compiler, where its
		/// signature alone meant CodeDom was named on a type CoreCLR residents prepare.</summary>
		static string Format(CompilerError error) {
			var text = error.ErrorNumber + " (" + error.Line + "," + error.Column + "): " + error.ErrorText.Replace("\r", " ").Replace("\n", " ");
			return text.Length <= MaximumDiagnosticLength ? text : text.Substring(0, MaximumDiagnosticLength);
		}
	}
}
