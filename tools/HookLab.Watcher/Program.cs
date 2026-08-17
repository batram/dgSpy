using System.Diagnostics;
using System.Text.Json;
using System.ComponentModel;
using HookLab.Injector;

namespace HookLab.Watcher;

internal static class Program {
	static readonly JsonSerializerOptions JsonOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
	public static async Task<int> Main(string[] arguments) {
		if(TryParseSupervisor(arguments,out var supervisorState)) return RunSupervisor(supervisorState);
		if(arguments.Length==1&&arguments[0]=="run-installed") arguments=InstalledRunArguments();
		if(InstallCommand.TryParse(arguments,out var install)) return RunInstall(install);
		if(EnrollmentCommand.TryParse(arguments,out var enrollment)) return RunEnrollment(enrollment.Options);
		if(arguments.Length==2&&arguments[0]=="validate-package") return ValidatePackage(arguments[1]);
		if(CommandOptions.TryParseApply(arguments,out var apply)) return ApplyPackage(apply);
		if(CommandOptions.TryParseStatus(arguments,out var status)) return ShowStatus(status);
		if(ControlOptions.TryParse(arguments,out var control)) return UpdateControl(control);
		if(arguments.Length>0&&arguments[0] is "apply" or "status" or "validate-package" or "pause" or "resume" or "disable-profile" or "enable-profile" or "enroll" or "install" or "verify-install" or "uninstall" or "run-installed" or "supervise") { WriteJson(new { status="error",code="invalid_arguments",message="Command arguments are invalid." }); return CommandExitCodes.InvalidArguments; }
		if(!WatchOptions.TryParse(arguments,out var options)) { Console.Error.WriteLine("Usage: HookLab.Watcher.exe install [--no-task] | verify-install | uninstall [--keep-state] | enroll --deployment <directory> [--replace] [--enrollment-root <directory>] [--profiles-root <directory>] [--state-root <directory>] | validate-package <directory> | apply --package <directory> --pid <pid> [--payload-dir <directory>] | status [--pid <pid>] | pause|resume [--state-root <directory>] | disable-profile|enable-profile <id> [--state-root <directory>] | supervise | run-installed | run (--profiles <directory> | --definitions <directory>) [--additional-profiles <directory>] [--state-root <directory>] [--poll-ms <25-5000>] [--max-parallel <1-32>] [--payload-dir <directory>] [--audit <jsonl>]"); return 2; }
		try {
			IWatchCatalog catalog=options.ProfilesDirectory is null?new StaticWatchCatalog(DefinitionCatalog.Load(options.DefinitionsDirectory!)):options.AdditionalProfilesDirectory is null?new ReloadingProfileCatalog(options.ProfilesDirectory):new CompositeWatchCatalog(new ReloadingProfileCatalog(options.ProfilesDirectory),new ReloadingProfileCatalog(options.AdditionalProfilesDirectory,true));
			var definitions=catalog.Current().Definitions; var controlStore=new WatchControlStore(options.StateRoot is null?null:Path.Combine(options.StateRoot,"watcher-control.json")); var statusStore=new WatcherStatusStore(options.StateRoot is null?null:Path.Combine(options.StateRoot,"watcher-status.json"));
			using var current=Process.GetCurrentProcess(); using var audit=new AuditWriter(options.AuditPath!); using var cancellation=new CancellationTokenSource();
			Console.CancelKeyPress+=(sender,eventArguments)=>{ eventArguments.Cancel=true; cancellation.Cancel(); };
			Console.WriteLine("HookLab watcher loaded "+definitions.Count+" definition(s) for session "+current.SessionId+".");
			await new WatchRunner(catalog,current.SessionId,options.PollMilliseconds,options.MaximumParallel,audit,options.PayloadDirectory,control:controlStore,statusStore:statusStore,processStarts:WmiProcessStartSignal.Create(),residentStateRoot:HookLab.Host.Transport.Discovery.DgSpyStateRoot.SharedResidentRoot()).RunAsync(cancellation.Token);
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
	static int ShowStatus(CommandOptions options) { try { var result=new ResidentCoordinator(null,HookLab.Host.Transport.Discovery.DgSpyStateRoot.SharedResidentRoot()).Status(options.ProcessId); WriteJson(new { status="ok",watcher=WatcherStatusStore.Read(),residents=result }); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int UpdateControl(ControlOptions options) { try { var store=new WatchControlStore(options.ControlPath); var value=store.Update(options.Paused,options.EnableProfile,options.DisableProfile); WriteJson(new { status="ok",paused=value.Paused,disabledProfiles=value.DisabledProfiles.OrderBy(id=>id,StringComparer.Ordinal) }); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int RunEnrollment(EnrollmentOptions options) { try { WriteJson(new WatcherEnrollmentService().Enroll(options)); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int RunInstall(InstallCommand command) { try { var installer=new WatcherInstaller(); var result=command.Action switch { "install"=>installer.Install(command.Options), "verify-install"=>installer.Verify(command.Options), "uninstall"=>installer.Uninstall(command.Options), _=>throw new InvalidOperationException() }; WriteJson(result); return CommandExitCodes.Success; } catch(Exception ex) { return WriteFailure(ex); } }
	static int RunSupervisor(string stateRoot) {
		HideOwnConsoleWindow();
		return WatcherSupervisor.Run(()=>RunInstalledChild(stateRoot),Thread.Sleep,message=>Console.Error.WriteLine(message));
	}
	static (int ExitCode,TimeSpan Uptime) RunInstalledChild(string stateRoot) {
		var executable=Environment.ProcessPath??throw new InvalidOperationException("Watcher executable path is unavailable.");
		var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true}; foreach(var argument in InstalledRunArguments(stateRoot)) info.ArgumentList.Add(argument);
		var stopwatch=Stopwatch.StartNew();
		using var process=Process.Start(info)??throw new InvalidOperationException("Could not start the installed watcher.");
		process.WaitForExit();
		return (process.ExitCode,stopwatch.Elapsed);
	}
	// The scheduled task launches this console executable directly; hide the logon console like the
	// former PowerShell shim's -WindowStyle Hidden did. The supervised child gets CreateNoWindow instead.
	static void HideOwnConsoleWindow() { var window=GetConsoleWindow(); if(window!=IntPtr.Zero) ShowWindow(window,0); }
	[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
	[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
	static bool TryParseSupervisor(string[] arguments,out string stateRoot) { stateRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab"); if(arguments.Length==1&&arguments[0]=="supervise") return true; if(arguments.Length==3&&arguments[0]=="supervise"&&arguments[1]=="--state-root"&&!String.IsNullOrWhiteSpace(arguments[2])) { stateRoot=Path.GetFullPath(arguments[2]); return true; } return false; }
	static string[] InstalledRunArguments(string? stateRoot=null) { var root=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); var state=Path.GetFullPath(stateRoot??Path.Combine(local,"HookLab")); var enrolled=Path.Combine(local,"Programs","HookLab.Watcher.Enrolled"); return new[]{"run","--profiles",Path.Combine(root,"deployments"),"--additional-profiles",enrolled,"--state-root",state,"--payload-dir",Path.Combine(root,"payload"),"--audit",Path.Combine(state,"watcher-audit.jsonl")}; }
	static int WriteFailure(Exception ex) { var failure=CommandFailure.Classify(ex); WriteJson(new { status="error",code=failure.Code,message=Sanitize(ex.Message) }); return failure.ExitCode; }
	static void WriteJson(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,JsonOptions));
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed record EnrollmentCommand(EnrollmentOptions Options) {
	public static bool TryParse(string[] values,out EnrollmentCommand result) {
		result=new(EnrollmentOptions.Defaults(".")); if(values.Length<3||values[0]!="enroll") return false; string? deployment=null,enrollment=null,profiles=null,state=null; var replace=false;
		for(var index=1;index<values.Length;index++) {
			if(values[index]=="--replace") { if(replace) return false; replace=true; continue; }
			if(values[index] is not ("--deployment" or "--enrollment-root" or "--profiles-root" or "--state-root")||++index>=values.Length) return false; var option=values[index-1]; var value=values[index]; if(option=="--deployment"&&deployment is null) deployment=value; else if(option=="--enrollment-root"&&enrollment is null) enrollment=value; else if(option=="--profiles-root"&&profiles is null) profiles=value; else if(option=="--state-root"&&state is null) state=value; else return false;
		}
		if(String.IsNullOrWhiteSpace(deployment)) return false; var control=state is null?null:Path.Combine(Path.GetFullPath(state),"watcher-control.json"); result=new(EnrollmentOptions.Defaults(deployment,enrollment,profiles,control,replace)); return true;
	}
}

internal sealed record InstallCommand(string Action,InstalledWatcherOptions Options) {
	public static bool TryParse(string[] values,out InstallCommand result) {
		result=new("",InstalledWatcherOptions.Defaults()); if(values.Length==0||values[0] is not ("install" or "verify-install" or "uninstall")) return false;
		string? source=null,root=null,state=null; var task=true; var keep=false;
		for(var index=1;index<values.Length;index++) { if(values[index] is "--source" or "--install-root" or "--state-root") { if(++index>=values.Length) return false; if(values[index-1]=="--source") source=values[index]; else if(values[index-1]=="--install-root") root=values[index]; else state=values[index]; } else if(values[index]=="--no-task"&&values[0]=="install") task=false; else if(values[index]=="--keep-state"&&values[0]=="uninstall") keep=true; else return false; }
		result=new(values[0],InstalledWatcherOptions.Defaults(source,root,state,task,keep)); return true;
	}
}

internal sealed record ControlOptions(bool? Paused,string? EnableProfile,string? DisableProfile,string? ControlPath) {
	public static bool TryParse(string[] values,out ControlOptions result) {
		result=new(null,null,null,null); string? stateRoot=null; if(values.Length>=3&&values[^2]=="--state-root"&&!String.IsNullOrWhiteSpace(values[^1])) { stateRoot=Path.GetFullPath(values[^1]); values=values[..^2]; } var path=stateRoot is null?null:Path.Combine(stateRoot,"watcher-control.json");
		if(values.Length==1&&values[0] is "pause" or "resume") { result=new(values[0]=="pause",null,null,path); return true; }
		if(values.Length==2&&values[0] is "enable-profile" or "disable-profile"&&!String.IsNullOrWhiteSpace(values[1])) { result=new(null,values[0]=="enable-profile"?values[1]:null,values[0]=="disable-profile"?values[1]:null,path); return true; }
		return false;
	}
}

internal static class CommandExitCodes { public const int Success=0,InvalidArguments=2,InvalidInput=3,NotFound=4,AccessDenied=5,TargetExited=6,Conflict=7,Timeout=8,OperationFailed=9; }
internal sealed record CommandFailure(string Code,int ExitCode) {
	public static CommandFailure Classify(Exception exception) {
		var chain=Chain(exception).ToArray();
		if(chain.Any(value=>value is InvalidDataException or DirectoryNotFoundException or FileNotFoundException)) return new("invalid_input",CommandExitCodes.InvalidInput);
		if(chain.Any(value=>value is UnauthorizedAccessException||value is Win32Exception win32&&win32.NativeErrorCode==5)) return new("access_denied",CommandExitCodes.AccessDenied);
		if(chain.Any(value=>value is TargetExitedException)||exception is ArgumentException&&exception.Message.Contains("process",StringComparison.OrdinalIgnoreCase)) return new("target_exited",CommandExitCodes.TargetExited);
		if(chain.Any(value=>value is ClrReadinessTimeoutException)) return new("runtime_not_ready",CommandExitCodes.Timeout);
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

internal sealed record WatchOptions(string? DefinitionsDirectory,string? ProfilesDirectory,string? AdditionalProfilesDirectory,string? StateRoot,int PollMilliseconds,int MaximumParallel,string? PayloadDirectory,string? AuditPath) {
	public static bool TryParse(string[] values,out WatchOptions result) {
		result=new(null,null,null,null,100,4,null,null); if(values.Length<3||values[0]!="run"||(values.Length-1)%2!=0) return false;
		string? definitions=null,profiles=null,additionalProfiles=null,state=null,payload=null,audit=null; var poll=100; var parallel=4;
		for(var index=1;index<values.Length;index+=2) {
			var value=values[index+1];
			if(values[index]=="--definitions"&&definitions is null) definitions=value;
			else if(values[index]=="--profiles"&&profiles is null) profiles=value;
			else if(values[index]=="--additional-profiles"&&additionalProfiles is null) additionalProfiles=value;
			else if(values[index]=="--state-root"&&state is null) state=Path.GetFullPath(value);
			else if(values[index]=="--poll-ms"&&Int32.TryParse(value,out var parsedPoll)&&parsedPoll is >=25 and <=5000) poll=parsedPoll;
			else if(values[index]=="--max-parallel"&&Int32.TryParse(value,out var parsedParallel)&&parsedParallel is >=1 and <=32) parallel=parsedParallel;
			else if(values[index]=="--payload-dir"&&payload is null) payload=Path.GetFullPath(value);
			else if(values[index]=="--audit"&&audit is null) audit=Path.GetFullPath(value);
			else return false;
		}
		if(String.IsNullOrWhiteSpace(definitions)==String.IsNullOrWhiteSpace(profiles)||additionalProfiles is not null&&profiles is null) return false;
		result=new(definitions is null?null:Path.GetFullPath(definitions),profiles is null?null:Path.GetFullPath(profiles),additionalProfiles is null?null:Path.GetFullPath(additionalProfiles),state,poll,parallel,payload,audit??Path.Combine(state??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab"),"watcher-audit.jsonl")); return true;
	}
}
