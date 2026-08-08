using System;
using System.Collections.Generic;
using System.Linq;

namespace dgSpy.Extension {
	static class BreakpointSnapPolicy {
		/// <summary>Returns the first legal offset at or after the request, falling back to the
		/// last legal offset before it when the request lies beyond the method's final sequence point.</summary>
		public static uint? Select(uint requested,IEnumerable<uint> sequencePoints) {
			var ordered=sequencePoints.Distinct().OrderBy(offset=>offset).ToArray();
			if (ordered.Length==0) return null;
			foreach (var offset in ordered) if (offset>=requested) return offset;
			return ordered[ordered.Length-1];
		}
	}
}
