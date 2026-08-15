using System.Diagnostics;
using System.Text.Json;

namespace HookLab.Watcher;

internal static class Program {
	public static async Task<int> Main(string[] arguments) {
		if(arguments.Length==2&&arguments[0]=="validate-package") { try { var package=PackageLoader.Load(arguments[1]); Console.WriteLine(JsonSerializer.Serialize(new { status="valid",packageId=package.PackageId,packageDigest=package.Digest,definitionId=package.Definition.Id })); return 0; } catch(Exception ex) { Console.Error.WriteLine("HookLab package invalid: "+ex.Message); return 1; } }
		if(!WatchOptions.TryParse(arguments,out var options)) { Console.Error.WriteLine("Usage: HookLab.Watcher.exe validate-package <directory> | run (--profiles <directory> | --definitions <directory>) [--poll-ms <25-5000>] [--max-parallel <1-32>] [--payload-dir <directory>] [--audit <jsonl>]"); return 2; }
		try {
			var definitions=options.ProfilesDirectory is null?DefinitionCatalog.Load(options.DefinitionsDirectory!):ProfileCatalog.Load(options.ProfilesDirectory);
			using var current=Process.GetCurrentProcess(); using var audit=new AuditWriter(options.AuditPath!); using var cancellation=new CancellationTokenSource();
			Console.CancelKeyPress+=(sender,eventArguments)=>{ eventArguments.Cancel=true; cancellation.Cancel(); };
			Console.WriteLine("HookLab watcher loaded "+definitions.Count+" definition(s) for session "+current.SessionId+".");
			await new WatchRunner(definitions,current.SessionId,options.PollMilliseconds,options.MaximumParallel,audit,options.PayloadDirectory).RunAsync(cancellation.Token);
			return 0;
		}
		catch(Exception ex) { Console.Error.WriteLine("HookLab watcher failed: "+ex.Message); return 1; }
	}
}

internal sealed record WatchOptions(string? DefinitionsDirectory,string? ProfilesDirectory,int PollMilliseconds,int MaximumParallel,string? PayloadDirectory,string? AuditPath) {
	public static bool TryParse(string[] values,out WatchOptions result) {
		result=new(null,null,100,4,null,null); if(values.Length<3||values[0]!="run"||(values.Length-1)%2!=0) return false;
		string? definitions=null,profiles=null,payload=null,audit=null; var poll=100; var parallel=4;
		for(var index=1;index<values.Length;index+=2) {
			var value=values[index+1];
			if(values[index]=="--definitions"&&definitions is null) definitions=value;
			else if(values[index]=="--profiles"&&profiles is null) profiles=value;
			else if(values[index]=="--poll-ms"&&Int32.TryParse(value,out var parsedPoll)&&parsedPoll is >=25 and <=5000) poll=parsedPoll;
			else if(values[index]=="--max-parallel"&&Int32.TryParse(value,out var parsedParallel)&&parsedParallel is >=1 and <=32) parallel=parsedParallel;
			else if(values[index]=="--payload-dir"&&payload is null) payload=Path.GetFullPath(value);
			else if(values[index]=="--audit"&&audit is null) audit=Path.GetFullPath(value);
			else return false;
		}
		if(String.IsNullOrWhiteSpace(definitions)==String.IsNullOrWhiteSpace(profiles)) return false;
		result=new(definitions is null?null:Path.GetFullPath(definitions),profiles is null?null:Path.GetFullPath(profiles),poll,parallel,payload,audit??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-audit.jsonl")); return true;
	}
}
