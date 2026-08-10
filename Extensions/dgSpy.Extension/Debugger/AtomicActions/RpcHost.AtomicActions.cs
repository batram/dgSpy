using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using dgSpy.Extension.Debugger.AtomicActions;
using dgSpy.Extension.Debugger.OwnedBreakpoints;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Code;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		readonly ConcurrentDictionary<string,AtomicActionRecord> atomicActions=new ConcurrentDictionary<string,AtomicActionRecord>(StringComparer.Ordinal);

		async Task<AtomicActionResult> RunAtomicActionAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			RequireAtomicOperationVersion(req);
			var request=ProtocolJson.FromNode<AtomicActionRequest>(req.Arguments["request"]) ?? throw new RpcException("invalid_arguments","request is required.");
			if(request.DeadlineUtc==default) request.DeadlineUtc=DateTime.UtcNow.AddMilliseconds(Math.Min(60000,Math.Max(1,(int?)req.Arguments["timeout_ms"] ?? 10000)));
			var kind=(string?)req.Arguments["action_kind"] ?? throw new RpcException("invalid_arguments","action_kind is required.");
			var action=CreateAtomicAction(kind,req);
			var record=new AtomicActionRecord(request.ActionId);
			if(!atomicActions.TryAdd(request.ActionId,record)) throw new RpcException("action_exists","An atomic action with this action_id already exists.");
			var host=new RpcAtomicActionHost(this,req,request.ProcessId);
			try {
				var machine=new AtomicActionStateMachine(actionLeases,host);
				var result=await machine.RunAsync(request,action,cancellationToken,record.Cancelled.Token).ConfigureAwait(false);
				if(shutdown.IsCancellationRequested && result.Status.InterruptionReason==HookLab.Contracts.InterruptionReason.client_disconnected)
					result.Status=new HookLab.Contracts.AtomicActionStatus(result.Status.ActionOutcome,HookLab.Contracts.InterruptionReason.ui_shutdown,result.Status.CleanupOutcome,result.Status.ActionMayHaveExecuted,result.Status.AuditId,result.Status.ReconciliationOperation);
				record.Result=result; return result;
			}
			finally { host.Dispose(); record.Completed=true; }
		}

		object GetAtomicActionStatus(RpcRequest req) {
			RequireAtomicOperationVersion(req);
			var id=(string?)req.Arguments["action_id"] ?? throw new RpcException("invalid_arguments","action_id is required.");
			if(!atomicActions.TryGetValue(id,out var record)) throw new RpcException("action_not_found","No atomic action has this action_id.");
			return new { schema_version=1,action_id=id,completed=record.Completed,result=record.Result };
		}

		object CancelAtomicAction(RpcRequest req) {
			RequireAtomicOperationVersion(req);
			var id=(string?)req.Arguments["action_id"] ?? throw new RpcException("invalid_arguments","action_id is required.");
			if(!atomicActions.TryGetValue(id,out var record)) throw new RpcException("action_not_found","No atomic action has this action_id.");
			record.Cancelled.Cancel(); return new { schema_version=1,action_id=id,cancel_requested=true,completed=record.Completed };
		}

		IAtomicAction CreateAtomicAction(string kind,RpcRequest req) => kind switch {
			"capture" => new RpcExpressionAction(this,req,kind,mutating:false),
			"assignment" => new RpcExpressionAction(this,req,kind,mutating:true),
			"method_invocation" => new RpcExpressionAction(this,req,kind,mutating:true),
			_ => throw new RpcException("invalid_arguments","action_kind must be capture, assignment, or method_invocation."),
		};
		static void RequireAtomicOperationVersion(RpcRequest req) { if((int?)req.Arguments["operation_version"]!=1) throw new RpcException("incompatible_operation","operation_version 1 is required for this atomic-action operation."); }

		sealed class AtomicActionRecord {
			public AtomicActionRecord(string id) { Id=id; }
			public string Id { get; }
			public CancellationTokenSource Cancelled { get; }=new CancellationTokenSource();
			public volatile bool Completed;
			public AtomicActionResult? Result;
		}

		sealed class RpcExpressionAction : IAtomicAction {
			static readonly Regex AddressablePath=new Regex(@"^(?:this\.)?@?[A-Za-z_][A-Za-z0-9_]*(?:\.@?[A-Za-z_][A-Za-z0-9_]*)*$",RegexOptions.CultureInvariant);
			readonly RpcHost owner; readonly RpcRequest source; readonly bool mutating;
			public RpcExpressionAction(RpcHost owner,RpcRequest source,string kind,bool mutating) { this.owner=owner; this.source=source; this.mutating=mutating; Kind=kind; }
			public string Kind { get; }
			RpcRequest AtStop(AtomicActionContext context) {
				var clone=new RpcRequest { Operation=source.Operation,Arguments=(JsonObject)source.Arguments.DeepClone(),DeadlineUtc=source.DeadlineUtc };
				clone.Arguments["thread_id"]=context.Stop.ThreadId; clone.Arguments["frame_index"]=0; return clone;
			}
			public async Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken token) {
				var req=AtStop(context);
				if(Kind=="capture") { req.Arguments["expression"]=Path("capture_path"); req.Arguments["allow_func_eval"]=false; req.Arguments["allow_side_effects"]=false; var value=await owner.EvaluateAsync(req,token).ConfigureAwait(false); return new AtomicActionExecution { Completed=value.Error is null,MayHaveExecuted=false,Evidence=ProtocolJson.Serialize(value),Error=value.Error }; }
				if(Kind=="assignment") { req.Arguments["expression"]=Path("assignment_target"); req.Arguments["value"]=Scalar(source.Arguments["serialized_value"],"serialized_value"); req.Arguments["allow_func_eval"]=false; var value=await owner.SetValueAsync(req,token).ConfigureAwait(false); return new AtomicActionExecution { Completed=value.Assigned,MayHaveExecuted=value.CompilerError!=true,Evidence=ProtocolJson.Serialize(value),Error=value.Error }; }
				req.Arguments["expression"]=Invocation();
				var invoked=await owner.InvokeExpressionAsync(req,"method_invocation",token).ConfigureAwait(false);
				return new AtomicActionExecution { Completed=invoked.Completed,MayHaveExecuted=invoked.CompilerError!=true,Evidence=ProtocolJson.Serialize(invoked),Error=invoked.Error };
			}
			public async Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken token) {
				var expression=(string?)source.Arguments["verification_path"];
				if(String.IsNullOrWhiteSpace(expression)) return new AtomicActionVerification { Verified=true,Evidence=execution.Evidence };
				if(!AddressablePath.IsMatch(expression!)) throw new RpcException("invalid_arguments","verification_path must be a local, argument, field, or dotted field path; calls and operators are forbidden.");
				var req=AtStop(context); req.Arguments["expression"]=expression; req.Arguments["allow_func_eval"]=false; req.Arguments["allow_side_effects"]=false;
				var value=await owner.EvaluateAsync(req,token).ConfigureAwait(false);
				var expected=source.Arguments["verification_expected"]?.ToJsonString();
				var evidence=ProtocolJson.Serialize(value); var raw=value.Value is null ? "null" : ProtocolJson.Serialize(value.Value); var verified=value.Error is null && (expected is null || String.Equals(raw,expected,StringComparison.Ordinal));
				return new AtomicActionVerification { Verified=verified,Evidence=evidence,Error=verified?null:value.Error ?? "Verification value did not match verification_expected." };
			}
			string Path(string name) { var value=(string?)source.Arguments[name]; if(String.IsNullOrWhiteSpace(value) || !AddressablePath.IsMatch(value!)) throw new RpcException("invalid_arguments",name+" must be a local, argument, field, or dotted field path; calls and operators are forbidden."); return value!; }
			string Invocation() { var target=Path("invocation_target"); var arguments=source.Arguments["invocation_arguments"] as JsonArray ?? new JsonArray(); return target+"("+String.Join(",",arguments.Select((value,index)=>Scalar(value,"invocation_arguments["+index+"]")))+")"; }
			static string Scalar(JsonNode? value,string name) { if(value is null) return "null"; if(value is not JsonValue) throw new RpcException("invalid_arguments",name+" must contain only JSON scalar values."); var text=value.ToJsonString(); if(text=="null" || text=="true" || text=="false" || text.Length>0 && (text[0]=='\"' || text[0]=='-' || Char.IsDigit(text[0]))) return text; throw new RpcException("invalid_arguments",name+" is not a supported JSON scalar."); }
		}

		sealed class RpcAtomicActionHost : IAtomicActionHost,IDisposable {
			readonly RpcHost owner; readonly RpcRequest source; readonly ConcurrentDictionary<Guid,TaskCompletionSource<OwnedBreakpointHit>> hits=new ConcurrentDictionary<Guid,TaskCompletionSource<OwnedBreakpointHit>>();
			readonly int requestedProcessId; int expectedContinue; DbgProcess? process; DbgRuntime? runtime; DbgModule? module; bool disposed;
			public RpcAtomicActionHost(RpcHost owner,RpcRequest source,int requestedProcessId) { this.owner=owner; this.source=source; this.requestedProcessId=requestedProcessId; owner.manager.IsRunningChanged+=Manager_IsRunningChanged; }
			public long CaptureEventCursor() { lock(owner.sync) return owner.events.LastEventId; }
			public PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot) => PatchedTargetState.unknown; // T09 supplies probe inventory; absence is never reported as proof of no patch.
			public async Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> callback,CancellationToken token) {
				DbgDotNetCodeLocation location=await owner.OnDebuggerAsync(()=>{
					module=owner.FindModule(source,slot.Module); runtime=module.Runtime; process=runtime.Process;
					var moduleId=owner.GetModuleId(module) ?? throw new RpcException("capability_unsupported","The selected module cannot carry an owned breakpoint.");
					return owner.locations.Create(moduleId,slot.MethodToken,slot.IlOffset);
				},token).ConfigureAwait(false);
				OwnedBreakpoint? created=null;
				created=await owner.ownedBreakpoints.AddOwnerAsync(runtime!,location,(in OwnedBreakpointHit hit)=>{
					if(created is null) return true;
					var tcs=hits.GetOrAdd(created.OwnerToken,_=>new TaskCompletionSource<OwnedBreakpointHit>(TaskCreationOptions.RunContinuationsAsynchronously)); tcs.TrySetResult(hit); return true;
				},token).ConfigureAwait(false);
				hits.TryAdd(created.OwnerToken,new TaskCompletionSource<OwnedBreakpointHit>(TaskCreationOptions.RunContinuationsAsynchronously));
				return new RpcOwnedBreakpoint(created);
			}
			public Task ContinueAsync(Action<Action> authorize,CancellationToken token) => owner.OnDebuggerAsync(()=>{ Interlocked.Exchange(ref expectedContinue,1); authorize(()=>process!.Run()); return true; },token);
			public async Task<AtomicActionStop> WaitForOwnedStopAsync(Guid token,long cursor,CancellationToken cancellationToken) {
				if(!hits.TryGetValue(token,out var tcs)) throw new InvalidOperationException("Owned breakpoint hit waiter is missing.");
				EventHandler<DbgMessageProcessExitedEventArgs> exited=(_,e)=>{ if(e.Process.Id==requestedProcessId) tcs.TrySetException(new AtomicActionInterruptedException(HookLab.Contracts.InterruptionReason.target_exited,"The target exited before reaching the action slot.")); };
				EventHandler<DbgMessageAppDomainUnloadedEventArgs> unloaded=(_,e)=>{ if(module?.AppDomain==e.AppDomain) tcs.TrySetException(new AtomicActionInterruptedException(HookLab.Contracts.InterruptionReason.appdomain_unloaded,"The target AppDomain unloaded before reaching the action slot.")); };
				owner.manager.MessageProcessExited+=exited; owner.manager.MessageAppDomainUnloaded+=unloaded;
				OwnedBreakpointHit hit;
				try { hit=await WaitAsync(tcs.Task,cancellationToken).ConfigureAwait(false); }
				finally { owner.manager.MessageProcessExited-=exited; owner.manager.MessageAppDomainUnloaded-=unloaded; }
				return await owner.OnDebuggerAsync(()=>{
					var thread=hit.Thread ?? throw new AtomicActionInterruptedException(HookLab.Contracts.InterruptionReason.target_exited,"The target exited before the owned stop supplied a thread.");
					var states=thread.State.Select(value=>value.State).ToArray(); var evaluable=owner.EvaluationBlocker(thread,states) is null;
					return new AtomicActionStop(runtime!.Guid.ToString("D"),module!.AppDomain?.Id.ToString(CultureInfo.InvariantCulture) ?? "default",process!.Id,ThreadId(thread),module.Filename,hit.Location.Token,hit.Location.Offset,evaluable);
				},cancellationToken).ConfigureAwait(false);
			}
			public Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? stop,CancellationToken token) => Task.FromResult(request.NearbyOffsets.Length==0 ? null : new AtomicActionSlot(request.Module,request.MethodToken,request.NearbyOffsets[0]));
			public Task ReleaseTemporaryHandlesAsync(CancellationToken token)=>Task.CompletedTask;
			public Task ResumeAsync(Action<Action> authorize,CancellationToken token)=>owner.OnDebuggerAsync(()=>{ if(process is not null && !process.IsRunning) { Interlocked.Exchange(ref expectedContinue,1); authorize(()=>process.Run()); } return true; },token);
			public Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken token)=>owner.OnDebuggerAsync(()=>new AtomicActionFinalState { SessionActive=owner.sessionId is not null,ProcessActive=process is not null && owner.manager.Processes.Contains(process),IsRunning=process?.IsRunning==true,IsPaused=process is not null && !process.IsRunning,StopId=owner.stopId },token);
			static async Task<T> WaitAsync<T>(Task<T> task,CancellationToken token) { var canceled=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); using(token.Register(()=>canceled.TrySetCanceled(token))) return await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false); }
			void Manager_IsRunningChanged(object? sender,EventArgs e) {
				if(disposed || process is null) return;
				if(!owner.manager.IsDebugging || !owner.manager.Processes.Contains(process)) return;
				if(process.IsRunning) { if(Interlocked.Exchange(ref expectedContinue,0)==1) return; owner.actionLeases.ReportExternalDebuggerAction(process.Id,"engine_continue"); return; }
				if(hits.Values.Any(value=>value.Task.IsCompleted)) return;
				owner.actionLeases.ReportExternalDebuggerAction(process.Id,"engine_pause");
			}
			public void Dispose() { if(disposed) return; disposed=true; owner.manager.IsRunningChanged-=Manager_IsRunningChanged; }
		}

		sealed class RpcOwnedBreakpoint : IAtomicActionBreakpoint {
			readonly OwnedBreakpoint value; public RpcOwnedBreakpoint(OwnedBreakpoint value)=>this.value=value; public Guid OwnerToken=>value.OwnerToken; public string? BindError=>value.BindError;
			public async Task<bool> WaitBoundAsync(CancellationToken token)=>(await value.WaitBoundAsync(token).ConfigureAwait(false))==OwnedBreakpointState.Bound;
			public Task ReleaseAsync(CancellationToken token)=>value.ReleaseAsync(token); public void Dispose()=>value.Dispose();
		}
	}
}
