using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		// Every evaluation runs on the EvaluationQueue, never the dispatcher. Func-eval executes code
		// inside the target and can block for as long as the target takes; doing that on the dispatcher
		// would stall event delivery for the whole session. dnSpy has the same rule.
		//
		// Func-eval is opt-in per call and off by default. An expression that calls a property getter can
		// deadlock a target that is holding a lock, and it can mutate state the caller was inspecting.
		// NoFuncEval is the safe default; allow_func_eval says the caller accepted that.
		const int MaxMembers = 200;

		static DbgEvaluationOptions EvaluationOptions(bool allowFuncEval,bool allowSideEffects) {
			var options=DbgEvaluationOptions.Expression;
			if (!allowFuncEval) options|=DbgEvaluationOptions.NoFuncEval;
			if (!allowSideEffects) options|=DbgEvaluationOptions.NoSideEffects;
			return options;
		}
		static DbgValueNodeEvaluationOptions NodeOptions(bool allowFuncEval) =>
			allowFuncEval ? DbgValueNodeEvaluationOptions.None : DbgValueNodeEvaluationOptions.NoFuncEval;

		/// <summary>Resolves the caller's thread_id + frame_index to a live dnSpy frame. Runs on the
		/// dispatcher, because that is where DbgObject access belongs; the returned snapshot is then used
		/// on the evaluation thread, where it can go stale — see DescribeFrame for why that is rejected
		/// rather than patched up.</summary>
		async Task<CapturedFrame> CaptureFrameAsync(RpcRequest req,CancellationToken cancellationToken) {
			var requestedThreadId=(string?)req.Arguments["thread_id"];
			var frameIndex=Math.Max(0,(int?)req.Arguments["frame_index"] ?? 0);
			await WaitForDebuggerAsync(()=>IsTargetRunning!=false || manager.Processes.SelectMany(p=>p.Threads).Any(),cancellationToken,TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			var selectedThreadId=await OnDebuggerAsync(()=>{
				if (IsTargetRunning!=false) throw new RpcException("not_paused","Pause the session before evaluating.");
				if (!string.IsNullOrEmpty(requestedThreadId)) {
					var requested=manager.Processes.SelectMany(p=>p.Threads).FirstOrDefault(t=>ThreadId(t)==requestedThreadId)
						?? throw new RpcException("thread_not_found",$"Thread {requestedThreadId} is not active. Refresh list_threads and use an exact thread_id.");
					manager.CurrentThread.Current=requested;
					// The caller's choice is authoritative; do not read CurrentThread.Current back to
					// discover it. That assignment does not necessarily land before the read, so the
					// read returned whichever thread carried the stop and every frame below was then
					// taken from the wrong thread -- an error when that thread had no frame at the
					// index, and silently the wrong frame's data when it did. The wait on the next
					// line is what actually lets the assignment take effect.
					return ThreadId(requested);
				}
				return manager.CurrentThread.Current is null ? "" : ThreadId(manager.CurrentThread.Current);
			},cancellationToken).ConfigureAwait(false);
			if (selectedThreadId.Length==0) throw new RpcException("invalid_arguments","No thread is current. Pass thread_id from list_threads.");
			await WaitForDebuggerAsync(()=>IsTargetRunning!=false || (callStack.Frames.Frames.Count!=0 && ThreadId(callStack.Frames.Frames[0].Thread)==selectedThreadId),cancellationToken,TimeSpan.FromSeconds(3)).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>{
				if (IsTargetRunning!=false) throw new RpcException("not_paused","Pause the session before evaluating.");
				var frames=callStack.Frames.Frames.Where(f=>ThreadId(f.Thread)==selectedThreadId).ToArray();
				if (frameIndex>=frames.Length) throw new RpcException("frame_not_found",$"Thread {selectedThreadId} has no frame at index {frameIndex}. Refresh get_callstack and use an available frame_index.");
				var frame=frames[frameIndex];
				return new CapturedFrame(frame,languages.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid),new FrameInfo {
					FrameId=$"{sessionId}:{stateVersion}:{selectedThreadId}:{frameIndex}",ThreadId=selectedThreadId,FrameIndex=frameIndex,
					Module=frame.Module?.Filename ?? "",ModuleName=frame.Module?.Name ?? "",MethodToken=frame.FunctionToken,IlOffset=frame.FunctionOffset,
				});
			},cancellationToken).ConfigureAwait(false);
		}

		/// <summary>Runs a callback with a live evaluation context for the caller's frame, on the
		/// evaluation thread. The context is closed on every path — it owns engine-side resources.</summary>
		async Task<T> WithEvaluationAsync<T>(RpcRequest req,Func<CapturedFrame,DbgEvaluationInfo,T> callback,CancellationToken cancellationToken) {
			var captured=await CaptureFrameAsync(req,cancellationToken).ConfigureAwait(false);
			var timeoutMs=Math.Min(CapabilityCatalog.Limits.MaxEvaluationTimeoutMs,Math.Max(1,(int?)req.Arguments["timeout_ms"] ?? 1000));
			using var evaluation=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,cancellationToken);
			return await evaluations.RunAsync(()=>{
				FrameSnapshotGuard.EnsureOpen(captured.Frame.IsClosed,"The target resumed while this frame was being evaluated. Pause again and request a fresh snapshot.");
				// dnSpy forwards this deadline to the engine's func-eval implementation. CorDebug aborts a
				// timed-out eval and temporarily disables further func-eval if recovery itself fails.
				var context=captured.Language.CreateContext(captured.Frame,funcEvalTimeout:TimeSpan.FromMilliseconds(timeoutMs),cancellationToken:evaluation.Token);
				try { return callback(captured,new DbgEvaluationInfo(context,captured.Frame,evaluation.Token)); }
				finally { context.Close(); }
			},cancellationToken).ConfigureAwait(false);
		}

		EvaluatedValue DescribeNode(DbgValueNode node,DbgEvaluationInfo eval,string expression) {
			var name=new DbgStringBuilderTextWriter(); var type=new DbgStringBuilderTextWriter(); var display=new DbgStringBuilderTextWriter();
			// Error nodes still carry useful identity. In particular, a property blocked by NoFuncEval has
			// an error value but FormatName reports the property name; skipping it made every such child
			// fall back to the parent expression (eg. every property under `this` was named `this`).
			node.FormatName(eval,name,DbgValueFormatterOptions.None);
			if (node.ErrorMessage is null) {
				node.FormatActualType(eval,type,DbgValueFormatterTypeOptions.None,DbgValueFormatterOptions.None,null);
				node.FormatValue(eval,display,DbgValueFormatterOptions.None,null);
			}
			var value=node.Value;
			// CanEvaluateExpression only says this is a value row rather than a grouping row; it does not
			// promise the string parses. For a compiler-generated member it does not - see
			// ExpressionAddressability. Report no expression rather than one guaranteed to fail, and do not
			// fall back to the caller's expression here: that names the parent, not this member.
			var nodeExpression=node.CanEvaluateExpression && node.Expression.Length!=0 ? node.Expression : expression;
			if (!ExpressionAddressability.IsAddressable(nodeExpression)) nodeExpression="";
			// The "Static members" row is a grouping row whose expression is the declaring type name rather
			// than an expression, so passing it back returns "error CS0119: 'X' is a type, which is not valid
			// in the given context". Its members are addressable - dnSpy composes them as Type.Member, which
			// is legal - but the group itself is not, and there is no RPC path to expand a grouping row.
			if (node.ImageName==PredefinedDbgValueNodeImageNames.StaticMembers) nodeExpression="";
			return new EvaluatedValue {
				Expression=nodeExpression,
				Name=name.Text.Length==0 ? expression : name.Text,
				Type=type.Text,
				// Raw value and display text are kept apart on purpose: an agent that needs to compare or
				// compute wants the scalar, and one that needs to show something wants dnSpy's formatting.
				// Collapsing them forces every caller to parse display text back into a value.
				Display=display.Text,
				Value=value is not null && value.HasRawValue && value.ValueType!=DbgSimpleValueType.Void ? value.RawValue : null,
				HasRawValue=value is not null && value.HasRawValue && value.ValueType!=DbgSimpleValueType.Void,
				// "No raw value" is not "null". A null reference has a raw value of null; an optimized-away
				// or unavailable local has none at all, and the error message says which.
				Error=node.ErrorMessage,
				ReadOnly=node.IsReadOnly,
				CausesSideEffects=node.CausesSideEffects,
				HasChildren=node.HasChildren,
			};
		}

		DbgValueNode[] CreateNodes(CapturedFrame captured,DbgEvaluationInfo eval,string[] expressions,bool allowFuncEval,bool allowSideEffects) {
			var infos=expressions.Select(e=>new DbgExpressionEvaluationInfo(e,NodeOptions(allowFuncEval),EvaluationOptions(allowFuncEval,allowSideEffects),null)).ToArray();
			return captured.Language.ValueNodeFactory.Create(eval,infos).Select(r=>r.ValueNode).ToArray();
		}

		async Task<EvaluatedValue> EvaluateAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var expression=(string?)req.Arguments["expression"];
			if (string.IsNullOrWhiteSpace(expression)) throw new RpcException("invalid_arguments","expression is required.");
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			var allowSideEffects=(bool?)req.Arguments["allow_side_effects"] ?? false;
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var nodes=CreateNodes(captured,eval,new[]{expression!},allowFuncEval,allowSideEffects);
				try { return DescribeNode(nodes[0],eval,expression!); }
				finally { manager.Close(nodes); }
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<MutationResult> InvokeExpressionAsync(RpcRequest req,string capability,CancellationToken cancellationToken) {
			CheckSession(req);
			var expression=(string?)req.Arguments["expression"];
			if (string.IsNullOrWhiteSpace(expression)) throw new RpcException("invalid_arguments","expression is required.");
			var auditId=AuditMutation(req.Operation,expression!);
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var nodes=CreateNodes(captured,eval,new[]{expression!},allowFuncEval:true,allowSideEffects:true);
				try {
					var value=DescribeNode(nodes[0],eval,expression!);
					return new MutationResult { Completed=value.Error is null,CausesSideEffects=true,AuditId=auditId,Value=value.Error is null ? value : null,Error=value.Error,Capability=capability };
				}
				finally { manager.Close(nodes); }
			},cancellationToken).ConfigureAwait(false);
		}

		string AuditMutation(string operation,string detail) {
			var id=Guid.NewGuid().ToString("N");
			manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,$"dgSpy audit {id}: operation={operation} session={sessionId ?? "none"} side_effects=true detail={detail}");
			return id;
		}

		// Children of one expression, one level deep and paged. Depth is the caller's to control by
		// asking for a child's own expression: recursing here would be unbounded on any cyclic object
		// graph, and the caller cannot cancel a walk it did not ask for.
		async Task<MemberList> GetMembersAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var expression=(string?)req.Arguments["expression"];
			if (string.IsNullOrWhiteSpace(expression)) throw new RpcException("invalid_arguments","expression is required.");
			var offset=Math.Max(0,(int?)req.Arguments["offset"] ?? 0);
			var count=Math.Min(MaxMembers,Math.Max(1,(int?)req.Arguments["count"] ?? 50));
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var roots=CreateNodes(captured,eval,new[]{expression!},allowFuncEval,allowSideEffects:false);
				try {
					var root=roots[0];
					if (root.ErrorMessage is not null) throw new RpcException("evaluation_failed",root.ErrorMessage);
					if (root.HasChildren==false) return new MemberList { Expression=expression!,Total=0,Offset=offset,Members=Array.Empty<EvaluatedValue>(),SessionId=sessionId,StateVersion=stateVersion };
					var total=root.GetChildCount(eval);
					var pageCount=MemberPagination.Count(total,offset,count);
					DbgValueNode[] children;
					// One failed expansion is one error, not a page of rows. See ChildExpansionFailure.
					try { children=pageCount==0 ? Array.Empty<DbgValueNode>() : root.GetChildren(eval,(ulong)offset,pageCount,NodeOptions(allowFuncEval)); }
					catch (DbgValueNodeExpansionException ex) { throw ChildExpansionFailure.ToRpcException(ex.ParentExpression,ex.ErrorMessage); }
					try {
						return new MemberList {
							Expression=expression!,
							// Reported as a long even though dnSpy counts in ulong: a member count that does
							// not fit in a long is not a real object graph, and JSON has no ulong anyway.
							Total=total>long.MaxValue ? long.MaxValue : (long)total,
							Offset=offset,Truncated=(ulong)(offset+children.Length)<total,
							Members=children.Select(c=>DescribeNode(c,eval,expression!)).ToArray(),
							SessionId=sessionId,StateVersion=stateVersion,
						};
					}
					finally { manager.Close(children); }
				}
				finally { manager.Close(roots); }
			},cancellationToken).ConfigureAwait(false);
		}

		// Assignment always executes in the target, so it is side-effecting by definition and says so.
		// A compiler error means nothing ran; anything else means the target may have been touched.
		async Task<AssignmentResult> SetValueAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var expression=(string?)req.Arguments["expression"];
			var valueExpression=(string?)req.Arguments["value"];
			if (string.IsNullOrWhiteSpace(expression)) throw new RpcException("invalid_arguments","expression is required.");
			if (valueExpression is null) throw new RpcException("invalid_arguments","value is required.");
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var result=captured.Language.ExpressionEvaluator.Assign(eval,expression!,valueExpression,EvaluationOptions(allowFuncEval,allowSideEffects:true));
				if (result.Error is not null)
					return new AssignmentResult { Expression=expression!,Assigned=false,Error=result.Error,CompilerError=result.IsCompilerError,SessionId=sessionId,StateVersion=stateVersion };
				var nodes=CreateNodes(captured,eval,new[]{expression!},allowFuncEval,allowSideEffects:false);
				try { return new AssignmentResult { Expression=expression!,Assigned=true,Value=DescribeNode(nodes[0],eval,expression!),SessionId=sessionId,StateVersion=stateVersion }; }
				finally { manager.Close(nodes); }
			},cancellationToken).ConfigureAwait(false);
		}

		// The exception that stopped the target, from dnSpy's own exceptions provider rather than from a
		// caller-typed expression. Empty is a legitimate answer: most stops are not exception stops.
		async Task<EvaluatedValue[]> GetExceptionAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var nodes=captured.Language.ExceptionsProvider.GetNodes(eval,NodeOptions(allowFuncEval));
				try { return nodes.Select(n=>DescribeNode(n,eval,"$exception")).ToArray(); }
				finally { manager.Close(nodes); }
			},cancellationToken).ConfigureAwait(false);
		}

		// Watches are stored expressions, not retained value handles. A handle would go stale on the next
		// resume; an expression is re-evaluated against whatever frame the caller names, which is what
		// someone watching a value across several stops actually wants.
		readonly Dictionary<int,string> watches=new Dictionary<int,string>();
		int nextWatchId;

		WatchInfo AddWatch(RpcRequest req) {
			var expression=(string?)req.Arguments["expression"];
			if (string.IsNullOrWhiteSpace(expression)) throw new RpcException("invalid_arguments","expression is required.");
			lock(sync) {
				var existing=watches.FirstOrDefault(w=>w.Value==expression);
				if (existing.Value is not null) return new WatchInfo { WatchId=existing.Key,Expression=expression! };
				var id=++nextWatchId; watches[id]=expression!;
				return new WatchInfo { WatchId=id,Expression=expression! };
			}
		}
		WatchRemovalResult RemoveWatch(RpcRequest req) {
			var id=(int?)req.Arguments["watch_id"] ?? throw new RpcException("invalid_arguments","watch_id is required.");
			lock(sync) {
				if (!watches.Remove(id)) throw new RpcException("watch_not_found",$"Watch {id} does not exist. Refresh list_watches and use an exact watch_id.");
				return new WatchRemovalResult { WatchId=id,Removed=true };
			}
		}
		// Listing evaluates every watch against the requested frame in one pass. A watch that fails
		// reports its own error rather than failing the call: one bad expression must not hide the rest.
		async Task<WatchInfo[]> ListWatchesAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			KeyValuePair<int,string>[] stored; lock(sync) stored=watches.OrderBy(w=>w.Key).ToArray();
			if (stored.Length==0) return Array.Empty<WatchInfo>();
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			return await WithEvaluationAsync(req,(captured,eval)=>{
				var nodes=CreateNodes(captured,eval,stored.Select(s=>s.Value).ToArray(),allowFuncEval,allowSideEffects:false);
				try {
					return stored.Select((s,index)=>new WatchInfo { WatchId=s.Key,Expression=s.Value,Value=DescribeNode(nodes[index],eval,s.Value) }).ToArray();
				}
				finally { manager.Close(nodes); }
			},cancellationToken).ConfigureAwait(false);
		}

		// get_frame's `include`. The plan had separate get_locals / get_arguments / get_this tools; they
		// are one round trip against one evaluation context, and splitting them into three would mean
		// three frame captures for one paused frame. `locals` on FrameInfo stays primitive-only because
		// callers already depend on it; `values` is the complete picture, objects included.
		static readonly string[] KnownIncludes={"locals","this"};
		async Task<FrameInfo> GetFrameWithIncludesAsync(RpcRequest req,FrameInfo frame,string[] include,CancellationToken cancellationToken) {
			var unknown=include.Where(i=>Array.IndexOf(KnownIncludes,i)<0).ToArray();
			if (unknown.Length!=0) throw new RpcException("invalid_argument",$"Unknown include value(s) {string.Join(", ",unknown)}. Valid: {string.Join(", ",KnownIncludes)}.");
			var allowFuncEval=(bool?)req.Arguments["allow_func_eval"] ?? false;
			frame.Values=await WithEvaluationAsync(req,(captured,eval)=>{
				var results=new List<EvaluatedValue>();
				if (include.Contains("locals")) {
					// Arguments come back from the same provider as locals — dnSpy does not separate them,
					// which is why there is no separate "arguments" include pretending otherwise.
					var nodes=captured.Language.LocalsProvider.GetNodes(eval,NodeOptions(allowFuncEval),DbgLocalsValueNodeEvaluationOptions.None).Select(n=>n.ValueNode).ToArray();
					try { results.AddRange(nodes.Select(n=>DescribeNode(n,eval,n.CanEvaluateExpression ? n.Expression : ""))); }
					finally { manager.Close(nodes); }
				}
				if (include.Contains("this")) {
					var nodes=CreateNodes(captured,eval,new[]{"this"},allowFuncEval,allowSideEffects:false);
					// A static method has no this, and dnSpy reports that as an evaluation error. That is
					// not worth surfacing as a failed include.
					try { if (nodes[0].ErrorMessage is null) results.Add(DescribeNode(nodes[0],eval,"this")); }
					finally { manager.Close(nodes); }
				}
				return results.ToArray();
			},cancellationToken).ConfigureAwait(false);
			return frame;
		}

		// Modules of every runtime in the session. This is also the answer to "what module path do I pass
		// to set_il_breakpoint": a frame reports one, but a caller that has not stopped anywhere yet has
		// no other way to find one.
		async Task<ModuleInfo[]> ListModulesAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>modules.Select(m=>new ModuleInfo {
				Name=m.Name,Filename=m.Filename,ProcessId=m.Process.Id,RuntimeGuid=m.Runtime.Guid.ToString("D"),
				IsDynamic=m.IsDynamic,IsInMemory=m.IsInMemory,IsOptimized=m.IsOptimized,Order=m.Order,
				Address=m.Address,Size=m.Size,Version=m.Version,
				// The engine-provided ModuleId includes the runtime discriminator needed by dynamic and
				// in-memory modules; modules for which no provider supplies one remain explicitly false.
				CanSetBreakpoint=CanCarryBreakpoint(m),
			}).OrderBy(m=>m.ProcessId).ThenBy(m=>m.Order).ToArray(),cancellationToken).ConfigureAwait(false);
		}
	}
}
