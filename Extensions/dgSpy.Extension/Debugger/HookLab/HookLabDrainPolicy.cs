using System;
using System.Collections.Generic;
using System.Globalization;

namespace dgSpy.Extension {
	/// <summary>Small, testable policy for draining the probe's bounded 1024-event queue after unpatching.</summary>
	static class HookLabDrainPolicy {
		public const int PageSize=256;
		public const int MaximumPages=5;

		public static int EventCount(IReadOnlyDictionary<string,string> report) {
			if(report==null) throw new ArgumentNullException(nameof(report));
			var count=0;
			while(report.ContainsKey("event_"+count.ToString(CultureInfo.InvariantCulture))) count++;
			return count;
		}

		public static bool NeedsAnotherPage(IReadOnlyDictionary<string,string> report,int pagesRead) =>
			pagesRead<MaximumPages && EventCount(report)==PageSize;
	}
}
