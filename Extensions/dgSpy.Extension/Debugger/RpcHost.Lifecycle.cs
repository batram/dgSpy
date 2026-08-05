using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.DotNet.Mono;
using System.Text.Json.Nodes;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		async Task<SessionState> LaunchAsync(RpcRequest req,CancellationToken cancellationToken) {
			var filename=(string?)req.Arguments["filename"];
			if (string.IsNullOrWhiteSpace(filename)) throw new RpcException("invalid_arguments","filename is required.");
			try { filename=Path.GetFullPath(filename); }
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { throw new RpcException("invalid_arguments","filename is invalid: "+ex.Message); }
			if (!File.Exists(filename)) throw new RpcException("file_not_found","The launch target does not exist: "+filename);

			var commandLine=(string?)req.Arguments["command_line"] ?? "";
			var workingDirectory=(string?)req.Arguments["working_directory"];
			if (string.IsNullOrWhiteSpace(workingDirectory)) workingDirectory=Path.GetDirectoryName(filename);
			if (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory)) throw new RpcException("directory_not_found","The working directory does not exist: "+workingDirectory);
			var breakKind=BreakKind((string?)req.Arguments["break_at"]);
			var engine=((string?)req.Arguments["engine"] ?? "cordebug").ToLowerInvariant();
			StartDebuggingOptions options;
			TimeSpan connectWait=default;
			switch(engine) {
			case "cordebug":
				options=new DotNetFrameworkStartDebuggingOptions { Filename=filename,CommandLine=commandLine,WorkingDirectory=workingDirectory,BreakKind=breakKind };
				break;
			case "unity":
				var unity=new UnityStartDebuggingOptions { Filename=filename,CommandLine=commandLine,WorkingDirectory=workingDirectory,BreakKind=breakKind };
				var timeoutMs=(int?)req.Arguments["connection_timeout_ms"] ?? 0;
				if (timeoutMs>0) unity.ConnectionTimeout=TimeSpan.FromMilliseconds(Math.Min(timeoutMs,(int)TimeSpan.FromMinutes(5).TotalMilliseconds));
				options=unity;
				connectWait=(unity.ConnectionTimeout==TimeSpan.Zero ? TimeSpan.FromSeconds(30) : unity.ConnectionTimeout)+TimeSpan.FromSeconds(5);
				break;
			default: throw new RpcException("invalid_arguments","engine must be \"cordebug\" or \"unity\".");
			}
			ApplyEnvironment(options,req.Arguments["environment"] as JsonObject);
			return await StartSessionAsync("launch:"+engine+":"+filename,"launch",()=>manager.Start(options),connectWait,cancellationToken).ConfigureAwait(false);
		}

		static string BreakKind(string? value) {
			switch((value ?? "none").ToLowerInvariant()) {
			case "none": return PredefinedBreakKinds.DontBreak;
			case "create_process": return PredefinedBreakKinds.CreateProcess;
			case "entry_point": return PredefinedBreakKinds.EntryPoint;
			default: throw new RpcException("invalid_arguments","break_at must be \"none\", \"create_process\", or \"entry_point\".");
			}
		}

		static void ApplyEnvironment(StartDebuggingOptions options,JsonObject? environment) {
			if (environment is null) return;
			DbgEnvironment target;
			if (options is CorDebugStartDebuggingOptions corDebug) target=corDebug.Environment;
			else if (options is MonoStartDebuggingOptionsBase mono) target=mono.Environment;
			else return;
			foreach (var property in environment) {
				if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new RpcException("invalid_arguments","environment values must be strings.");
				target.Add(property.Key,text);
			}
		}

		async Task<SessionState> TerminateAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			if (!await OnDebuggerAsync(()=>manager.IsDebugging,cancellationToken).ConfigureAwait(false)) throw new RpcException("session_not_running","The session has already ended.");
			if(req.Arguments["process_id"] is not null) {
				var process=await OnDebuggerAsync(()=>SelectProcess(req),cancellationToken).ConfigureAwait(false);
				lock(sync) processLifecycleActions[process.Id]="terminate";
				await OnDebuggerAsync(()=>{ process.Terminate(); return true; },cancellationToken).ConfigureAwait(false);
				await WaitForDebuggerAsync(()=>manager.Processes.All(p=>p.Id!=process.Id),cancellationToken).ConfigureAwait(false);
				return await OnDebuggerAsync(State,cancellationToken).ConfigureAwait(false);
			}
			lock(sync) lifecycleAction="terminate";
			try {
				await OnDebuggerAsync(()=>{ CloseStepper(); manager.TerminateAll(); return true; },cancellationToken).ConfigureAwait(false);
				await WaitForDebuggerAsync(()=>!manager.IsDebugging,cancellationToken).ConfigureAwait(false);
				if (await OnDebuggerAsync(()=>manager.IsDebugging,cancellationToken).ConfigureAwait(false)) throw new RpcException("terminate_timed_out","dnSpy did not terminate the target before the operation deadline.");
				lock(sync) { terminalReason="terminated_by_client"; requestedOffsets.Clear(); }
				return await OnDebuggerAsync(State,cancellationToken).ConfigureAwait(false);
			}
			finally { lock(sync) lifecycleAction=null; }
		}

		async Task<SessionState> RestartAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var oldProcessIds=await OnDebuggerAsync(()=>manager.Processes.Select(process=>process.Id).ToArray(),cancellationToken).ConfigureAwait(false);
			var canRestart=await OnDebuggerAsync(()=>sessionKind=="launch" && manager.Processes.Length==1 && manager.CanRestart,cancellationToken).ConfigureAwait(false);
			if (!canRestart) throw new RpcException("restart_unsupported","Only a target launched through dgSpy can be restarted, and the active engine must advertise restart support.");
			lock(sync) lifecycleAction="restart";
			try {
				await OnDebuggerAsync(()=>{ CloseStepper(); manager.Restart(); return true; },cancellationToken).ConfigureAwait(false);
				await WaitForDebuggerAsync(()=>manager.IsDebugging && manager.Processes.Any(process=>!oldProcessIds.Contains(process.Id)) && manager.Processes.SelectMany(process=>process.Threads).Any(),cancellationToken,TimeSpan.FromSeconds(12)).ConfigureAwait(false);
				var restarted=await OnDebuggerAsync(()=>manager.IsDebugging && manager.Processes.Any(process=>!oldProcessIds.Contains(process.Id)),cancellationToken).ConfigureAwait(false);
				if (!restarted) throw new RpcException("restart_failed","dnSpy did not create a replacement process before the operation deadline.");
				lock(sync) { terminalExitCode=null; terminalReason=null; requestedOffsets.Clear(); }
				Record(EventKinds.Restarted);
				return await OnDebuggerAsync(State,cancellationToken).ConfigureAwait(false);
			}
			finally { lock(sync) lifecycleAction=null; }
		}

		void OnProcessExited(DbgMessageProcessExitedEventArgs e) {
			string? action;
			lock(sync) {
				if (sessionId is null) return;
				if(!processLifecycleActions.TryGetValue(e.Process.Id,out action)) action=lifecycleAction; else processLifecycleActions.Remove(e.Process.Id);
			}
			var reason=action=="terminate" ? "terminated_by_client" : action=="restart" ? "restart" : action=="detach" ? "detached_by_client" : "target_exited";
			var terminal=action!="restart" && !manager.Processes.Any(p=>p.Id!=e.Process.Id);
			// Raised on the debugger dispatcher, so this is the right thread to release a stepper the
			// target just took with it. A step in flight when the process dies never raises StepComplete.
			CloseStepper();
			if (terminal) lock(sync) { terminalExitCode=e.ExitCode; terminalReason=reason; requestedOffsets.Clear(); }
			Record(action=="terminate" ? EventKinds.Terminated : action=="restart" ? EventKinds.RestartProcessExited : action=="detach" ? EventKinds.Detached : EventKinds.SessionExited,terminal,e.Process.Id,e.ExitCode,reason);
		}
	}
}
