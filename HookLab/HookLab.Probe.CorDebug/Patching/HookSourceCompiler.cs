using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HookLab.Probe.CorDebug.Patching {
	/// <summary>Everything a compiler backend needs, in types every runtime has.
	///
	/// <para>Strings and string arrays on purpose. A request that carried a <c>CompilerParameters</c> or
	/// a <c>MetadataReference</c> would put one runtime's compiler type in a signature the other runtime
	/// also reaches, and the CLR resolves the types in a signature when it prepares the member - so a
	/// CoreCLR target would be made to find CodeDom, or a CLR v4 target Roslyn, before either had done
	/// anything wrong.</para></summary>
	sealed class HookCompileRequest {
		internal HookCompileRequest(string assemblyName,string source,IReadOnlyList<string> referencePaths,string? patchEngineReferencePath) {
			AssemblyName=assemblyName; Source=source; ReferencePaths=referencePaths; PatchEngineReferencePath=patchEngineReferencePath;
		}
		internal string AssemblyName { get; }
		internal string Source { get; }
		/// <summary>Assembly file paths to compile against.</summary>
		internal IReadOnlyList<string> ReferencePaths { get; }
		/// <summary>A materialised copy of the pinned patch engine, or null when the source does not name
		/// its types. Only Transpiler source does.</summary>
		internal string? PatchEngineReferencePath { get; }
	}

	/// <summary>The narrow boundary between "compile this hook" and whichever compiler this runtime has.
	///
	/// <para>One method, taking runtime-neutral data and returning a BCL <see cref="Assembly"/> or
	/// throwing <see cref="HookCompilationException"/> with bounded string diagnostics. No CodeDom or
	/// Roslyn type appears here, in any implementation's public surface, or anywhere in
	/// <see cref="CompiledHookCompiler"/> - which is what lets each backend's technology stay unreachable
	/// on the runtime that does not have it.</para></summary>
	interface IHookSourceCompiler {
		Assembly Compile(HookCompileRequest request);
	}

	/// <summary>Picks the compiler for the runtime this resident is living in.
	///
	/// <para>The corlib name is the test because it is the one fact available before anything
	/// runtime-specific has been touched: <c>mscorlib</c> means CLR v4 and CodeDom, anything else means
	/// CoreCLR and Roslyn.</para>
	///
	/// <para><see cref="MethodImplOptions.NoInlining"/> on the two constructors is load-bearing rather
	/// than decorative. Inlined into this method, the JIT would prepare both backends' types when it
	/// prepared this one, and preparing the wrong one means resolving a compiler assembly that this
	/// runtime does not have. The split into separate types is what makes that avoidable at all; the
	/// attribute is what keeps it avoided.</para></summary>
	static class HookSourceCompilerSelector {
		internal static bool IsDesktopClr => string.Equals(typeof(object).Assembly.GetName().Name,"mscorlib",StringComparison.Ordinal);

		internal static IHookSourceCompiler ForCurrentRuntime()=>IsDesktopClr?Desktop():CoreClr();

		[MethodImpl(MethodImplOptions.NoInlining)]
		static IHookSourceCompiler Desktop()=>new DesktopCodeDomHookCompiler();

		[MethodImpl(MethodImplOptions.NoInlining)]
		static IHookSourceCompiler CoreClr()=>new CoreClrRoslynHookCompiler();
	}
}
