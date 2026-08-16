using System;
using System.ComponentModel;

namespace dnSpy.Debugger.Attach {
	static class ProcessNameCandidate {
		public static bool TryReadExecutableName(Func<string?> read,out string name) {
			if (read is null) throw new ArgumentNullException(nameof(read));
			try { name=read() ?? string.Empty; return true; }
			catch (Win32Exception) { }
			catch (InvalidOperationException) { }
			catch (ArgumentException) { }
			name=string.Empty;
			return false;
		}
	}
}
