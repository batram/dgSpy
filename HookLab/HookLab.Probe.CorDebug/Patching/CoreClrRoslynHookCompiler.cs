using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HookLab.Probe.CorDebug.Patching {
	/// <summary>The CoreCLR compiler backend, and the only type in this assembly that names Roslyn.
	///
	/// <para>Roslyn travels with the payload precisely because CoreCLR has no CodeDom. It is also large,
	/// and its dependency closure is the thing that failed loudest during CoreCLR bring-up, so a CLR v4
	/// resident that never compiles on this path should never be made to resolve any of it - which is
	/// what keeping every mention inside this one type buys.</para></summary>
	sealed class CoreClrRoslynHookCompiler : IHookSourceCompiler {
		const int MaximumDiagnostics = 20;

		public Assembly Compile(HookCompileRequest request) {
			if (request == null) throw new ArgumentNullException(nameof(request));
			var paths = request.PatchEngineReferencePath == null
				? request.ReferencePaths
				: request.ReferencePaths.Concat(new[] { request.PatchEngineReferencePath }).ToArray();
			var references = paths.Select(path => MetadataReference.CreateFromFile(path));
			var compilation = CSharpCompilation.Create(request.AssemblyName, new[] { CSharpSyntaxTree.ParseText(request.Source) }, references, options: null);
			compilation = compilation.WithOptions(compilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
			using (var stream = new MemoryStream()) {
				var emitted = compilation.Emit(stream);
				if (!emitted.Success) {
					// Reflected rather than typed. EmitResult.Diagnostics is an ImmutableArray<Diagnostic>,
					// and naming that here would put System.Collections.Immutable into a signature on top of
					// Roslyn itself - a second payload for a runtime that may not need either.
					var values = (System.Collections.IEnumerable)emitted.GetType().GetProperty("Diagnostics").GetValue(emitted, null);
					var diagnostics = values.Cast<object>().Select(value => value.ToString()).Take(MaximumDiagnostics).ToArray();
					throw new HookCompilationException(diagnostics);
				}
				return Assembly.Load(stream.ToArray());
			}
		}
	}
}
