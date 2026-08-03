using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Mono;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Contracts.Metadata;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dgSpy.Extension {
	sealed partial class RpcHost : IDisposable {
		readonly AttachableProcessesService programs; readonly DbgManager manager; readonly DbgCodeBreakpointsService breakpoints; readonly DbgDotNetCodeLocationFactory locations; readonly DbgCallStackService callStack; readonly DbgLanguageService languages;
		readonly EvaluationQueue evaluations=new EvaluationQueue();
		readonly CancellationTokenSource shutdown=new CancellationTokenSource(); readonly object sync=new object(); readonly DebugEventBuffer events=new DebugEventBuffer(); readonly Dictionary<string,AttachableProcess> programCache=new Dictionary<string,AttachableProcess>(); readonly Dictionary<int,uint> requestedOffsets=new Dictionary<int,uint>();
		readonly int rpcPort=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_RPC_PORT"),out var value) ? value : 7351;
		TcpListener? tcpListener;
		Task? listener; string? sessionId; string? attachedProgramId; string? sessionKind; string? lifecycleAction; long stateVersion; bool attaching; bool faulted; string? faultMessage; string? lastUserMessage; int? terminalExitCode; string? terminalReason;
		public RpcHost(AttachableProcessesService programs, DbgManager manager, DbgCodeBreakpointsService breakpoints, DbgDotNetCodeLocationFactory locations, DbgCallStackService callStack, DbgLanguageService languages) {
			this.programs=programs; this.manager=manager; this.breakpoints=breakpoints; this.locations=locations; this.callStack=callStack; this.languages=languages;
			manager.ProcessPaused += (_,__) => Record("stopped"); manager.IsRunningChanged += (_,__) => { if (manager.IsRunning==true) Record("continued"); }; manager.IsDebuggingChanged += (_,__) => Record(manager.IsDebugging ? "session_started" : "session_ended");
			manager.MessageProcessExited += (_,e) => OnProcessExited(e);
			// An engine that fails to connect reports it here rather than through DbgManager.Start, which
			// only rejects options it cannot build an engine from. Recording it turns "faulted" from a
			// timeout guess into dnSpy's own reason. dnSpy's UI subscribes to the same event and shows a
			// modal error box — on the *UI* thread, so it does not block the debugger dispatcher or this
			// RPC, but it does leave a dialog nobody headless will dismiss.
			manager.MessageUserMessage += (_,e) => { lock(sync) lastUserMessage=e.Message; };
		}
		public void Start() { if (listener is not null) return; manager.WriteMessage($"dgSpy {Version} listening on 127.0.0.1:{rpcPort}"); listener=Task.Run(ListenAsync); }
		async Task ListenAsync() { try { tcpListener=new TcpListener(IPAddress.Loopback,rpcPort); tcpListener.Start(8); while(!shutdown.IsCancellationRequested) { var client=await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false); _=HandleClientAsync(client); } } catch(ObjectDisposedException) when(shutdown.IsCancellationRequested) { } catch(Exception ex) { manager.WriteMessage(PredefinedDbgManagerMessageKinds.ErrorUser,"dgSpy TCP listener: "+ex.Message); } }
		async Task HandleClientAsync(TcpClient client) { using(client) try { using var stream=client.GetStream(); using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true); using var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true){AutoFlush=true}; string? line; while ((line=await reader.ReadLineAsync().ConfigureAwait(false)) is not null) { var req=JsonConvert.DeserializeObject<RpcRequest>(line); var response=req is null ? RpcResponse.Failure("","invalid_request","Invalid JSON request.") : await DispatchAsync(req).ConfigureAwait(false); await writer.WriteLineAsync(JsonConvert.SerializeObject(response)).ConfigureAwait(false); } } 		// A client going away is routine, not an error: the gateway opens a fresh connection per request
		// and drops it whenever a request is cancelled or hits its deadline. Reporting those through
		// ErrorUser put a modal dialog on dnSpy's UI for every one. Genuine faults go to the Output
		// window, which is not modal.
		catch(Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException) { }
		catch(Exception ex) { manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,"dgSpy TCP client: "+ex.Message); } }
		async Task<RpcResponse> DispatchAsync(RpcRequest req) { try {
			if (req.Version!=ProtocolVersion.Current) return RpcResponse.Failure(req.RequestId,"incompatible_protocol",$"Protocol {req.Version} is unsupported; expected {ProtocolVersion.Current}.");
			if (req.DeadlineUtc is DateTime deadline && deadline<=DateTime.UtcNow) return RpcResponse.Failure(req.RequestId,"deadline_exceeded","Request deadline has expired.");
			using var requestCancellation=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
			if (req.DeadlineUtc is DateTime requestDeadline) requestCancellation.CancelAfter(requestDeadline-DateTime.UtcNow > TimeSpan.Zero ? requestDeadline-DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
			switch (req.Operation) {
			case "ping": return RpcResponse.Success(req.RequestId,new Handshake { ExtensionVersion=Version });
			case "get_host_info": return RpcResponse.Success(req.RequestId,Host());
			case "get_capabilities": return RpcResponse.Success(req.RequestId,CapabilityCatalog.Describe(Version));
			case "list_programs": return RpcResponse.Success(req.RequestId,await ListProgramsAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "attach": return RpcResponse.Success(req.RequestId,await AttachAsync((string?)req.Arguments["program_id"] ?? "",requestCancellation.Token).ConfigureAwait(false));
			case "attach_endpoint": return RpcResponse.Success(req.RequestId,await AttachEndpointAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "launch": return RpcResponse.Success(req.RequestId,await LaunchAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "get_session_state": CheckSession(req); return RpcResponse.Success(req.RequestId,await OnDebuggerAsync(State,requestCancellation.Token).ConfigureAwait(false));
			case "list_sessions": return RpcResponse.Success(req.RequestId,await ListSessionsAsync(requestCancellation.Token).ConfigureAwait(false));
			case "detach": return RpcResponse.Success(req.RequestId,await DetachAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "terminate": return RpcResponse.Success(req.RequestId,await TerminateAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "restart": return RpcResponse.Success(req.RequestId,await RestartAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "pause": CheckSession(req); await OnDebuggerAsync(()=>{ CheckVersion(req); manager.BreakAll(); return true; }).ConfigureAwait(false); await WaitForDebuggerAsync(()=>!manager.IsDebugging || manager.IsRunning==false,requestCancellation.Token).ConfigureAwait(false); return RpcResponse.Success(req.RequestId,await OnDebuggerAsync(State,requestCancellation.Token).ConfigureAwait(false));
			case "continue": CheckSession(req); await OnDebuggerAsync(()=>{ CheckVersion(req); manager.RunAll(); return true; }).ConfigureAwait(false); await WaitForDebuggerAsync(()=>!manager.IsDebugging || manager.IsRunning!=false,requestCancellation.Token).ConfigureAwait(false); return RpcResponse.Success(req.RequestId,await OnDebuggerAsync(State,requestCancellation.Token).ConfigureAwait(false));
			case "set_il_breakpoint": return RpcResponse.Success(req.RequestId,await SetBreakpointAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "list_breakpoints": return RpcResponse.Success(req.RequestId,await ListBreakpointsAsync(requestCancellation.Token).ConfigureAwait(false));
			case "remove_breakpoint": return RpcResponse.Success(req.RequestId,await RemoveBreakpointAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "clear_breakpoints": return RpcResponse.Success(req.RequestId,await ClearBreakpointsAsync(requestCancellation.Token).ConfigureAwait(false));
			case "wait_for_stop": return RpcResponse.Success(req.RequestId,await WaitAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "get_events": return RpcResponse.Success(req.RequestId,GetEvents(req));
			case "list_threads": return RpcResponse.Success(req.RequestId,await ListThreadsAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "get_callstack": return RpcResponse.Success(req.RequestId,await GetCallStackAsync(req,requestCancellation.Token).ConfigureAwait(false));
			case "get_frame": return RpcResponse.Success(req.RequestId,await GetFrameAsync(req,requestCancellation.Token).ConfigureAwait(false));
			default: return RpcResponse.Failure(req.RequestId,"unsupported","Unknown operation: "+req.Operation);
			}
		} catch (OperationCanceledException) { return RpcResponse.Failure(req.RequestId,"deadline_exceeded","The operation exceeded its deadline."); } catch (RpcException ex) { return RpcResponse.Failure(req.RequestId,ex.Code,ex.Message); } catch (Exception ex) { return RpcResponse.Failure(req.RequestId,"internal_error",ex.Message); } }
		// Unfiltered discovery probes every process on the machine. Passing process ids or names lets
		// dnSpy skip the rest, which is the difference between seconds and milliseconds when the
		// caller already knows what it is looking for.
		// provider_names selects attach providers by name. dnSpy skips providers a caller did not ask
		// for, which is how a caller avoids paying for a scan it does not need — Unity's multicast
		// discovery in particular only ever runs when it is named explicitly.
		async Task<ProgramInfo[]> ListProgramsAsync(RpcRequest req,CancellationToken cancellationToken) {
			var processIds=req.Arguments["process_ids"]?.ToObject<int[]>();
			var processNames=req.Arguments["process_names"]?.ToObject<string[]>();
			var providerNames=req.Arguments["provider_names"]?.ToObject<string[]>();
			var values=await programs.GetAttachableProcessesAsync(processNames,processIds,providerNames,cancellationToken).ConfigureAwait(false);
			lock(sync) { programCache.Clear(); return values.Select(p=>{
				// RuntimeId has no string form, so identity is composed from typed fields: pid, the
				// runtime GUID (the only thing separating .NET Framework from Unity/Mono), and the
				// engine's own discriminator, which is the CLR version for CorDebug.
				var id=ProgramIdentity.Create(p.ProcessId,p.RuntimeGuid,p.RuntimeName);
				programCache[id]=p;
				return new ProgramInfo { ProgramId=id,ProcessId=p.ProcessId,Executable=p.Filename,Title=p.Title,Architecture=p.Architecture.ToString(),RuntimeId=p.RuntimeName,RuntimeName=p.RuntimeName,RuntimeGuid=p.RuntimeGuid.ToString("D"),RuntimeKindGuid=p.RuntimeKindGuid.ToString("D"),AttachProviders=AttachProviders(p.RuntimeGuid) }; }).ToArray(); }
		}
		// AttachableProcess does not say which provider produced it, so report the providers that can:
		// the runtime GUID identifies the engine, and Unity's runtime is reachable through either of
		// dnSpy's two Unity providers. These are the exact strings provider_names accepts.
		static string[] AttachProviders(Guid runtimeGuid) =>
			runtimeGuid==PredefinedDbgRuntimeGuids.DotNetFramework_Guid ? new[]{PredefinedAttachProgramOptionsProviderNames.DotNetFramework} :
			runtimeGuid==PredefinedDbgRuntimeGuids.DotNet_Guid ? new[]{PredefinedAttachProgramOptionsProviderNames.DotNet} :
			runtimeGuid==PredefinedDbgRuntimeGuids.DotNetUnity_Guid ? new[]{PredefinedAttachProgramOptionsProviderNames.UnityEditor,PredefinedAttachProgramOptionsProviderNames.UnityPlayer} :
			Array.Empty<string>();
		async Task<SessionState> AttachAsync(string id,CancellationToken cancellationToken) {
			AttachableProcess p; lock(sync) if (!programCache.TryGetValue(id,out p!)) throw new RpcException("program_not_found","Refresh list_programs and use an exact program_id.");
			// AttachableProcess.Attach() is exactly DbgManager.Start(GetOptions()) with the returned error
			// string discarded. Calling Start directly is the same attach, except a refused engine says why.
			return await StartSessionAsync(id,"attach",()=>manager.Start(p.GetOptions()),default,cancellationToken).ConfigureAwait(false);
		}
		// A Mono/Unity target launched with --debugger-agent=transport=dt_socket,server=y,address=HOST:PORT
		// emits no multicast beacon, so no attach provider ever enumerates it and list_programs can never
		// reach it. Connecting by address and port is the only route to that engine.
		// The plan names UnityAttachToProgramOptions; that type is internal to dnSpy's Mono engine.
		// UnityConnectStartDebuggingOptions is the public contract equivalent — DbgEngineProviderImpl maps
		// both onto the same engine, and DbgEngineImpl.StartCore sets wasAttach for both, so the session
		// has attach semantics either way (detach leaves the target alive; the target is not launched).
		async Task<SessionState> AttachEndpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			var address=(string?)req.Arguments["address"]; if (string.IsNullOrWhiteSpace(address)) address="127.0.0.1";
			var port=(int?)req.Arguments["port"] ?? throw new RpcException("invalid_arguments","port is required.");
			if (port<1 || port>ushort.MaxValue) throw new RpcException("invalid_arguments","port must be between 1 and 65535.");
			var engine=((string?)req.Arguments["engine"] ?? "unity").ToLowerInvariant();
			MonoConnectStartDebuggingOptionsBase options=engine switch {
				"unity" => new UnityConnectStartDebuggingOptions(),
				"mono" => new MonoConnectStartDebuggingOptions(),
				_ => throw new RpcException("invalid_arguments","engine must be \"unity\" or \"mono\"."),
			};
			// suspend=y in the debugger-agent argument means the target is parked waiting for us; the
			// engine then breaks at the first method entry instead of letting it run. Getting this wrong
			// either hangs the target forever or misses the start of execution.
			options.Address=address; options.Port=(ushort)port; options.ProcessIsSuspended=(bool?)req.Arguments["process_is_suspended"] ?? false;
			var timeoutMs=(int?)req.Arguments["connection_timeout_ms"] ?? 0;
			if (timeoutMs>0) options.ConnectionTimeout=TimeSpan.FromMilliseconds(Math.Min(timeoutMs,(int)TimeSpan.FromMinutes(5).TotalMilliseconds));
			// dnSpy retries the socket for the whole connection timeout (10 s by default) before giving up,
			// so the session cannot be called faulted until after that plus room for the reply.
			var connect=(options.ConnectionTimeout==TimeSpan.Zero ? TimeSpan.FromSeconds(10) : options.ConnectionTimeout)+TimeSpan.FromSeconds(5);
			return await StartSessionAsync($"endpoint:{engine}:{address}:{port}","attach",()=>manager.Start(options),connect,cancellationToken).ConfigureAwait(false);
		}
		async Task<SessionState> StartSessionAsync(string programId,string kind,Func<string?> start,TimeSpan connectWait,CancellationToken cancellationToken) {
			if (sessionId is not null && await OnDebuggerAsync(()=>manager.IsDebugging).ConfigureAwait(false)) throw new RpcException("session_already_active","Detach the current session before attaching to another program.");
			var rejected=await OnDebuggerAsync(()=>{
				lock(sync) { events.Reset(); stateVersion=0; attaching=true; faulted=false; faultMessage=null; lastUserMessage=null; terminalExitCode=null; terminalReason=null; sessionKind=kind; lifecycleAction=null; }
				var failure=start();
				if (failure is null) { sessionId=Guid.NewGuid().ToString("N"); stateVersion++; }
				else lock(sync) attaching=false;
				return failure;
			}).ConfigureAwait(false);
			// DbgManager.Start rejects options it cannot build an engine from synchronously. That is a
			// caller error, not a session that came up and then died, so it never becomes a session.
			if (rejected is not null) throw new RpcException("attach_failed",rejected);
			Record("attached");
			// Attach is asynchronous: the engine is not up when Start() returns. Wait for threads, not
			// just for a process — a pause issued before the engine has enumerated threads produces a
			// stop with no current thread and no call stack, and the session does not recover until it
			// runs again. A connect failure arrives as a user message and ends the wait early, so a dead
			// endpoint costs the connection timeout rather than the connection timeout plus this one.
			await WaitForDebuggerAsync(()=>UserMessage() is not null || (manager.IsDebugging && manager.Processes.SelectMany(process=>process.Threads).Any()),cancellationToken,connectWait).ConfigureAwait(false);
			// The engine never came up. Report it rather than leaving the caller polling "attaching".
			if (!await OnDebuggerAsync(()=>manager.IsDebugging,cancellationToken).ConfigureAwait(false)) {
				lock(sync) { faulted=true; attaching=false; faultMessage=lastUserMessage ?? "The debug engine did not connect before the attach deadline."; }
				Record("attach_failed");
			}
			else lock(sync) { attachedProgramId=programId; faultMessage=null; }
			return await OnDebuggerAsync(State,cancellationToken).ConfigureAwait(false);
		}
		string? UserMessage() { lock(sync) return lastUserMessage; }
		// Detach is the only safe way to end a session: closing dnSpy with a live CorDebug attachment
		// terminates the target. If dnSpy cannot detach without killing it, say so instead of doing it.
		async Task<DetachResult> DetachAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			bool allowTerminate=(bool?)req.Arguments["allow_terminate"] ?? false;
			var canDetach=await OnDebuggerAsync(()=>!manager.IsDebugging || manager.CanDetachWithoutTerminating,cancellationToken).ConfigureAwait(false);
			if (!canDetach && !allowTerminate) throw new RpcException("detach_would_terminate","dnSpy cannot detach from this target without terminating it. Pass allow_terminate=true to stop debugging anyway.");
			if (!canDetach) lock(sync) lifecycleAction="terminate";
			await OnDebuggerAsync(()=>{ if (manager.IsDebugging) { if (canDetach) manager.DetachAll(); else manager.StopDebuggingAll(); } return true; },cancellationToken).ConfigureAwait(false);
			await WaitForDebuggerAsync(()=>!manager.IsDebugging,cancellationToken).ConfigureAwait(false);
			var stillDebugging=await OnDebuggerAsync(()=>manager.IsDebugging,cancellationToken).ConfigureAwait(false);
			string id; lock(sync) { id=sessionId!; if (!stillDebugging) { sessionId=null; attachedProgramId=null; sessionKind=null; lifecycleAction=null; attaching=false; faulted=false; faultMessage=null; lastUserMessage=null; terminalExitCode=null; terminalReason=null; } }
			Record("detached");
			return new DetachResult { SessionId=id,Detached=!stillDebugging && canDetach,Terminated=!stillDebugging && !canDetach,StateVersion=stateVersion };
		}
		async Task<SessionSummary[]> ListSessionsAsync(CancellationToken cancellationToken) => await OnDebuggerAsync(()=>{
			if (sessionId is null) return Array.Empty<SessionSummary>();
			var state=State();
			return new[] { new SessionSummary { SessionId=state.SessionId,State=state.State,ProgramId=attachedProgramId ?? "",StateVersion=state.StateVersion,LastEventId=state.LastEventId,ProcessIds=state.ProcessIds,CanDetachWithoutTerminating=manager.IsDebugging && manager.CanDetachWithoutTerminating } };
		},cancellationToken).ConfigureAwait(false);
		SessionState State() { if (sessionId is null) throw new RpcException("session_not_found","No active dgSpy session."); if (attaching && manager.IsDebugging && manager.Processes.Length!=0) attaching=false; return new SessionState { SessionId=sessionId,State=SessionStateCalculator.Get(faulted,attaching,manager.IsDebugging,manager.IsRunning),StateVersion=stateVersion,LastEventId=events.LastEventId,ProcessIds=manager.Processes.Select(p=>p.Id).ToArray(),FaultMessage=faulted ? faultMessage : null,ExitCode=terminalExitCode,TerminalReason=terminalReason }; }
		// Polls on the dispatcher so callers observe the state the operation actually produced. Bounded
		// by the request deadline; a timeout returns the current state rather than throwing, so the
		// caller still learns where the session got to.
		async Task WaitForDebuggerAsync(Func<bool> predicate,CancellationToken cancellationToken,TimeSpan maxWait=default) {
			var end=DateTime.UtcNow.Add(maxWait==default ? TimeSpan.FromSeconds(10) : maxWait);
			while (DateTime.UtcNow<end) {
				if (await OnDebuggerAsync(predicate).ConfigureAwait(false)) return;
				await Task.Delay(25,cancellationToken).ConfigureAwait(false);
			}
		}
		// Setting a breakpoint is not the same as binding one. The engine binds asynchronously on its own
		// thread, and Mono refuses any offset that is not a sequence point (NO_SEQ_POINT_AT_IL_OFFSET,
		// reported as "Could not create the breakpoint"). Returning an id the moment the breakpoint is
		// registered therefore reports success for a breakpoint that can never be hit, and the caller's
		// only symptom is a wait_for_stop that never fires. So: wait for the engine's verdict and report
		// it, and when the offset was refused, optionally retry at method entry, which Mono always
		// accepts. CorDebug takes any offset, so none of this changes .NET Framework behavior.
		async Task<BreakpointInfo> SetBreakpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var module=(string?)req.Arguments["module"] ?? throw new RpcException("invalid_arguments","module is required");
			var token=(uint?)req.Arguments["method_token"] ?? 0;
			var requested=(uint?)req.Arguments["il_offset"] ?? 0;
			bool snap=(bool?)req.Arguments["snap_to_sequence_point"] ?? true;
			// Captured before the breakpoint can exist. A breakpoint on a hot method is hit before the
			// caller can read an event cursor afterwards, so a cursor taken after this call has already
			// missed the stop and wait_for_stop reports a timeout for a breakpoint that is working.
			long cursor; lock(sync) cursor=events.LastEventId;
			var info=await AddBreakpointAsync(module,token,requested,requested,cancellationToken).ConfigureAwait(false);
			info.CursorEventId=cursor;
			// Only an outright Error means refused. No bound breakpoints with no error is a pending
			// breakpoint whose module has not loaded yet, which is legitimate and must not be retried.
			if (info.Bound || info.Severity!="error" || !snap || requested==0) return info;
			await RemoveBreakpointAsync(info.BreakpointId,cancellationToken).ConfigureAwait(false);
			var entry=await AddBreakpointAsync(module,token,0,requested,cancellationToken).ConfigureAwait(false);
			// Remembered so list_breakpoints keeps reporting that this breakpoint is not where it was asked
			// to be; the location itself no longer carries that.
			lock(sync) requestedOffsets[entry.BreakpointId]=requested;
			entry.CursorEventId=cursor;
			entry.Warning=$"The engine refused IL offset 0x{requested:X} ({info.Message}); on Mono a breakpoint can only sit on a sequence point. This one is at method entry (offset 0) instead, so it stops earlier than requested — and if the method is only entered once, possibly not at all. Pass snap_to_sequence_point=false to get the failure instead.";
			return entry;
		}
		async Task<BreakpointInfo> AddBreakpointAsync(string module,uint token,uint offset,uint requested,CancellationToken cancellationToken) {
			var bp=await OnDebuggerAsync(()=>{
				var location=locations.Create(ModuleId.Create(module),token,offset);
				var added=breakpoints.Add(new DbgCodeBreakpointInfo(location,new DbgCodeBreakpointSettings { IsEnabled=true }));
				// Add returns null when the location is already taken; the location we just cloned would
				// otherwise leak, since only the owning breakpoint closes it.
				if (added is null) { manager.Close(location); throw new RpcException("duplicate_breakpoint","A breakpoint already exists at that location."); }
				return added;
			},cancellationToken).ConfigureAwait(false);
			// The verdict arrives on the engine thread. Without a live session there is nothing to bind
			// against, so do not spend the wait.
			await WaitForDebuggerAsync(()=>!manager.IsDebugging || bp.BoundBreakpoints.Length!=0,cancellationToken,TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>Describe(bp,requested),cancellationToken).ConfigureAwait(false);
		}
		Task RemoveBreakpointAsync(int id,CancellationToken cancellationToken) => OnDebuggerAsync(()=>{
			var found=breakpoints.Breakpoints.FirstOrDefault(b=>b.Id==id);
			if (found is not null) breakpoints.Remove(new[]{found});
			return true;
		},cancellationToken);
		BreakpointInfo Describe(DbgCodeBreakpoint bp,uint? requested=null) {
			var message=bp.BoundBreakpointsMessage;
			var severity=message.Severity==DbgBoundCodeBreakpointSeverity.Error ? "error" : message.Severity==DbgBoundCodeBreakpointSeverity.Warning ? "warning" : "none";
			var location=bp.Location as DbgDotNetCodeLocation;
			// A warning is not "bound with a caveat": dnSpy's warning and error texts both read "The
			// breakpoint will not currently be hit", so only a clean message means the engine installed
			// it. Bound is a claim about binding, not a promise of a hit — a breakpoint in code that
			// never runs again is bound and silent, which is correct.
			var offset=location?.Offset ?? 0;
			if (requested is null) lock(sync) if (requestedOffsets.TryGetValue(bp.Id,out var remembered)) requested=remembered;
			return new BreakpointInfo {
				BreakpointId=bp.Id,Module=location?.Module.ModuleName ?? "",MethodToken=location?.Token ?? 0,IlOffset=offset,
				RequestedIlOffset=requested ?? offset,Snapped=(requested ?? offset)!=offset,Enabled=bp.IsEnabled,
				Bound=bp.BoundBreakpoints.Length!=0 && message.Severity==DbgBoundCodeBreakpointSeverity.None,
				BoundCount=bp.BoundBreakpoints.Length,Severity=severity,Message=message.Message.Length==0 ? null : message.Message,
				SessionId=sessionId,StateVersion=stateVersion,
			};
		}
		async Task<BreakpointInfo[]> ListBreakpointsAsync(CancellationToken cancellationToken) =>
			await OnDebuggerAsync(()=>breakpoints.Breakpoints.Select(b=>Describe(b)).OrderBy(b=>b.BreakpointId).ToArray(),cancellationToken).ConfigureAwait(false);
		async Task<RemoveBreakpointResult> RemoveBreakpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			var id=(int?)req.Arguments["breakpoint_id"] ?? throw new RpcException("invalid_arguments","breakpoint_id is required.");
			return await OnDebuggerAsync(()=>{
				var found=breakpoints.Breakpoints.FirstOrDefault(b=>b.Id==id);
				if (found is null) throw new RpcException("breakpoint_not_found",$"Breakpoint {id} does not exist. Refresh list_breakpoints and use an exact breakpoint_id.");
				breakpoints.Remove(new[]{found});
				lock(sync) requestedOffsets.Remove(id);
				return new RemoveBreakpointResult { BreakpointId=id,Removed=true,StateVersion=stateVersion };
			},cancellationToken).ConfigureAwait(false);
		}
		// Clears every dnSpy breakpoint, including any set by hand in the UI — dnSpy keeps one global
		// collection and dgSpy does not own a subset of it. Breakpoints outlive a session and rebind on
		// the next attach, so without this a fresh session can stop on a breakpoint nobody set.
		async Task<ClearBreakpointsResult> ClearBreakpointsAsync(CancellationToken cancellationToken) =>
			await OnDebuggerAsync(()=>{ var count=breakpoints.Breakpoints.Length; breakpoints.Clear(); lock(sync) requestedOffsets.Clear(); return new ClearBreakpointsResult { Removed=count,StateVersion=stateVersion }; },cancellationToken).ConfigureAwait(false);
		string ThreadId(DbgThread thread) => $"{thread.Process.Id}:{thread.Id}";
		async Task<ThreadInfo[]> ListThreadsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			return await OnDebuggerAsync(()=>{
				if (manager.IsRunning!=false) throw new RpcException("not_paused","Pause the session before listing threads so managed-frame availability is stable.");
				return manager.Processes.SelectMany(process=>process.Threads).Select(thread=>{
					return new ThreadInfo { ThreadId=ThreadId(thread),ProcessId=thread.Process.Id,OsThreadId=thread.Id,ManagedThreadId=thread.ManagedId,
						Name=thread.Name,Kind=thread.Kind,IsMain=thread.IsMain,IsCurrent=thread==manager.CurrentThread.Current,
						SuspendedCount=thread.SuspendedCount,States=thread.State.Select(state=>state.State).ToArray() };
				}).OrderBy(thread=>thread.ProcessId).ThenBy(thread=>thread.OsThreadId).ToArray();
			},cancellationToken).ConfigureAwait(false);
		}
		async Task<FrameInfo[]> GetCallStackAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			int max=Math.Min(100,Math.Max(1,(int?)req.Arguments["max_frames"] ?? 50));
			var requestedThreadId=(string?)req.Arguments["thread_id"];
			// The call stack is built from DbgManager.CurrentThread, which dnSpy sets from the UI or
			// from a stop that carries a thread. A pause issued right after attach has neither, and
			// the engine may not have enumerated any threads yet either. So: wait for a thread to
			// exist, then select one if nothing is current.
			await WaitForDebuggerAsync(()=>manager.IsRunning!=false || manager.Processes.SelectMany(p=>p.Threads).Any(),cancellationToken,TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			var selectedThreadId=await OnDebuggerAsync(()=>{
				if (manager.IsRunning!=false) throw new RpcException("not_paused","Pause the session before requesting its call stack.");
				var all=manager.Processes.SelectMany(p=>p.Threads).ToArray();
				if (!string.IsNullOrEmpty(requestedThreadId)) {
					var requested=all.FirstOrDefault(thread=>ThreadId(thread)==requestedThreadId);
					if (requested is null) throw new RpcException("thread_not_found",$"Thread {requestedThreadId} is not active. Refresh list_threads and use an exact thread_id.");
					manager.CurrentThread.Current=requested;
				}
				else if (manager.CurrentThread.Current is null) {
					var first=all.FirstOrDefault(); if (first is not null) manager.CurrentThread.Current=first;
				}
				return manager.CurrentThread.Current is null ? "" : ThreadId(manager.CurrentThread.Current);
			},cancellationToken).ConfigureAwait(false);
			// That picks an arbitrary thread, and on Unity the arbitrary one routinely has no managed
			// frames at all — the caller then gets an empty stack and no way to ask for a different
			// thread. Probe threads with a throwaway stack walker and select one that actually has
			// frames. The walker and its frames are ours to close; the frames the call stack service
			// then produces are not, which is why this only probes and does not return them.
			await OnDebuggerAsync(()=>{
				if (!string.IsNullOrEmpty(requestedThreadId) || manager.IsRunning!=false || callStack.Frames.Frames.Count!=0) return true;
				foreach (var thread in manager.Processes.SelectMany(process=>process.Threads)) {
					var walker=thread.CreateStackWalker();
					try {
						var probe=walker.GetNextStackFrames(1);
						if (probe.Length==0) continue;
						manager.Close(probe);
						manager.CurrentThread.Current=thread; selectedThreadId=ThreadId(thread);
						return true;
					}
					finally { walker.Close(); }
				}
				return true;
			},cancellationToken).ConfigureAwait(false);
			// DbgCallStackService then refreshes its frames on the dispatcher, so wait for them rather
			// than racing. A stack still empty after the wait is reported as empty, not as an error.
			await WaitForDebuggerAsync(()=>manager.IsRunning!=false || (callStack.Frames.Frames.Count!=0 && ThreadId(callStack.Frames.Frames[0].Thread)==selectedThreadId),cancellationToken,TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			// Identity is read on the dispatcher, where DbgObject access belongs. Evaluation of names
			// and locals then happens on the evaluation thread so it cannot stall event delivery.
			var captured=await OnDebuggerAsync(()=>{
				if(manager.IsRunning!=false) throw new RpcException("not_paused","Pause the session before requesting its call stack.");
				return callStack.Frames.Frames.Where(frame=>ThreadId(frame.Thread)==selectedThreadId).Take(max).Select((frame,index)=>new CapturedFrame(frame,languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid),new FrameInfo {
					FrameId=$"{sessionId}:{stateVersion}:{selectedThreadId}:{index}",ThreadId=selectedThreadId,FrameIndex=index,Module=frame.Module?.Filename ?? "",ModuleName=frame.Module?.Name ?? "",
					MethodToken=frame.FunctionToken,IlOffset=frame.FunctionOffset,Name=$"0x{frame.FunctionToken:X8}+0x{frame.FunctionOffset:X}",
				})).ToArray();
			},cancellationToken).ConfigureAwait(false);
			using var evaluation=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,cancellationToken);
			return await evaluations.RunAsync(()=>captured.Select(c=>DescribeFrame(c,evaluation.Token)).ToArray(),cancellationToken).ConfigureAwait(false);
		}
		async Task<FrameInfo> GetFrameAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			if (string.IsNullOrEmpty((string?)req.Arguments["thread_id"])) throw new RpcException("invalid_arguments","thread_id is required.");
			var index=(int?)req.Arguments["frame_index"] ?? throw new RpcException("invalid_arguments","frame_index is required.");
			if (index<0 || index>=100) throw new RpcException("invalid_arguments","frame_index must be between 0 and 99.");
			req.Arguments["max_frames"]=index+1;
			var frames=await GetCallStackAsync(req,cancellationToken).ConfigureAwait(false);
			if (index>=frames.Length) throw new RpcException("frame_not_found",$"Thread {(string?)req.Arguments["thread_id"]} has no frame at index {index}. Refresh get_callstack and use an available frame_index.");
			return frames[index];
		}
		readonly struct CapturedFrame {
			public readonly DbgStackFrame Frame; public readonly DbgLanguage Language; public readonly FrameInfo Info;
			public CapturedFrame(DbgStackFrame frame,DbgLanguage language,FrameInfo info) { Frame=frame; Language=language; Info=info; }
		}
		// Frame identity is module + method token + IL offset, which is exactly what set_il_breakpoint
		// takes. The formatted name is display only; agents must never have to parse it.
		// Runs on the evaluation thread, so the frame it was handed may have been closed in the
		// meantime — the dispatcher is now free to process a resume while this runs. That is the price
		// of not holding the dispatcher, and it is why the snapshot is rejected rather than patched up.
		FrameInfo DescribeFrame(CapturedFrame captured,CancellationToken cancellationToken) {
			var (frame,language,info)=(captured.Frame,captured.Language,captured.Info);
			if (frame.IsClosed) throw new RpcException("stale_handle","The target resumed while its call stack was being read. Pause again and request a fresh snapshot.");
			var context=language.CreateContext(frame,cancellationToken:cancellationToken);
			try {
				var eval=new DbgEvaluationInfo(context,frame,cancellationToken);
				var name=new DbgStringBuilderTextWriter();
				language.Formatter.FormatFrame(eval,name,DbgStackFrameFormatterOptions.DeclaringTypes|DbgStackFrameFormatterOptions.ParameterTypes|DbgStackFrameFormatterOptions.ReturnTypes,DbgValueFormatterOptions.None,null);
				if (name.Text.Length!=0) info.Name=name.Text;
				info.Locals=GetPrimitiveLocals(language,eval);
			}
			finally { context.Close(); }
			return info;
		}
		PrimitiveValue[] GetPrimitiveLocals(DbgLanguage language,DbgEvaluationInfo eval) { var nodes=language.LocalsProvider.GetNodes(eval,DbgValueNodeEvaluationOptions.NoFuncEval,DbgLocalsValueNodeEvaluationOptions.None).Select(n=>n.ValueNode).ToArray(); try { return nodes.Where(n=>n.Value is not null && n.Value.HasRawValue && n.Value.ValueType!=DbgSimpleValueType.Other && n.Value.ValueType!=DbgSimpleValueType.Void).Select(n=>{ var name=new DbgStringBuilderTextWriter(); var type=new DbgStringBuilderTextWriter(); n.FormatName(eval,name,DbgValueFormatterOptions.None); n.FormatActualType(eval,type,DbgValueFormatterTypeOptions.None,DbgValueFormatterOptions.None,null); return new PrimitiveValue { Name=name.Text,Type=type.Text,Value=n.Value!.RawValue }; }).ToArray(); } finally { manager.Close(nodes); } }
		// RunContinuationsAsynchronously matters: without it every continuation after an await —
		// response serialization, socket writes — runs inline on the debugger dispatcher thread, which
		// stalls event delivery for every session. See docs/DGSPY_BASELINE.md.
		// The token abandons the *wait*, not the queued work; dnSpy gives us no way to cancel a
		// dispatcher callback, so the callback still runs and its result is dropped.
		public void Dispose() { shutdown.Cancel(); tcpListener?.Stop(); evaluations.Dispose(); shutdown.Dispose(); }
	}

	/// <summary>
	/// Runs debugger expression evaluation on one dedicated thread that is NOT the dnSpy dispatcher.
	/// Evaluation blocks on the engine's own thread and, once func-eval is enabled, runs code inside
	/// the target; doing that on the dispatcher would stall debugger event delivery for every session
	/// while it ran. dnSpy has the same rule — it evaluates on its UI thread, never the dispatcher.
	/// A single thread keeps evaluations serialized, which the dnSpy object model requires anyway.
	/// </summary>
	sealed class EvaluationQueue : IDisposable {
		readonly BlockingCollection<Action> work=new BlockingCollection<Action>();
		public EvaluationQueue() { var thread=new Thread(Loop) { IsBackground=true,Name="dgSpy evaluation" }; thread.Start(); }
		void Loop() { foreach (var item in work.GetConsumingEnumerable()) { try { item(); } catch { /* per-item faults are reported through the item's own task */ } } }
		public Task<T> RunAsync<T>(Func<T> callback,CancellationToken cancellationToken) {
			var tcs=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			try { work.Add(()=>{ if (cancellationToken.IsCancellationRequested) { tcs.TrySetCanceled(cancellationToken); return; } try { tcs.TrySetResult(callback()); } catch (Exception ex) { tcs.TrySetException(ex); } }); }
			catch (InvalidOperationException) { tcs.TrySetCanceled(); return tcs.Task; }
			if (!cancellationToken.CanBeCanceled) return tcs.Task;
			// Cancellation abandons the wait; the queued evaluation still runs to completion because
			// dnSpy cannot abort one mid-flight. It no longer holds the dispatcher while it does.
			var registration=cancellationToken.Register(()=>tcs.TrySetCanceled(cancellationToken));
			return tcs.Task.ContinueWith(t=>{ registration.Dispose(); return t.GetAwaiter().GetResult(); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
		}
		public void Dispose() => work.CompleteAdding();
	}
}
