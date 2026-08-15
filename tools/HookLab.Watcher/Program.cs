using System.Diagnostics;
using System.Text.Json;
using System.ComponentModel;
using HookLab.Injector;

namespace HookLab.Watcher;

internal static class Program {
	static readonly JsonSerializerOptions JsonOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
	public static async Task<int> Main(string[] arguments) {
		if(arguments.Length==2&&arguments[0]=="validate-package") return ValidatePackage(arguments[1]);
		if(CommandOptions.TryParseApply(arguments,out var apply)) return ApplyPackage(apply);
		if(CommandOptions.TryParseStatus(arguments,out var status)) return ShowStatus(status);
		if(ControlOptions.TryParse(arguments,out var control)) return UpdateControl(control);
		if(arguments.Length>0&&arguments[0] is "apply" or "status" or "validate-package" or "pause" or "resume" or "disable-profile" or "enable-profile") { WriteJson(new { status="error",code="invalid_arguments",message="Command arguments are invalid." }); return CommandExitCodes.InvalidArguments; }
		if(!WatchOptions.TryParse(arguments,out var options)) { Console.Error.WriteLine("Usage: HookLab.Watcher.exe validate-package <directory> | apply --package <directory> --pid <pid> [--payload-dir <directory>] | status [--pid <pid>] | pause | resume | disable-profile <id> | enable-profile <id> | run (--profiles <directory> | --definitions <directory>) [--poll-ms <25-5000>] [--max-parallel <1-32>] [--payload-dir <directory>] [--audit <jsonl>]"); return 2; }
		try {
			IWatchCatalog catalog=options.ProfilesDirectory is null?new StaticWatchCatalog(DefinitionCatalog.Load(options.DefinitionsDirectory!)):new ReloadingProfileCatalog(options.ProfilesDirectory);
			var definitions=catalog.Current().Definitions; var controlStore=new WatchControlStore(); var statusStore=new WatcherStatusStore();
			using var current=Process.GetCurrentProcess(); using var audit=new AuditWriter(options.AuditPath!); using var cancellation=new CancellationTokenSource();
			Console.CancelKeyPress+=(sender,eventArguments)=>{ eventArguments.Cancel=true; cancellation.Cancel(); };
			Console.WriteLine("HookLab watcher loaded "+definitions.Count+" definition(s) for session "+current.SessionId+".");
			await new WatchRunner(catalog,current.SessionId,options.PollMilliseconds,options.MaximumParallel,audit,options.PayloadDirectory,control:controlStore,statusStore:statusStore).RunAsync(cancellation.Token);
			return 0;
		}
		catch(Exception ex) { Console.Error.WriteLine("HookLab watcher failed: "+ex.Message); return 1; }
	}
	static int ValidatePackage(string path) { try { var package=PackageLoader.Load(path); WriteJson(new { status="valid",packageId=package.PackageId,packageDigest=package.Digest,definitionId=package.Definition.Id }); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int ApplyPackage(CommandOptions options) {
		try {
			var package=PackageLoader.Load(options.PackagePath!); using var process=Process.GetProcessById(options.ProcessId!.Value); var creation=process.StartTime.ToUniversalTime().Ticks;
			var result=new ResidentCoordinator(options.PayloadDirectory).Apply(new(options.ProcessId.Value,creation,package.ManifestPath,package.Digest,package.Definition)); WriteJson(result); return CommandExitCodes.Success;
		}
		catch(Exception ex) { return WriteFailure(ex); }
	}
	static int ShowStatus(CommandOptions options) { try { var result=new ResidentCoordinator(null).Status(options.ProcessId); WriteJson(new { status="ok",watcher=WatcherStatusStore.Read(),residents=result }); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int UpdateControl(ControlOptions options) { try { var store=new WatchControlStore(); var value=store.Update(options.Paused,options.EnableProfile,options.DisableProfile); WriteJson(new { status="ok",paused=value.Paused,disabledProfiles=value.DisabledProfiles.OrderBy(id=>id,StringComparer.Ordinal) }); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int WriteFailure(Exception ex) { var failure=CommandFailure.Classify(ex); WriteJson(new { status="error",code=failure.Code,message=Sanitize(ex.Message) }); return failure.ExitCode; }
	static void WriteJson(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,JsonOptions));
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed record ControlOptions(bool? Paused,string? EnableProfile,string? DisableProfile) {
	public static bool TryParse(string[] values,out ControlOptions result) { result=new(null,null,null); if(values.Length==1&&values[0] is "pause" or "resume") { result=new(values[0]=="pause",null,null); return true; } if(values.Length==2&&values[0] is "enable-profile" or "disable-profile"&&!String.IsNullOrWhiteSpace(values[1])) { result=new(null,values[0]=="enable-profile"?values[1]:null,values[0]=="disable-profile"?values[1]:null); return true; } return false; }
}

internal static class CommandExitCodes { public const int Success=0,InvalidArguments=2,InvalidInput=3,NotFound=4,AccessDenied=5,TargetExited=6,Conflict=7,Timeout=8,OperationFailed=9; }
internal sealed record CommandFailure(string Code,int ExitCode) {
	public static CommandFailure Classify(Exception exception) {
		var chain=Chain(exception).ToArray();
		if(chain.Any(value=>value is InvalidDataException or DirectoryNotFoundException or FileNotFoundException)) return new("invalid_input",CommandExitCodes.InvalidInput);
		if(chain.Any(value=>value is UnauthorizedAccessException||value is Win32Exception win32&&win32.NativeErrorCode==5)) return new("access_denied",CommandExitCodes.AccessDenied);
		if(chain.Any(value=>value is TargetExitedException)||exception is ArgumentException&&exception.Message.Contains("process",StringComparison.OrdinalIgnoreCase)) return new("target_exited",CommandExitCodes.TargetExited);
		if(chain.Any(value=>value is TimeoutException)) return new("timeout_known_state_required",CommandExitCodes.Timeout);
		if(exception is InvalidOperationException&&exception.Message.StartsWith("No authenticated HookLab resident",StringComparison.Ordinal)) return new("not_found",CommandExitCodes.NotFound);
		if(exception is InvalidOperationException&&(exception.Message.Contains("conflict",StringComparison.OrdinalIgnoreCase)||exception.Message.Contains("refusing",StringComparison.OrdinalIgnoreCase)||exception.Message.Contains("different source",StringComparison.OrdinalIgnoreCase)||exception.Message.Contains("newer than desired",StringComparison.OrdinalIgnoreCase))) return new("deterministic_conflict",CommandExitCodes.Conflict);
		return new("operation_failed",CommandExitCodes.OperationFailed);
	}
	static IEnumerable<Exception> Chain(Exception exception) { for(Exception? current=exception;current is not null;current=current.InnerException) yield return current; }
}

internal sealed record CommandOptions(string? PackagePath,int? ProcessId,string? PayloadDirectory) {
	public static bool TryParseApply(string[] values,out CommandOptions result) { result=new(null,null,null); if(values.Length is not (5 or 7)||values[0]!="apply") return false; string? package=null,payload=null; int? process=null; for(var index=1;index<values.Length;index+=2) { if(values[index]=="--package"&&package is null) package=values[index+1]; else if(values[index]=="--pid"&&process is null&&Int32.TryParse(values[index+1],out var parsed)&&parsed>0) process=parsed; else if(values[index]=="--payload-dir"&&payload is null) payload=values[index+1]; else return false; } if(package is null||process is null) return false; result=new(Path.GetFullPath(package),process,payload is null?null:Path.GetFullPath(payload)); return true; }
	public static bool TryParseStatus(string[] values,out CommandOptions result) { result=new(null,null,null); if(values.Length==1&&values[0]=="status") return true; if(values.Length==3&&values[0]=="status"&&values[1]=="--pid"&&Int32.TryParse(values[2],out var process)&&process>0) { result=new(null,process,null); return true; } return false; }
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
