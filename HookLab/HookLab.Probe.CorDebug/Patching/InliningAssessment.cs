using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class InliningAssessment {
		internal InliningAssessment(bool interceptionMayBeIncomplete, string reason, IReadOnlyList<MethodBase> validationCallers) {
			InterceptionMayBeIncomplete = interceptionMayBeIncomplete; Reason = reason; ValidationCallers = validationCallers;
		}
		public bool InterceptionMayBeIncomplete { get; }
		public string Reason { get; }
		public IReadOnlyList<MethodBase> ValidationCallers { get; }
	}

	public static class InliningInspector {
		/// <summary>Reports risk; callers are explicit because the CLR exposes no complete caller inventory.</summary>
		public static InliningAssessment Assess(MethodBase target, IEnumerable<MethodBase>? knownCallers = null) {
			if (target == null) throw new ArgumentNullException(nameof(target));
			var flags = target.GetMethodImplementationFlags();
			var prevented = (flags & MethodImplAttributes.NoInlining) != 0;
			var aggressive = (flags & MethodImplAttributes.AggressiveInlining) != 0;
			var callers = (knownCallers ?? Array.Empty<MethodBase>()).Where(x => x != null).Distinct().ToArray();
			var reason = prevented
				? "The method declares NoInlining, but already-compiled or runtime-specific paths still require validation."
				: aggressive ? "The method requests AggressiveInlining; interception is likely incomplete for compiled callers."
				: "The CLR may inline this method; interception cannot be claimed complete. Validate at known callers.";
			return new InliningAssessment(!prevented || callers.Length != 0, reason, callers);
		}
	}
}
