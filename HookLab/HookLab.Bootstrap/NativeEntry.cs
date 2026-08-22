using System;
using System.IO;

namespace HookLab.Bootstrap {
	/// <summary>Fixed entry point used by the autonomous x64 initializer.</summary>
	public static class NativeEntry {
		public static int Initialize(string parameterFile) {
			string parameters="";
			try {
				parameters=File.ReadAllText(parameterFile);
				var prepared=HookLabBootstrap.Prepare(parameters);
				if(!prepared.StartsWith("status=ok\n",StringComparison.Ordinal)) { CompletionReportPublisher.TryPublish(parameters,prepared); return 2; }
				var committed=HookLabBootstrap.Commit();
				if(!committed.StartsWith("status=ok\n",StringComparison.Ordinal)) { CompletionReportPublisher.TryPublish(parameters,committed); return 3; }
				return 0;
			}
			catch(Exception ex) { CompletionReportPublisher.TryPublish(parameters,CompletionReportPublisher.ExceptionReport(ex)); return 1; }
		}
	}
}
