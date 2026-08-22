using System;
using System.IO;
using System.Text;

namespace HookLab.Bootstrap {
	/// <summary>Single-assignment publication for failures that happen before the resident worker owns
	/// completion.txt. It deliberately accepts the raw parameter text so even parameter-parse failures
	/// can report through the one path the controller is already waiting on.</summary>
	static class CompletionReportPublisher {
		internal static void TryPublish(string parameters,string report) {
			try {
				var completion=Value(parameters,"completion_path"); if(String.IsNullOrWhiteSpace(completion)||File.Exists(completion)) return;
				var temporary=completion+".native-entry.tmp-"+Guid.NewGuid().ToString("N");
				try { File.WriteAllText(temporary,report,new UTF8Encoding(false)); if(!File.Exists(completion)) File.Move(temporary,completion); }
				finally { if(File.Exists(temporary)) File.Delete(temporary); }
			}
			catch { }
		}
		internal static string ExceptionReport(Exception ex)=>"status=error\nstage=native_entry\nerror_type="+Sanitize(ex.GetType().FullName??"Exception")+"\nerror_message="+Sanitize(ex.Message)+"\n";
		static string? Value(string text,string key) { foreach(var line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) { var split=line.IndexOf('='); if(split>0&&line.Substring(0,split)==key) return line.Substring(split+1); } return null; }
		static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ').Trim();
	}
}
