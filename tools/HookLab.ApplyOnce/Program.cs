namespace HookLab.ApplyOnce;

internal static class Program {
	public static int Main(string[] arguments) {
		if(!Arguments.TryParse(arguments,out var options)) {
			Console.Error.WriteLine("Usage: HookLab.ApplyOnce.exe --pid <pid> --definition <hook.json> [--payload-dir <directory>]");
			return 2;
		}
		try {
			var definitionPath=Path.GetFullPath(options.DefinitionPath!);
			var definition=HookDefinition.Load(definitionPath);
			var result=OneShotInjector.Apply(options.ProcessId,definition,definitionPath,options.PayloadDirectory);
			Console.Write(result);
			return 0;
		}
		catch(Exception ex) {
			var result="status=error\nprocess_id="+options.ProcessId+"\nmessage="+Sanitize(ex.Message)+"\n";
			OneShotInjector.WriteResult(options.ProcessId,result);
			Console.Error.WriteLine("HookLab.ApplyOnce failed: "+ex.Message);
			return 1;
		}
	}

	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed record Arguments(int ProcessId,string? DefinitionPath,string? PayloadDirectory) {
	public static bool TryParse(string[] values,out Arguments result) {
		result=new Arguments(0,null,null);
		if(values.Length is not (4 or 6)) return false;
		int? processId=null; string? definition=null, payload=null;
		for(var index=0;index<values.Length;index+=2) {
			if(values[index]=="--pid" && processId is null && Int32.TryParse(values[index+1],out var parsed) && parsed>0) processId=parsed;
			else if(values[index]=="--definition" && definition is null) definition=values[index+1];
			else if(values[index]=="--payload-dir" && payload is null) payload=values[index+1];
			else return false;
		}
		if(processId is null || String.IsNullOrWhiteSpace(definition)) return false;
		result=new Arguments(processId.Value,definition,payload is null?null:Path.GetFullPath(payload));
		return true;
	}
}
