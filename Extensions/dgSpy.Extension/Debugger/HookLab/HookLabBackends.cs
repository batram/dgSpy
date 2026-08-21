using System;
using System.Collections.Generic;
using System.Linq;

namespace dgSpy.Extension {
	readonly struct HookLabRuntimeIdentity {
		public HookLabRuntimeIdentity(Guid guid,string name) { Guid=guid; Name=name ?? String.Empty; }
		public Guid Guid { get; }
		public string Name { get; }
	}

	/// <summary>How a resident payload gets into a target. This is a property of the runtime, not a
	/// preference: CLR v4 is reached by loading a native bootstrap into the process, and CoreCLR and Mono
	/// by one debugger evaluation, because nothing else works on each. Mono has no native bootstrap and
	/// needs none - its soft debugger can already run an expression in the target.</summary>
	enum HookLabArrival { NativeBootstrap,DebuggerEvaluation }

	/// <summary>One supported runtime backend, stated once and immutably.
	///
	/// <para>Every field here used to be a conditional somewhere else. Which Harmony asset, which
	/// compiler, how the payload arrives, whether the runtime version has to be read from the target,
	/// whether the debugger needs re-synchronising afterwards - each was a separate
	/// <c>backend == CoreClr</c> in shared orchestration, and each was an independent chance to encode a
	/// slightly different idea of what "CoreCLR" means.</para>
	///
	/// <para>The payload ids are the ones in the shipped payload matrix, so the host's idea of which
	/// patch engine a runtime uses and the build's idea of which patch engine it ships are the same
	/// string, and a test can require them to agree.</para></summary>
	sealed class HookLabBackend {
		internal HookLabBackend(string id,string name,string family,HookLabArrival arrival,Guid[] runtimeGuids,string runtimeNamePrefix,
			int priority,string? fixedRuntimeId,bool synchronizesAfterArrival,string patchEnginePayloadId,string[] compilerPayloadIds) {
			Id=id; Name=name; Family=family; Arrival=arrival; RuntimeGuids=runtimeGuids; RuntimeNamePrefix=runtimeNamePrefix;
			Priority=priority; FixedRuntimeId=fixedRuntimeId; SynchronizesAfterArrival=synchronizesAfterArrival;
			PatchEnginePayloadId=patchEnginePayloadId; CompilerPayloadIds=compilerPayloadIds;
		}

		/// <summary>Stable identifier, architecture included, so an x86 variant can never be confused with
		/// its x64 sibling if one is ever added.</summary>
		internal string Id { get; }
		/// <summary>What refusal and readiness text calls this backend. Kept as the historical
		/// DesktopClrV4/CoreClr spelling: it is in operator-visible readiness output.</summary>
		internal string Name { get; }
		/// <summary>The payload matrix's runtime family token: <c>clrv4</c>, <c>coreclr</c>, or <c>mono</c>.</summary>
		internal string Family { get; }
		internal HookLabArrival Arrival { get; }
		/// <summary>Every runtime GUID this backend answers for. Usually one. Mono has two, because dnSpy
		/// reports the same soft-debugger engine as "MonoCLR" or "Unity" depending on the target - one
		/// <c>DbgEngineImpl</c>, two names - and a resident cannot tell the difference either.</summary>
		internal Guid[] RuntimeGuids { get; }
		/// <summary>Empty when the runtime GUID alone identifies the runtime. CLR v4 needs the name too,
		/// because the same GUID also covers CLR v2.</summary>
		internal string RuntimeNamePrefix { get; }
		/// <summary>Lower wins when a process has loaded more than one supported runtime. Declared rather
		/// than left to the order of a chain of ifs, so "which backend does a mixed process get" has an
		/// answer that is written down and can be tested.</summary>
		internal int Priority { get; }
		/// <summary>The runtime id when it is a constant, or null when it has to be read from the live
		/// target because the runtime has many versions.</summary>
		internal string? FixedRuntimeId { get; }
		/// <summary>Whether the debugger must be re-synchronised after the payload arrives.</summary>
		internal bool SynchronizesAfterArrival { get; }
		internal string PatchEnginePayloadId { get; }
		internal string[] CompilerPayloadIds { get; }
		internal int Bitness => 64;
		internal string Architecture => "X64";

		internal bool Matches(HookLabRuntimeIdentity runtime) =>
			RuntimeGuids.Contains(runtime.Guid) &&
			(RuntimeNamePrefix.Length==0 || runtime.Name.StartsWith(RuntimeNamePrefix,StringComparison.OrdinalIgnoreCase));

		public override string ToString()=>Name;
	}

	/// <summary>The complete, concrete set of supported backends. Two entries, listed by hand: this is a
	/// table of what has been proved to work, not a provider ecosystem, and adding a row is meant to
	/// require the evidence that a new row implies.</summary>
	static class HookLabBackends {
		static readonly Guid DotNetFrameworkRuntimeGuid=new Guid("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");
		static readonly Guid CoreClrRuntimeGuid=new Guid("E0B4EB52-D1D9-42AB-B130-028CA31CF9F6");
		/// <summary>The two names dnSpy gives one runtime. <c>DbgEngineImpl</c> in
		/// dnSpy.Debugger.DotNet.Mono reports "MonoCLR" or "Unity" from the same soft-debugger engine
		/// depending on the target, so both belong to one backend rather than two - and a resident, which
		/// only ever sees <c>Mono.Runtime</c>, could not tell them apart to honour a split anyway.</summary>
		static readonly Guid MonoRuntimeGuid=new Guid("7A99738E-9A75-4268-9B74-BBA174764FC7");
		static readonly Guid UnityRuntimeGuid=new Guid("CE8A11EE-73EF-4A51-B5D0-BDA2E665A2B4");

		internal static readonly HookLabBackend DesktopClrV4=new HookLabBackend(
			id:"clrv4-x64",name:"DesktopClrV4",family:"clrv4",arrival:HookLabArrival.NativeBootstrap,
			runtimeGuids:new[]{DotNetFrameworkRuntimeGuid},runtimeNamePrefix:"CLR v4.",priority:0,
			fixedRuntimeId:"v4.0.30319",synchronizesAfterArrival:false,
			patchEnginePayloadId:"Harmony.Desktop",compilerPayloadIds:new[]{"Microsoft.CodeAnalysis","Microsoft.CodeAnalysis.CSharp"});

		internal static readonly HookLabBackend CoreClr=new HookLabBackend(
			id:"coreclr-x64",name:"CoreClr",family:"coreclr",arrival:HookLabArrival.DebuggerEvaluation,
			runtimeGuids:new[]{CoreClrRuntimeGuid},runtimeNamePrefix:"",priority:1,
			// CoreCLR ships many versions and the exact one is part of the target's identity, so it is read
			// from the live process rather than assumed.
			fixedRuntimeId:null,synchronizesAfterArrival:true,
			patchEnginePayloadId:"Harmony.CoreClr",compilerPayloadIds:new[]{"Microsoft.CodeAnalysis","Microsoft.CodeAnalysis.CSharp"});

		/// <summary>Mono, whether standalone or embedded in a Unity player. It arrives by debugger
		/// evaluation like CoreCLR, because there is no native bootstrap for Mono and none is needed - the
		/// soft debugger can already run an expression in the target. It uses the CLR v4 Harmony, because
		/// Mono is a net48-era runtime, and the Roslyn compiler slots, because Mono's CodeDom shells out to
		/// an <c>mcs</c> that is not there to shell to.
		///
		/// <para>One row for both, because it is one runtime: mono-project's Mono 6.12 and the Mono 6.13 a
		/// Unity 2021.3 player embeds answer identically on everything this row decides - the missing
		/// identity APIs, the native DACL, the compiler, the teardown. Measured on both, separately.</para>
		///
		/// <para>Last in priority: a Mono process exposes exactly one runtime, so the ordering only matters
		/// for a hypothetical mixed process, and in one of those the runtime with a native bootstrap and the
		/// longest evidence should win.</para></summary>
		internal static readonly HookLabBackend Mono=new HookLabBackend(
			id:"mono-x64",name:"Mono",family:"mono",arrival:HookLabArrival.DebuggerEvaluation,
			runtimeGuids:new[]{MonoRuntimeGuid,UnityRuntimeGuid},runtimeNamePrefix:"",priority:2,
			// A constant, and the CLR v4 one. This is not a copy-paste: runtime_id is guarded against what
			// the target computes for itself, and Mono's RuntimeEnvironment.GetSystemVersion() returns
			// "v4.0.30319" - measured on Mono 6.12 and on Unity 2021.3's 6.13. Reading it from the live
			// process the way CoreCLR does is not an option either, because that derivation finds the loaded
			// coreclr.dll and a Mono target has none. The Mono build version is a different fact, not
			// available here, and the compatibility probe is where it is pinned to a proved range.
			fixedRuntimeId:"v4.0.30319",synchronizesAfterArrival:false,
			patchEnginePayloadId:"Harmony.Desktop",compilerPayloadIds:new[]{"Microsoft.CodeAnalysis","Microsoft.CodeAnalysis.CSharp"});

		internal static readonly HookLabBackend[] All={ DesktopClrV4,CoreClr };

		/// <summary>Backends whose payload, compiler and resident behaviour are proved but whose arrival is
		/// not, with the reason each is still refused. They are described here rather than deleted because
		/// everything except arrival is real and tested - and because a refusal that names the missing piece
		/// is worth more than one that says "unsupported runtime".</summary>
		internal static readonly (HookLabBackend Backend,string Reason)[] Pending={
			(Mono,
			 "HookLab on Mono is not available yet: the resident arrives through one debugger evaluation, "+
			 "and placing that evaluation needs an owned internal breakpoint, which only the CorDebug engine "+
			 "implements. Everything after arrival is proved on Mono - payload set, Roslyn compilation, "+
			 "the patch engine, the control endpoint, and retirement - by the compatibility probe's mono leg."),
		};

		/// <summary>Every backend whose runtime this process has loaded, in priority order. Exposed so a
		/// test can assert what a mixed-runtime process matches, rather than only what it is given.</summary>
		internal static HookLabBackend[] Candidates(int bitness,string architecture,IEnumerable<HookLabRuntimeIdentity> runtimes) {
			if(runtimes is null) throw new ArgumentNullException(nameof(runtimes));
			if(bitness!=64 || !String.Equals(architecture,"X64",StringComparison.OrdinalIgnoreCase)) return Array.Empty<HookLabBackend>();
			var attached=runtimes.ToArray();
			return All.Where(backend=>attached.Any(backend.Matches)).OrderBy(backend=>backend.Priority).ToArray();
		}

		/// <summary>The one backend for this target, or null. Zero candidates is a refusal; more than one
		/// is resolved by declared priority rather than by the order of a chain of ifs - a mixed process
		/// that has loaded both runtimes really does exist, and it gets CLR v4.</summary>
		internal static HookLabBackend? Select(int bitness,string architecture,IEnumerable<HookLabRuntimeIdentity> runtimes) =>
			Candidates(bitness,architecture,runtimes).FirstOrDefault();

		internal static string? UnsupportedReason(int bitness,string architecture,IEnumerable<HookLabRuntimeIdentity> runtimes) {
			if(runtimes is null) throw new ArgumentNullException(nameof(runtimes));
			if(bitness!=64 || !String.Equals(architecture,"X64",StringComparison.OrdinalIgnoreCase))
				return "HookLab currently supports x64 desktop CLR v4 and CoreCLR targets; the attached process architecture is "+architecture+" ("+bitness+"-bit).";
			var attached=runtimes.ToArray();
			if(Select(bitness,architecture,attached) is not null) return null;
			// A runtime we know and cannot yet reach says which piece is missing. "The attached process
			// exposes Unity" is true and useless; the operator's next question is always why.
			foreach(var pending in Pending)
				if(attached.Any(pending.Backend.Matches)) return pending.Reason;
			var names=attached.Select(runtime=>String.IsNullOrWhiteSpace(runtime.Name)?runtime.Guid.ToString("D"):runtime.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			return "HookLab currently supports x64 desktop CLR v4 and CoreCLR targets; the attached process exposes "+(names.Length==0?"no managed runtime":String.Join(", ",names))+".";
		}
	}
}
