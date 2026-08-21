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
	/// <para>Two facts available before anything runtime-specific has been touched: the corlib name, and
	/// whether <c>Mono.Runtime</c> exists. <c>mscorlib</c> alone is not enough, because Mono's corlib is
	/// called that too - and answering "CLR v4, so CodeDom" for a Unity target sends compilation to an
	/// external <c>mcs</c> that is not there.</para>
	///
	/// <para><see cref="MethodImplOptions.NoInlining"/> on the two constructors is load-bearing rather
	/// than decorative. Inlined into this method, the JIT would prepare both backends' types when it
	/// prepared this one, and preparing the wrong one means resolving a compiler assembly that this
	/// runtime does not have. The split into separate types is what makes that avoidable at all; the
	/// attribute is what keeps it avoided.</para></summary>
	static class HookSourceCompilerSelector {
		internal static bool IsDesktopClr => string.Equals(typeof(object).Assembly.GetName().Name,"mscorlib",StringComparison.Ordinal);

		/// <summary>Unity's Mono, which the corlib name cannot distinguish from CLR v4 because Mono's corlib
		/// is also called <c>mscorlib</c>. That is not a detail: CodeDom on Mono does not compile in-process,
		/// it shells out to <c>mcs</c> through the current executable, and no Unity player ships one. So a
		/// Mono target takes the Roslyn path CLR v4 carries but does not use - the payload is already there,
		/// it was simply never selected.</summary>
		internal static bool IsMono => Type.GetType("Mono.Runtime")!=null;

		/// <summary>CodeDom is the .NET Framework answer only. Every other runtime here compiles with the
		/// payload's own Roslyn, which is why the backend is no longer named after CoreCLR.</summary>
		internal static IHookSourceCompiler ForCurrentRuntime()=>IsDesktopClr && !IsMono?CodeDom():Roslyn();

		[MethodImpl(MethodImplOptions.NoInlining)]
		static IHookSourceCompiler CodeDom()=>new DesktopCodeDomHookCompiler();

		[MethodImpl(MethodImplOptions.NoInlining)]
		static IHookSourceCompiler Roslyn()=>new RoslynHookCompiler();
	}
}
