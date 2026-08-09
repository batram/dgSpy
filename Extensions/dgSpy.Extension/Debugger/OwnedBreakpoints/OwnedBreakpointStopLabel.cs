using System;

namespace dgSpy.Extension.Debugger.OwnedBreakpoints {
	/// <summary>Pure precedence rule shared by live event routing and focused tests.</summary>
	public static class OwnedBreakpointStopLabel {
		/// <summary>The T07/T08 shared stop-attribution vocabulary.</summary>
		public const string Prefix="owned_breakpoint:";
		public static string? Select(bool userVisibleStop,Guid? ownerToken) =>
			userVisibleStop || !ownerToken.HasValue ? null : Prefix+ownerToken.Value.ToString("N");
	}
}
