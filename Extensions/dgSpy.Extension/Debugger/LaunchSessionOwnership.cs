using System;
using System.Collections.Generic;
using System.Linq;

namespace dgSpy.Extension {
	static class LaunchSessionOwnership {
		public static bool CanAdopt(string programId,IEnumerable<string> ownedProgramIds,IEnumerable<string> liveProcessFilenames) {
			if (!ownedProgramIds.Contains(programId,StringComparer.OrdinalIgnoreCase)) return false;
			var filename=Filename(programId);
			return filename is not null && liveProcessFilenames.Contains(filename,StringComparer.OrdinalIgnoreCase);
		}
		static string? Filename(string programId) {
			if (!programId.StartsWith("launch:",StringComparison.OrdinalIgnoreCase)) return null;
			var separator=programId.IndexOf(':',"launch:".Length);
			return separator<0 || separator==programId.Length-1 ? null : programId.Substring(separator+1);
		}
	}
}
