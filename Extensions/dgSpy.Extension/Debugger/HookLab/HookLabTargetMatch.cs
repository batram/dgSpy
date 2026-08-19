using System;
using System.IO;
using HookLab.Contracts;

namespace dgSpy.Extension {
	/// <summary>
	/// Whether a recorded HookLab resident describes the target in front of us.
	///
	/// This lives in its own file, away from the RPC surface, because it answers two questions that are
	/// easy to conflate and expensive to get wrong, and because a rule this consequential should be
	/// reachable from a test. It previously was not: it sat as a private static inside a nested class,
	/// where nothing could construct it.
	///
	/// <para><see cref="SameProcess"/> asks "is this record still about this exact live process". It
	/// deliberately ignores the application domain. <c>ProbeDiscoveryStore.DiscoverCore</c> treats a
	/// record it is told is not current as either garbage to delete or an integrity failure to
	/// quarantine, so answering false for a valid resident that merely lives in another domain would
	/// destroy it - and preserving unknown and foreign-owned residents is an invariant of the product,
	/// not a nicety.</para>
	///
	/// <para><see cref="SameTarget"/> asks "is this record the resident I am operating on", and does
	/// consider the domain. Without that, an operation aimed at one application domain would adopt the
	/// resident belonging to another and install hooks that can never bind.</para>
	/// </summary>
	static class HookLabTargetMatch {
		/// <summary>Written by an owner we recognise: our own resident host, or ApplyOnce.</summary>
		public static bool OwnedBy(TargetIdentity identity,string residentHostId)=>
			identity.HostId==residentHostId||identity.HostId=="apply-once";

		/// <summary>Same live process, whatever application domain the resident lives in.</summary>
		public static bool SameProcess(TargetIdentity left,TargetIdentity right,string residentHostId)=>
			OwnedBy(left,residentHostId)&&
			left.ProcessId==right.ProcessId&&
			left.ProcessCreationTimeUtc.ToUniversalTime().Ticks==right.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&
			String.Equals(Path.GetFullPath(left.ImagePath),Path.GetFullPath(right.ImagePath),StringComparison.OrdinalIgnoreCase)&&
			left.Architecture==right.Architecture&&
			left.RuntimeId==right.RuntimeId;

		/// <summary>Same live process and the same application domain.</summary>
		public static bool SameTarget(TargetIdentity left,TargetIdentity right,string residentHostId)=>
			SameProcess(left,right,residentHostId)&&left.AppDomainId==right.AppDomainId;
	}
}
