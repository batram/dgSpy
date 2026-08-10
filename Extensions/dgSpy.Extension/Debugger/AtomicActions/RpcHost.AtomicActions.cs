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
		readonly AtomicActionRecordStore atomicActions=new AtomicActionRecordStore();
		AtomicActionScheduler? atomicScheduler;
		AtomicActionScheduler AtomicScheduler => atomicScheduler ??= new AtomicActionScheduler(atomicActions);

		/// <summary>Everything an atomic action needs that can be decided without touching the debugger.
		/// Both entry points validate through this, so a rejected request is rejected identically and, on the
		/// asynchronous path, before any record exists to reconcile.</summary>
		sealed class AtomicActionPlan {
			public AtomicActionPlan(AtomicActionRequest request,IAtomicAction action,RpcRequest scoped) { Request=request; Action=action; Scoped=scoped; }
			public AtomicActionRequest Request { get; }
			public IAtomicAction Action { get; }
			public RpcRequest Scoped { get; }
		}

		AtomicActionPlan PrepareAtomicAction(RpcRequest req) {
			var request=ProtocolJson.FromNode<AtomicActionRequest>(req.Arguments["request"]) ?? throw new RpcException("invalid_arguments","request is required.");
			// From this boundary onward the state machine compares and reports the exact loaded-module
			// identity. The legacy display name remains accepted only for callers that did not supply one.
			if(!String.IsNullOrWhiteSpace(request.ModuleId)) request.Module=request.ModuleId;
			AtomicActionRequestScope.ValidateProcessIds((int?)req.Arguments["process_id"],request.ProcessId);
			request.DeadlineUtc=AtomicActionDeadline.Resolve(request.DeadlineUtc,(int?)req.Arguments["timeout_ms"],DateTime.UtcNow);
			var kind=(string?)req.Arguments["action_kind"] ?? throw new RpcException("invalid_arguments","action_kind is required.");
			var action=CreateAtomicAction(kind,req);
			return new AtomicActionPlan(request,action,AtomicActionRequestScope.ScopeModuleSearch(req,request));
		}

		async Task<AtomicActionResult> RunAtomicActionAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			RequireAtomicOperationVersion(req);
			var plan=PrepareAtomicAction(req);
			var request=plan.Request;
			var record=atomicActions.TryStart(request.ActionId,CurrentAtomicGeneration(request.ProcessId)) ?? throw new RpcException("action_exists","An atomic action with this action_id already exists or is still retained for reconciliation.");
			var host=new RpcAtomicActionHost(this,plan.Scoped,request.ProcessId);
			try {
				var machine=new AtomicActionStateMachine(actionLeases,host);
				var result=await machine.RunAsync(request,plan.Action,cancellationToken,record.Cancelled.Token,record.AdvanceToPhase).ConfigureAwait(false);
				result.Status=AtomicActionInterruptions.RemapForShutdown(result.Status,shutdown.IsCancellationRequested);
				atomicActions.Complete(record,result);
				await SettleAtomicResumeAsync(request,result).ConfigureAwait(false);
				return result;
			}
			catch(RpcException ex) { atomicActions.Complete(record,null,ex.Code,ex.Message); throw; }
			catch(Exception ex) { atomicActions.Complete(record,null,"internal_error",ex.Message); throw; }
			finally { host.Dispose(); }
		}

		/// <summary>
		/// The asynchronous entry point. It validates synchronously, reserves the action id, creates the
		/// retained record, schedules host-owned execution, and returns <c>accepted</c> - it does <b>not</b>
		/// wait for lease acquisition or breakpoint binding. Waiting for "armed" was the first draft and was
		/// the design-stopping flaw: a host connection reads one request and awaits its full dispatch before
		/// reading the next, so a start that waits for arming still blocks the only channel the caller has,
		/// and a hung bind is still uncancellable. Validation failure returns an RPC error and creates no
		/// record; an arming failure later becomes a retained terminal record carrying the error.
		/// </summary>
		object StartAtomicAction(RpcRequest req) {
			CheckSession(req);
			RequireAtomicOperationVersion(req);
			// disconnect_policy belongs to the blocking form only. Once this call deliberately returns, the
			// request lifetime ends *normally*, so cancel_on_disconnect and complete_on_disconnect describe
			// nothing: the action cannot read the success of its own start request as a client disconnect.
			// Refused rather than ignored, because silently dropping a policy the caller asked for is how a
			// caller ends up believing controller loss will cancel their target mutation.
			var policy=AtomicActionRequestScope.ResolveAsyncDisconnectPolicy(req.Arguments["request"]);
			var plan=PrepareAtomicAction(req);
			var request=plan.Request;
			request.DisconnectPolicy=policy;
			var acceptance=AtomicScheduler.Start(request.ActionId,CurrentAtomicGeneration(request.ProcessId),
				async record=>{
					var host=new RpcAtomicActionHost(this,plan.Scoped,request.ProcessId);
					try {
						var machine=new AtomicActionStateMachine(actionLeases,host);
						// CancellationToken.None as the client lifetime, deliberately: the run is host-owned
						// from acceptance, and the only lifetime that may end it is its own deadline or an
						// explicit cancel.
						var result=await machine.RunAsync(request,plan.Action,CancellationToken.None,record.Cancelled.Token,record.AdvanceToPhase).ConfigureAwait(false);
						result.Status=AtomicActionInterruptions.RemapForShutdown(result.Status,shutdown.IsCancellationRequested);
						await SettleAtomicResumeAsync(request,result).ConfigureAwait(false);
						return result;
					}
					finally { host.Dispose(); }
				},
				(reason,message)=>SynthesizeAtomicTerminal(request,reason,message));
			return acceptance;
		}

		/// <summary>A terminal record for a run that never produced one - cancelled while queued, or ended by
		/// host shutdown. It carries the requested slot and the effective deadline so the record is still a
		/// reconcilable account rather than a bare error string.</summary>
		static AtomicActionResult SynthesizeAtomicTerminal(AtomicActionRequest request,global::HookLab.Contracts.InterruptionReason reason,string message) =>
			new AtomicActionResult {
				RequestedSlot=new AtomicActionSlot(request.Module,request.MethodToken,request.IlOffset),
				EffectiveDeadlineUtc=request.DeadlineUtc.ToUniversalTime(),
				Error=message,
				Status=new global::HookLab.Contracts.AtomicActionStatus(global::HookLab.Contracts.ActionOutcome.trigger_not_reached,reason,
					global::HookLab.Contracts.CleanupOutcome.not_required,false,Guid.NewGuid().ToString("N"),null),
			};

		/// <summary>The session and process identity a record is bound to. A cancel has to match it, which is
		/// what replaces <c>expected_execution_version</c>: measurement showed that counter did not move
		/// during an action, and a value stamped onto the run's own response only reaches the caller once the
		/// run is over, so it could never have guarded a cancellation.</summary>
		AtomicActionGeneration CurrentAtomicGeneration(int processId) { lock(sync) return new AtomicActionGeneration(sessionId,processId,lifecycleVersion); }

		/// <summary>Cancels every in-flight background action and forces a terminal record onto anything that
		/// does not finish inside the bound. Returns how many had to be forced.</summary>
		internal Task<int> ReconcileAtomicActionsForShutdown(TimeSpan bound) =>
			AtomicScheduler.ShutdownAsync(bound,(reason,message)=>new AtomicActionResult {
				Status=new global::HookLab.Contracts.AtomicActionStatus(global::HookLab.Contracts.ActionOutcome.trigger_not_reached,reason,
					global::HookLab.Contracts.CleanupOutcome.ambiguous,false,Guid.NewGuid().ToString("N"),AtomicActionStateMachine.StatusOperation),
				Error=message,
			});

		/// <summary>
		/// run_atomic_action is version-stamped, so the vector on its response has to be the one after its
		/// final resume applied. That resume is authorized on the debugger thread and its Continued event is
		/// recorded afterwards, so composing the answer immediately would stamp the paused vector and the
		/// caller's next guarded call would fail against a number this response had just handed it. Best
		/// effort by design, like SettleExecutionChangeAsync: a resume the engine declines must not turn a
		/// completed action into a failed call.
		/// </summary>
		async Task SettleAtomicResumeAsync(AtomicActionRequest request,AtomicActionResult result) {
			if(request.ResumePolicy!=AtomicActionResumePolicy.resume) return;
			if(result.Status.InterruptionReason==global::HookLab.Contracts.InterruptionReason.external_debugger_action) return;
			using var bound=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
			bound.CancelAfter(TimeSpan.FromMilliseconds(750));
			while(true) {
				// Read the cursor before re-testing the state, never after: an event recorded between the
				// two reads would otherwise be waited past and the wait would hang out its bound.
				var observed=events.LastEventId;
				lock(sync) { if(stopId is null) return; }
				try { await events.WaitForChangeAsync(observed,bound.Token).ConfigureAwait(false); }
				catch(OperationCanceledException) { return; }
			}
		}

		object GetAtomicActionStatus(RpcRequest req) {
			RequireAtomicOperationVersion(req);
			var id=(string?)req.Arguments["action_id"] ?? throw new RpcException("invalid_arguments","action_id is required.");
			if(!atomicActions.TryGet(id,out var record)) throw new RpcException("action_not_found","No atomic action has this action_id. Terminal records are retained for "+(int)AtomicActionRecordStore.TerminalRetention.TotalMinutes+" minutes.");
			// A run that failed before it could produce a result reports why, rather than a bare
			// completed=true with nothing in it.
			// Read completed *after* the phase, mirroring the order Complete writes them, so a status that
			// reports completed=false can never carry the terminal phase and vice versa.
			var phase=record.Phase;
			return new { schema_version=1,action_id=id,phase=phase.ToString(),completed=record.Completed,
				cancel_requested=record.CancelRequested,
				// There is deliberately no wait_atomic_action. A blocking wait would recreate the defect the
				// asynchronous shape removes: while the single registered channel waits, a cancel cannot
				// overtake it. The caller polls at the interval below and chooses between the next read and
				// a cancel.
				recommended_poll_after_ms=record.RecommendedPollAfterMs,
				result=record.Result,
				error=record.ErrorCode is null ? null : new { code=record.ErrorCode,message=record.ErrorMessage } };
		}

		/// <summary>
		/// Authorized by the record's captured session and process generation plus the exact action_id -
		/// not by <c>expected_execution_version</c>, which measurement showed did not move during any
		/// observed action, and which a canceller could not have obtained in time anyway: the run stamps its
		/// vector onto its own response, which only arrives once there is nothing left to cancel. Controller
		/// authority is enforced above this, by the Gateway's session controller.
		/// </summary>
		object CancelAtomicAction(RpcRequest req) {
			RequireAtomicOperationVersion(req);
			var id=(string?)req.Arguments["action_id"] ?? throw new RpcException("invalid_arguments","action_id is required.");
			if(!atomicActions.TryGet(id,out var record)) throw new RpcException("action_not_found","No atomic action has this action_id.");
			if(!record.Generation.Authorizes(CurrentAtomicGeneration(record.Generation.ProcessId)))
				throw new RpcException("action_not_found","This action_id belongs to an earlier session or process generation; there is nothing under it to cancel now.");
			var requested=record.TryCancel(out var alreadyRequested);
			return new { schema_version=1,action_id=id,cancel_requested=requested,already_requested=alreadyRequested,
				completed=record.Completed,phase=record.Phase.ToString() };
		}

		IAtomicAction CreateAtomicAction(string kind,RpcRequest req) => kind switch {
			"capture" => new RpcExpressionAction(this,req,kind,mutating:false),
			"assignment" => new RpcExpressionAction(this,req,kind,mutating:true),
			"method_invocation" => new RpcExpressionAction(this,req,kind,mutating:true),
			_ => throw new RpcException("invalid_arguments","action_kind must be capture, assignment, or method_invocation."),
		};
		static void RequireAtomicOperationVersion(RpcRequest req) { if((int?)req.Arguments["operation_version"]!=1) throw new RpcException("incompatible_operation","operation_version 1 is required for this atomic-action operation."); }

		sealed class RpcExpressionAction : IAtomicAction {
			static readonly Regex AddressablePath=new Regex(@"^(?:this\.)?@?[A-Za-z_][A-Za-z0-9_]*(?:\.@?[A-Za-z_][A-Za-z0-9_]*)*$",RegexOptions.CultureInvariant);
			readonly RpcHost owner; readonly RpcRequest source; readonly bool mutating;
			public RpcExpressionAction(RpcHost owner,RpcRequest source,string kind,bool mutating) { this.owner=owner; this.source=source; this.mutating=mutating; Kind=kind; }
			public string Kind { get; }
			/// <summary>These three actions leave nothing in the target to discover, so the action record is
			/// the whole reconciliation. An action that installs something names its own operation instead.</summary>
			public string? ReconciliationOperation=>null;
			RpcRequest AtStop(AtomicActionContext context) {
				var clone=new RpcRequest { Operation=source.Operation,Arguments=(JsonObject)source.Arguments.DeepClone(),DeadlineUtc=source.DeadlineUtc };
				clone.Arguments["thread_id"]=context.Stop.ThreadId; clone.Arguments["frame_index"]=0; return clone;
			}
			public async Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken token) {
				var req=AtStop(context);
				if(Kind=="capture") { req.Arguments["expression"]=Path("capture_path"); req.Arguments["allow_func_eval"]=false; req.Arguments["allow_side_effects"]=false; var value=await owner.EvaluateAsync(req,token).ConfigureAwait(false); return new AtomicActionExecution { Completed=value.Error is null,MayHaveExecuted=false,Evidence=ProtocolJson.Serialize(value),Error=value.Error }; }
				// set_value writes no audit line of its own, so an assignment has no second id to correlate.
				if(Kind=="assignment") { req.Arguments["expression"]=Path("assignment_target"); req.Arguments["value"]=Scalar(source.Arguments["serialized_value"],"serialized_value"); req.Arguments["allow_func_eval"]=false; var value=await owner.SetValueAsync(req,token).ConfigureAwait(false); return new AtomicActionExecution { Completed=value.Assigned,MayHaveExecuted=value.CompilerError!=true,Evidence=ProtocolJson.Serialize(value),Error=value.Error }; }
				// MutationAuditId carries the id AuditMutation wrote to the debugger output log, so the
				// audited line can be tied to the audit_id this call reports.
				req.Arguments["expression"]=Invocation();
				var invoked=await owner.InvokeExpressionAsync(req,"method_invocation",token).ConfigureAwait(false);
				return new AtomicActionExecution { Completed=invoked.Completed,MayHaveExecuted=invoked.CompilerError!=true,Evidence=ProtocolJson.Serialize(invoked),Error=invoked.Error,MutationAuditId=invoked.AuditId };
			}
			/// <summary>An expression action installs nothing in the target: a capture reads, and an
			/// assignment or invocation is a value change the caller asked for, which undoing would be a
			/// second unrequested mutation rather than a rollback. So there is nothing to undo, and saying
			/// <c>not_required</c> is the truthful answer rather than a stub.</summary>
			public Task<AtomicActionCleanup> CleanupAsync(AtomicActionCleanupContext context,CancellationToken token) =>
				Task.FromResult(new AtomicActionCleanup { Outcome=global::HookLab.Contracts.CleanupOutcome.not_required });
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
				// The waiter exists before the breakpoint does. The handler used to close over the
				// OwnedBreakpoint that AddOwnerAsync had not returned yet, so a hit arriving first found
				// null, paused the engine anyway, and completed nothing: the action then waited out its
				// whole deadline reporting trigger_not_reached on a target that had reached the slot.
				var pending=new TaskCompletionSource<OwnedBreakpointHit>(TaskCreationOptions.RunContinuationsAsynchronously);
				var created=await owner.ownedBreakpoints.AddOwnerAsync(runtime!,location,(in OwnedBreakpointHit hit)=>{
					pending.TrySetResult(hit); return true;
				},token).ConfigureAwait(false);
				hits[created.OwnerToken]=pending;
				return new RpcOwnedBreakpoint(created);
			}
			public Task ContinueAsync(Action<Action> authorize,CancellationToken token) => owner.OnDebuggerAsync(()=>{ Interlocked.Exchange(ref expectedContinue,1); authorize(()=>process!.Run()); return true; },token);
			public async Task<AtomicActionStop> WaitForOwnedStopAsync(Guid token,long cursor,CancellationToken cancellationToken) {
				if(!hits.TryGetValue(token,out var tcs)) throw new InvalidOperationException("Owned breakpoint hit waiter is missing.");
				EventHandler<DbgMessageProcessExitedEventArgs> exited=(_,e)=>{ var interruption=AtomicActionInterruptions.ProcessExited(requestedProcessId,e.Process.Id); if(interruption is not null) tcs.TrySetException(interruption); };
				EventHandler<DbgMessageAppDomainUnloadedEventArgs> unloaded=(_,e)=>{ var interruption=AtomicActionInterruptions.AppDomainUnloaded(module?.AppDomain==e.AppDomain); if(interruption is not null) tcs.TrySetException(interruption); };
				owner.manager.MessageProcessExited+=exited; owner.manager.MessageAppDomainUnloaded+=unloaded;
				OwnedBreakpointHit hit;
				try { hit=await WaitAsync(tcs.Task,cancellationToken).ConfigureAwait(false); }
				finally { owner.manager.MessageProcessExited-=exited; owner.manager.MessageAppDomainUnloaded-=unloaded; }
				await SettleStopAsync(cancellationToken).ConfigureAwait(false);
				return await owner.OnDebuggerAsync(()=>{
					var thread=hit.Thread ?? throw AtomicActionInterruptions.MissingThread();
					// The whole probe, not `blocker is null`. Collapsing it lost which of three blockers had
					// fired and hid a probe that had failed outright, and the state machine then printed one
					// hardcoded unsafe-point sentence for all of them.
					var states=thread.State.Select(value=>value.State).ToArray();
					return new AtomicActionStop(runtime!.Guid.ToString("D"),module!.AppDomain?.Id.ToString(CultureInfo.InvariantCulture) ?? "default",process!.Id,ThreadId(thread),owner.ModuleIdOf(module),hit.Location.Token,hit.Location.Offset,owner.ProbeEvaluability(thread,states));
				},cancellationToken).ConfigureAwait(false);
			}
			/// <summary>
			/// The owned-breakpoint hit callback is raised by the engine while the stop is still being
			/// processed, so reading the thread's state immediately after it can observe a thread dnSpy has
			/// not marked stopped yet - which the evaluability check reads as a CorDebug-unsafe point. A live
			/// run reported reached_not_evaluable on two of five actions at a perfectly ordinary breakpoint
			/// for exactly that reason. Wait, boundedly, for the pause the hit caused to be visible; a wait
			/// that times out changes nothing and leaves the original reading to stand.
			/// </summary>
			async Task SettleStopAsync(CancellationToken token) {
				var deadline=DateTime.UtcNow.AddSeconds(2);
				while(DateTime.UtcNow<deadline) {
					if(await owner.OnDebuggerAsync(()=>process is null || !process.IsRunning,token).ConfigureAwait(false)) return;
					await Task.Delay(20,token).ConfigureAwait(false);
				}
			}
			public Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? stop,CancellationToken token) => Task.FromResult(NearbySlotSelector.Select(request,stop));
			public Task ReleaseTemporaryHandlesAsync(CancellationToken token)=>Task.CompletedTask;
			public Task ResumeAsync(Action<Action> authorize,CancellationToken token)=>owner.OnDebuggerAsync(()=>{ if(process is not null && !process.IsRunning) { Interlocked.Exchange(ref expectedContinue,1); authorize(()=>process.Run()); } return true; },token);
			public Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken token)=>owner.OnDebuggerAsync(()=>new AtomicActionFinalState { SessionActive=owner.sessionId is not null,ProcessActive=process is not null && owner.manager.Processes.Contains(process),IsRunning=process?.IsRunning==true,IsPaused=process is not null && !process.IsRunning,StopId=owner.stopId },token);
			static async Task<T> WaitAsync<T>(Task<T> task,CancellationToken token) { var canceled=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); using(token.Register(()=>canceled.TrySetCanceled(token))) return await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false); }
			void Manager_IsRunningChanged(object? sender,EventArgs e) {
				if(disposed || process is null) return;
				if(!owner.manager.IsDebugging || !owner.manager.Processes.Contains(process)) return;
				if(process.IsRunning) { if(Interlocked.Exchange(ref expectedContinue,0)==1) return; owner.actionLeases.ReportExternalDebuggerAction(process.Id,"engine_continue"); return; }
				if(hits.Values.Any(value=>value.Task.IsCompleted)) return;
				// The engine's running-state notification and the owned-breakpoint hit callback are two
				// separate deliveries, and a live run showed the notification arriving first: the action's
				// own arrival was then classified as external interference and every atomic action against
				// a real target died with external_debugger_action before it reached its slot. Deciding
				// synchronously cannot distinguish "someone else paused us" from "our stop, not yet
				// observed", so give the hit a bounded grace period and decide after it.
				_=ReportExternalPauseAsync(process);
			}
			const int ExternalPauseGraceMs=300;
			async Task ReportExternalPauseAsync(DbgProcess paused) {
				try {
					await Task.Delay(ExternalPauseGraceMs).ConfigureAwait(false);
					if(disposed || hits.Values.Any(value=>value.Task.IsCompleted)) return;
					// Back to the debugger thread to read DbgObject state: this continuation is on the pool.
					var stillPaused=await owner.OnDebuggerAsync(()=>owner.manager.IsDebugging && owner.manager.Processes.Contains(paused) && !paused.IsRunning,CancellationToken.None).ConfigureAwait(false);
					if(!stillPaused || disposed || hits.Values.Any(value=>value.Task.IsCompleted)) return;
					owner.actionLeases.ReportExternalDebuggerAction(paused.Id,"engine_pause");
				}
				catch { }
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
