using System;

namespace AppDomainProof.Embedded {
	/// <summary>
	/// Stands in for HookLab.Contracts and HookLab.Probe.CorDebug: an assembly the payload byte-loads
	/// after it is itself running, rather than one the CLR ever binds from disk. Product HookLab reports
	/// these as <c>is_in_memory: true</c>, and that nesting - a byte-loaded assembly byte-loading more -
	/// is the pattern that has to survive being run in a secondary AppDomain.
	/// </summary>
	public static class EmbeddedMarker {
		public static string Describe()=>"embedded in domain "+AppDomain.CurrentDomain.Id.ToString()+" ("+AppDomain.CurrentDomain.FriendlyName+")";
	}
}
