using System;
using System.IO;

namespace HookLab.Bootstrap {
	/// <summary>Fixed entry point used by the autonomous x64 initializer.</summary>
	public static class NativeEntry {
		public static int Initialize(string parameterFile) {
			try {
				var parameters=File.ReadAllText(parameterFile);
				var prepared=HookLabBootstrap.Prepare(parameters);
				if(!prepared.StartsWith("status=ok\n",StringComparison.Ordinal)) return 2;
				var committed=HookLabBootstrap.Commit();
				return committed.StartsWith("status=ok\n",StringComparison.Ordinal) ? 0 : 3;
			}
			catch { return 1; }
		}
	}
}
