using dgSpy.Protocol;
using System.Text.Json.Nodes;

namespace dgSpy.Gateway;

public sealed class GatewayControlException : Exception {
	public string Code { get; }
	public GatewayControlException(string code,string message) : base(message) { Code=code; }
}

public sealed class McpClientSessions {
	public const string Header="Mcp-Session-Id";
	public const string LegacyClient="legacy-local";
	readonly object sync=new();
	readonly Dictionary<string,DateTime> seen=new(StringComparer.Ordinal);
	readonly TimeSpan idleTimeout;
	public McpClientSessions() : this(TimeSpan.FromSeconds(ReadTimeout())) { }
	internal McpClientSessions(TimeSpan idleTimeout) { this.idleTimeout=idleTimeout; }
	static int ReadTimeout() => int.TryParse(Environment.GetEnvironmentVariable("DGSPY_CONTROLLER_IDLE_SECONDS"),out var seconds) ? Math.Max(30,seconds) : 300;
	public string Create() { var id=Guid.NewGuid().ToString("N"); Touch(id); return id; }
	public string Resolve(string? presented) { var id=string.IsNullOrWhiteSpace(presented) ? LegacyClient : presented.Trim(); Touch(id); return id; }
	public void Touch(string clientId) { lock(sync) seen[clientId]=DateTime.UtcNow; }
	public bool IsActive(string clientId) { lock(sync) return seen.TryGetValue(clientId,out var last) && DateTime.UtcNow-last<=idleTimeout; }
	/// <summary>When this client's lease lapses if it never calls again, or null if it is already unknown.
	/// A caller that lost its transport cannot renew the lease and cannot claim the session until then, so
	/// without this it can only poll blind.</summary>
	public DateTime? ExpiresUtc(string clientId) { lock(sync) return seen.TryGetValue(clientId,out var last) ? last+idleTimeout : (DateTime?)null; }
	internal void SetLastSeen(string clientId,DateTime value) { lock(sync) seen[clientId]=value; }
}

public sealed class SessionControllers {
	readonly object sync=new();
	readonly Dictionary<string,SessionControllerInfo> owners=new(StringComparer.Ordinal);
	readonly McpClientSessions clients;
	public SessionControllers(McpClientSessions clients) { this.clients=clients; }
	public SessionControllerInfo Claim(string sessionId,string? hostId,string clientId) {
		lock(sync) {
			if(owners.TryGetValue(sessionId,out var current) && current.ClientId!=clientId && clients.IsActive(current.ClientId))
				throw new GatewayControlException("session_owned",$"Session '{sessionId}' is controlled by another active MCP session.");
			var claimed=new SessionControllerInfo(sessionId,hostId,clientId,DateTime.UtcNow); owners[sessionId]=claimed; return claimed;
		}
	}
	public SessionControllerInfo Authorize(string sessionId,string clientId) {
		lock(sync) {
			if(!owners.TryGetValue(sessionId,out var current)) throw new GatewayControlException("session_unowned",$"Session '{sessionId}' has no controller. Call claim_session explicitly.");
			if(!clients.IsActive(current.ClientId)) { owners.Remove(sessionId); throw new GatewayControlException("session_unowned",$"Session '{sessionId}' controller expired without changing the target. Call claim_session explicitly."); }
			if(current.ClientId!=clientId) throw new GatewayControlException("session_owned",$"Session '{sessionId}' is controlled by another active MCP session.");
			return current;
		}
	}
	public SessionControllerInfo Release(string sessionId,string clientId) {
		lock(sync) { var current=Authorize(sessionId,clientId); owners.Remove(sessionId); return current; }
	}
	public void ReleaseTerminal(string sessionId) { lock(sync) owners.Remove(sessionId); }
	public SessionControllerInfo? Get(string sessionId) { lock(sync) { if(!owners.TryGetValue(sessionId,out var value)) return null; if(clients.IsActive(value.ClientId)) return value; owners.Remove(sessionId); return null; } }
	/// <summary>The live controller and when its lease lapses. claim_session correctly refuses while another
	/// controller is active, so a caller whose own transport was replaced -- which happens whenever the MCP
	/// client restarts its stdio server -- has to wait the lease out. Reporting the deadline makes that wait
	/// deterministic instead of a poll loop against an unknown bound.</summary>
	public (SessionControllerInfo? Owner,DateTime? ExpiresUtc) Inspect(string sessionId) {
		lock(sync) { var owner=Get(sessionId); return (owner,owner is null ? null : clients.ExpiresUtc(owner.ClientId)); }
	}
}

public sealed record SessionControllerInfo(string SessionId,string? HostId,string ClientId,DateTime ClaimedUtc);

public static class MutationGuards {
	static readonly HashSet<string> Lifecycle=new(StringComparer.Ordinal) { "detach","terminate","restart" };
	static readonly HashSet<string> Breakpoints=new(StringComparer.Ordinal) { "set_il_breakpoint","set_breakpoint","remove_breakpoint","clear_breakpoints","update_breakpoint","set_exception_breakpoint","set_module_breakpoint","update_module_breakpoint","remove_module_breakpoint","import_breakpoints","set_exception_policy","remove_exception_policy","restore_exception_defaults" };
	// cancel_atomic_action is deliberately not stop-bound and carries no expected_execution_version: the
	// action it cancels moves the stop underneath the caller, so any value they could supply is stale by
	// construction. Its authorization is the record's captured session and process generation instead.
	static readonly HashSet<string> StopBound=new(StringComparer.Ordinal) { "run_atomic_action","start_atomic_action","step_into","step_over","step_out","set_value","invoke_method","create_object","set_instruction_pointer","create_object_id","write_value_export" };
	/// <summary>Mutations that deliberately carry no <c>expected_*_version</c>, because no value the caller
	/// could hold would be guarding anything. <c>cancel_atomic_action</c> is the case: the action it cancels
	/// is what moves execution_version and stop_id, and the run only hands out a fresh vector on its own
	/// response - which arrives when there is nothing left to cancel. Measurement went further and found the
	/// counter did not move during an observed action at all, so the guard was neither necessary nor
	/// sufficient. Authorization there is the record's captured session and process generation instead.</summary>
	static readonly HashSet<string> Unguarded=new(StringComparer.Ordinal) { "cancel_atomic_action" };
	public static bool RequiresVersionGuard(string operation) => !Unguarded.Contains(operation);
	public static string Scope(string operation) => Lifecycle.Contains(operation) ? "lifecycle" : Breakpoints.Contains(operation) ? "breakpoints" : "execution";
	public static string Argument(string operation) => "expected_"+Scope(operation)+"_version";
	public static string StateProperty(string operation) => Scope(operation)+"_version";
	public static bool RequiresStop(string operation) => StopBound.Contains(operation);
}

public sealed class GatewayAccessPolicy {
	public string Mode { get; }
	public GatewayAccessPolicy() { var configured=Environment.GetEnvironmentVariable("DGSPY_ACCESS_MODE")?.Trim().ToLowerInvariant(); Mode=configured is null or "" or "full-control" ? "full-control" : configured=="inspect-only" ? configured : throw new InvalidOperationException("DGSPY_ACCESS_MODE must be full-control or inspect-only."); }
	internal GatewayAccessPolicy(string mode) { Mode=mode; }
	public void AuthorizeMutation(string operation) { if(Mode=="inspect-only") throw new GatewayControlException("access_denied",$"Gateway access mode 'inspect-only' denies '{operation}'."); }
	/// <summary>Outer provider boundary for T10. Capture once before dispatch so reload cannot widen work
	/// already in flight; the extension must independently capture and demand its authoritative decision.</summary>
	public GatewayCapabilityDecision CaptureCapability(string hostId,string provider,string operation,CapabilityPermission permission,string? policyPath=null) => CapabilityPolicySnapshot.Load(hostId,policyPath,Mode).CaptureDecision(provider,operation,permission);
}

public sealed class GatewayAuditLog {
	readonly object sync=new(); readonly string path; readonly long maxBytes;
	public GatewayAuditLog() : this(Environment.GetEnvironmentVariable("DGSPY_AUDIT_FILE") ?? Path.Combine(Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy"),"gateway-audit.jsonl"),5*1024*1024) { }
	internal GatewayAuditLog(string path,long maxBytes) { this.path=path; this.maxBytes=maxBytes; }
	public void Write(string auditId,string clientId,string? hostId,string? sessionId,string operation,long? expectedVersion,string outcome,string? errorCode) {
		var record=new { timestamp_utc=DateTime.UtcNow.ToString("O"),audit_id=auditId,controller_id=clientId,host_id=hostId,session_id=sessionId,operation,expected_version_scope=MutationGuards.Scope(operation),expected_version=expectedVersion,outcome,error_code=errorCode };
		var line=ProtocolJson.Serialize(record)+Environment.NewLine;
		try { lock(sync) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); if(File.Exists(path) && new FileInfo(path).Length+line.Length>maxBytes) { var previous=path+".1"; if(File.Exists(previous)) File.Delete(previous); File.Move(path,previous); } File.AppendAllText(path,line); } } catch { }
	}
}

public sealed class GatewayToolExecutor {
	readonly HostRouter router; readonly SessionControllers controllers; readonly GatewayAccessPolicy access; readonly GatewayAuditLog audit; readonly DeploymentService deployments;
	public GatewayToolExecutor(HostRouter router,SessionControllers controllers,GatewayAccessPolicy access,GatewayAuditLog audit,DeploymentService deployments) { this.router=router; this.controllers=controllers; this.access=access; this.audit=audit; this.deployments=deployments; }
	internal GatewayToolExecutor(HostRouter router,SessionControllers controllers,GatewayAccessPolicy access,GatewayAuditLog audit) : this(router,controllers,access,audit,new DeploymentService()) { }
	public async Task<RpcResponse> ExecuteAsync(string operation,JsonObject arguments,string clientId,CancellationToken token) {
		var hostId=(string?)arguments["host_id"]; var sessionId=(string?)arguments["session_id"]; var guardArgument=MutationGuards.Argument(operation); var expected=(long?)arguments[guardArgument];
		var mutates=CapabilityCatalog.Operations.Any(item=>item.Operation==operation && item.MutatesSession); var controlMutation=operation is "claim_session" or "release_session";
		var auditId=mutates || controlMutation ? Guid.NewGuid().ToString("N") : null;
		try {
			if(IsGuidedOperation(operation)) { var guided=await ExecuteGuidedAsync(operation,arguments,clientId,token); audit.Write(Guid.NewGuid().ToString("N"),clientId,hostId,sessionId,operation,expected,guided.Error is null?"succeeded":"failed",guided.Error?.Code); return guided; }
			if(DeploymentService.IsGatewayOperation(operation)) {
				if(DeploymentService.IsMutation(operation)) access.AuthorizeMutation(operation);
				var result=await deployments.ExecuteAsync(operation,arguments,router,token);
				if(auditId is not null || DeploymentService.IsMutation(operation)) audit.Write(auditId ?? Guid.NewGuid().ToString("N"),clientId,hostId,sessionId,operation,null,"succeeded",null);
				return RpcResponse.Success(Guid.NewGuid().ToString("N"),result);
			}
			if(operation=="get_session_controller") {
				if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required.");
				var (current,expires)=controllers.Inspect(sessionId);
				if(current is null) return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,owned=false,controller_id=(string?)null,controller_is_caller=false,controller_expires_utc=(string?)null,controller_expires_in_seconds=(int?)null });
				var remaining=expires is null ? (int?)null : Math.Max(0,(int)Math.Ceiling((expires.Value-DateTime.UtcNow).TotalSeconds));
				return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,owned=true,controller_id=(string?)current.ClientId,controller_is_caller=current.ClientId==clientId,controller_expires_utc=expires?.ToString("O"),controller_expires_in_seconds=remaining });
			}
			if(operation=="claim_session") { access.AuthorizeMutation(operation); if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required."); var state=await GetStateAsync(arguments,token); var claimed=controllers.Claim(sessionId,hostId,clientId); var node=ProtocolJson.ToNode(state)!; audit.Write(auditId!,clientId,hostId,sessionId,operation,null,"succeeded",null); return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,controller_id=claimed.ClientId,state_version=(long?)node["state_version"],lifecycle_version=(long?)node["lifecycle_version"],execution_version=(long?)node["execution_version"],breakpoints_version=(long?)node["breakpoints_version"],stop_id=(string?)node["stop_id"] }); }
			if(operation=="release_session") { access.AuthorizeMutation(operation); if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required."); var released=controllers.Release(sessionId,clientId); audit.Write(auditId!,clientId,hostId,sessionId,operation,null,"succeeded",null); return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,released=true,controller_id=released.ClientId }); }
			if(mutates) {
				access.AuthorizeMutation(operation);
				if(operation is not ("attach" or "attach_endpoint" or "launch")) {
					if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments",$"'{operation}' requires session_id.");
					controllers.Authorize(sessionId,clientId);
					// Controller authority is demanded above for every mutation, including the deliberately
					// unguarded ones. What an unguarded mutation skips is only the version comparison: there
					// is no value the caller could hold that would be guarding anything, so demanding one
					// would refuse every correct call. cancel_atomic_action is the case, and it authorizes on
					// the record's captured session and process generation at the host instead.
					if(MutationGuards.RequiresVersionGuard(operation)) {
						if(!expected.HasValue) throw new GatewayControlException(MutationGuards.Scope(operation)+"_version_required",$"'{operation}' requires {guardArgument}.");
						var state=await GetStateAsync(arguments,token); var legacy=arguments[guardArgument] is null; var property=legacy ? "state_version" : MutationGuards.StateProperty(operation); var current=Version(state,property);
						if(expected.Value!=current) throw new GatewayControlException(legacy ? "stale_state" : "stale_"+MutationGuards.Scope(operation),$"Expected {property} {expected.Value}, current value is {current}.");
						if(!legacy && MutationGuards.RequiresStop(operation)) { var expectedStop=(string?)arguments["expected_stop_id"]; var currentStop=(string?)ProtocolJson.ToNode(state)!["stop_id"]; if(string.IsNullOrWhiteSpace(expectedStop)) throw new GatewayControlException("stop_id_required",$"'{operation}' requires expected_stop_id from the current paused state."); if(expectedStop!=currentStop) throw new GatewayControlException("stale_stop",$"Expected stop '{expectedStop}', current stop is '{currentStop ?? "none"}'."); }
					}
				}
			}
			var response=await router.CallAsync(new RpcRequest { Operation=operation,Arguments=(JsonObject)arguments.DeepClone(),DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds(operation)) },token);
			if(response.Error is null && (operation=="attach" || operation=="attach_endpoint" || operation=="launch")) { var started=ProtocolJson.ToNode(response.Result!)!.AsObject(); var created=(string?)started["session_id"]; if(!string.IsNullOrEmpty(created)) controllers.Claim(created,hostId,clientId); }
			if(response.Error is null && !string.IsNullOrWhiteSpace(sessionId) && IsTerminalLifecycleResult(operation,response.Result)) controllers.ReleaseTerminal(sessionId);
			if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,response.Error is null ? "succeeded" : "failed",response.Error?.Code);
			return response;
		}
		catch(GatewayControlException ex) { if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,"rejected",ex.Code); return RpcResponse.Failure(Guid.NewGuid().ToString("N"),ex.Code,ex.Message); }
		catch(Exception ex) { if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,"failed","internal_error"); return RpcResponse.Failure(Guid.NewGuid().ToString("N"),"internal_error",ex.Message); }
	}
	async Task<object> GetStateAsync(JsonObject source,CancellationToken token) { var args=new JsonObject(); if(source["host_id"] is not null) args["host_id"]=source["host_id"]!.DeepClone(); args["session_id"]=source["session_id"]!.DeepClone(); var response=await router.CallAsync(new RpcRequest { Operation="get_session_state",Arguments=args,DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds("get_session_state")) },token); if(response.Error is not null) throw new GatewayControlException(response.Error.Code,response.Error.Message); return response.Result!; }
	static long StateVersion(object state) => Version(state,"state_version");
	static long Version(object state,string property) => (long?)ProtocolJson.ToNode(state)![property] ?? throw new GatewayControlException("invalid_state",$"Host did not report {property}.");
	internal static bool IsTerminalLifecycleResult(string operation,object? result) {
		var node=result is null ? null : ProtocolJson.ToNode(result) as JsonObject;
		if(node is null) return false;
		if(operation=="detach") return (bool?)node["session_active"]==false;
		if(operation=="terminate") return node["process_ids"] is JsonArray processIds && processIds.Count==0;
		return false;
	}

	static bool IsGuidedOperation(string operation) => operation is "step_and_inspect" or "trace_calls" or "run_to_method" or "run_to_location";
	async Task<RpcResponse> ExecuteGuidedAsync(string operation,JsonObject arguments,string clientId,CancellationToken token) {
		access.AuthorizeMutation(operation);
		var sessionId=(string?)arguments["session_id"] ?? throw new GatewayControlException("invalid_arguments","session_id is required.");
		controllers.Authorize(sessionId,clientId);
		return operation switch {
			"step_and_inspect" => await StepAndInspectAsync(arguments,clientId,token),
			"trace_calls" => await TraceCallsAsync(arguments,clientId,token),
			_ => await RunToAsync(operation,arguments,token)
		};
	}
	async Task<RpcResponse> StepAndInspectAsync(JsonObject arguments,string clientId,CancellationToken token) {
		var kind=(string?)arguments["kind"] ?? "into"; var stepOperation=kind switch { "into"=>"step_into","over"=>"step_over","out"=>"step_out",_=>throw new GatewayControlException("invalid_arguments","kind must be into, over, or out.") };
		var stepArgs=BaseArgs(arguments); stepArgs["expected_execution_version"]=arguments["expected_execution_version"]?.DeepClone(); stepArgs["expected_stop_id"]=arguments["expected_stop_id"]?.DeepClone();
		var step=await ExecuteAsync(stepOperation,stepArgs,clientId,token); if(step.Error is not null) return step;
		var stepNode=ProtocolJson.ToNode(step.Result)!.AsObject(); var cursor=(long?)stepNode["cursor_event_id"] ?? 0; var timeout=Math.Clamp((int?)arguments["timeout_ms"] ?? 5000,1,10000);
		var wait=await RouteAsync("wait_for_stop",BaseArgs(arguments,new JsonObject { ["after_event_id"]=cursor,["timeout_ms"]=timeout }),token); if(wait.Error is not null) return wait;
		var waitNode=ProtocolJson.ToNode(wait.Result)!.AsObject(); var events=waitNode["events"]?.AsArray(); var stop=events?.LastOrDefault(); if((bool?)waitNode["timed_out"]==true||stop is null) return RpcResponse.Success(Guid.NewGuid().ToString("N"),Versioned(new JsonObject { ["step"]=ProtocolJson.ToNode(step.Result),["wait"]=waitNode.DeepClone(),["inspection"]=null },waitNode));
		var threadId=(string?)stop["thread_id"] ?? (string?)arguments["thread_id"];
		var maxFrames=Math.Clamp((int?)arguments["max_frames"] ?? 10,1,25); var inspectArgs=BaseArgs(arguments); if(threadId is not null) inspectArgs["thread_id"]=threadId;
		var stack=await RouteAsync("get_callstack",BaseArgs(inspectArgs,new JsonObject{{"max_frames",maxFrames}}),token);
		var frame=threadId is null ? null : await RouteAsync("get_frame",BaseArgs(inspectArgs,new JsonObject{{"frame_index",0},{"include",new JsonArray("locals","this")}}),token);
		var watches=await RouteAsync("list_watches",BaseArgs(inspectArgs,new JsonObject{{"frame_index",0}}),token); var exception=await RouteAsync("get_exception",BaseArgs(inspectArgs,new JsonObject{{"frame_index",0}}),token);
		return RpcResponse.Success(Guid.NewGuid().ToString("N"),Versioned(new JsonObject { ["step"]=ProtocolJson.ToNode(step.Result),["stop"]=stop.DeepClone(),["callstack"]=ProtocolJson.ToNode(ResultOrError(stack)),["frame"]=frame is null?null:ProtocolJson.ToNode(ResultOrError(frame)),["watches"]=ProtocolJson.ToNode(ResultOrError(watches)),["exception"]=ProtocolJson.ToNode(ResultOrError(exception)),["limitations"]=new JsonArray("optimized or inlined calls may be skipped","native and runtime calls are not traced","async continuations can change threads") },waitNode));
	}
	/// <summary>Lift the inner call's `versions` vector to the top of a composed result. A composition is
	/// exactly where the counters are hardest to guess — several guarded calls moved them and only the
	/// gateway saw the intermediate responses — so a caller that had to re-read state after every
	/// step_and_inspect got no benefit from the composite at all. The source is the call that observed
	/// the final state: the wait for the stepping tools, the cleanup removal for run_to_*.</summary>
	static JsonObject Versioned(JsonObject result,JsonObject? source) => VersionedFromVector(result,source?["versions"]);
	static JsonObject VersionedFromVector(JsonObject result,JsonNode? versions) { if(versions is not null) result["versions"]=versions.DeepClone(); return result; }
	async Task<RpcResponse> TraceCallsAsync(JsonObject arguments,string clientId,CancellationToken token) {
		var maxSteps=Math.Clamp((int?)arguments["max_steps"] ?? 25,1,100); var duration=Math.Clamp((int?)arguments["duration_ms"] ?? 10000,1,30000); var maxDepth=Math.Clamp((int?)arguments["max_depth"] ?? 20,1,50); var started=DateTime.UtcNow; var entries=new JsonArray(); var current=(JsonObject)arguments.DeepClone(); JsonObject? versions=null;
		for(var index=0;index<maxSteps && (DateTime.UtcNow-started).TotalMilliseconds<duration;index++) {
			current["kind"]="into"; current["timeout_ms"]=Math.Min(5000,duration-(int)(DateTime.UtcNow-started).TotalMilliseconds); current["max_frames"]=maxDepth;
			var result=await StepAndInspectAsync(current,clientId,token); if(result.Error is not null) return result; var node=ProtocolJson.ToNode(result.Result)!.AsObject();
			versions=node["versions"]?.AsObject() ?? ProtocolJson.ToNode(await GetStateAsync(current,token))!.AsObject();
			var stop=node["stop"]; if(stop is null) return RpcResponse.Success(Guid.NewGuid().ToString("N"),VersionedFromVector(new JsonObject { ["entries"]=entries,["completed"]=false,["reason"]="step_timeout",["steps"]=index+1,["limitations"]=new JsonArray(TraceLimitations.Select(value=>(JsonNode?)value).ToArray()) },versions));
			var stack=node["callstack"]; var text=stack?.ToJsonString() ?? ""; if(Matches(arguments,text)) entries.Add(new JsonObject { ["step"]=index+1,["stop"]=stop.DeepClone(),["callstack"]=stack?.DeepClone() });
			// The step's own response now carries the vector the next iteration must guard with, so the
			// trace no longer spends a get_session_state per step to re-read what it was just told.
			current["expected_execution_version"]=versions["execution_version"]?.DeepClone(); current["expected_stop_id"]=versions["stop_id"]?.DeepClone();
		}
		versions ??= ProtocolJson.ToNode(await GetStateAsync(current,token))!.AsObject();
		return RpcResponse.Success(Guid.NewGuid().ToString("N"),VersionedFromVector(new JsonObject { ["entries"]=entries,["completed"]=true,["reason"]="bound_reached",["steps"]=entries.Count,["limitations"]=new JsonArray(TraceLimitations.Select(value=>(JsonNode?)value).ToArray()) },versions));
	}
	static readonly string[] TraceLimitations={"best-effort managed sequence-point trace","optimized and inlined calls can be absent","native/runtime calls are unobservable","async continuations may move to another thread"};
	static bool Matches(JsonObject arguments,string text) { foreach(var property in new[]{"module_filter","namespace_filter","type_filter"}) { var filter=(string?)arguments[property]; if(!string.IsNullOrWhiteSpace(filter)&&!text.Contains(filter,StringComparison.OrdinalIgnoreCase)) return false; } return true; }
	async Task<RpcResponse> RunToAsync(string operation,JsonObject arguments,CancellationToken token) {
		Require(arguments,"expected_execution_version"); Require(arguments,"expected_breakpoints_version"); Require(arguments,"expected_stop_id");
		// module and expected_breakpoints_version belong to the temporary breakpoint, not to this call's
		// own guards: both set_breakpoint and set_il_breakpoint require them, and BaseArgs carries neither.
		// Without them every run_to_* attempt died on the host's "module is required" before the target
		// ever ran, which read as a caller mistake because the caller had in fact passed module.
		var setOperation=operation=="run_to_method"?"set_breakpoint":"set_il_breakpoint"; var breakpointArgs=BaseArgs(arguments); foreach(var name in new[]{"module_id","module","type","method","signature","method_token","il_offset","expected_breakpoints_version"}) if(arguments[name] is not null) breakpointArgs[name]=arguments[name]!.DeepClone();
		var created=await RouteAsync(setOperation,breakpointArgs,token); if(created.Error is not null) return created; var createdNode=ProtocolJson.ToNode(created.Result)!.AsObject(); var breakpointId=(int?)createdNode["breakpoint_id"] ?? throw new GatewayControlException("invalid_state","Host returned no temporary breakpoint id."); var cursor=(long?)createdNode["cursor_event_id"] ?? 0;
		// Filled by the cleanup below. The vector the caller needs is the one after the temporary
		// breakpoint came back out, not the one the wait saw: removing it moves breakpoints_version
		// again, so stamping from the wait would hand back a guard that is stale on arrival.
		RpcResponse? completed=null;
		try {
			// Scoped guard, not the deprecated alias: expected_state_version is compared against state_version,
			// which counts every state change in the session, while the caller's guard is an execution_version.
			// The two are equal only until something else moves state, so the alias turned every run_to_* into
			// a stale_state whose numbers ("expected 2, current 14") described a guard the caller never passed.
			var continueArgs=BaseArgs(arguments,new JsonObject { ["expected_execution_version"]=arguments["expected_execution_version"]?.DeepClone() }); var continued=await RouteAsync("continue",continueArgs,token); if(continued.Error is not null) return continued;
			var timeout=Math.Clamp((int?)arguments["timeout_ms"] ?? 10000,1,10000); var wait=await RouteAsync("wait_for_stop",BaseArgs(arguments,new JsonObject{{"after_event_id",cursor},{"timeout_ms",timeout}}),token);
			completed=wait.Error is null ? RpcResponse.Success(Guid.NewGuid().ToString("N"),new JsonObject { ["temporary_breakpoint"]=ProtocolJson.ToNode(created.Result),["wait"]=ProtocolJson.ToNode(wait.Result) }) : wait;
			return completed;
		} finally {
			// remove_breakpoint carries its own guard, and creating the temporary breakpoint already moved
			// breakpoints_version past whatever the caller passed in, so the cleanup has to re-read state
			// rather than reuse it. Getting this wrong leaves the temporary breakpoint behind, and a
			// breakpoint nobody set outlives the session and rebinds on the next attach.
			var cleanup=BaseArgs(arguments,new JsonObject{{"breakpoint_id",breakpointId}});
			try { cleanup["expected_breakpoints_version"]=ProtocolJson.ToNode(await GetStateAsync(arguments,CancellationToken.None))!["breakpoints_version"]?.DeepClone(); } catch (Exception) { }
			var removed=await RouteAsync("remove_breakpoint",cleanup,CancellationToken.None);
			// Mutating the object the response already holds: the returned RpcResponse is the same
			// reference either way, so this reaches the caller even though the return ran first.
			if(completed?.Result is JsonObject body && removed.Error is null) Versioned(body,ProtocolJson.ToNode(removed.Result) as JsonObject);
		}
	}
	async Task<RpcResponse> RouteAsync(string operation,JsonObject args,CancellationToken token) => await router.CallAsync(new RpcRequest { Operation=operation,Arguments=args,DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds(operation)) },token);
	static JsonObject BaseArgs(JsonObject source,JsonObject? additions=null) { var result=new JsonObject(); foreach(var name in new[]{"host_id","session_id","thread_id"}) if(source[name] is not null) result[name]=source[name]!.DeepClone(); if(additions is not null) foreach(var pair in additions) result[pair.Key]=pair.Value?.DeepClone(); return result; }
	static object? ResultOrError(RpcResponse response) => response.Error is null?response.Result:new { error=response.Error };
	static void Require(JsonObject args,string name) { if(args[name] is null) throw new GatewayControlException("invalid_arguments",$"{name} is required."); }
}
