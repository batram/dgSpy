using dgSpy.Protocol;
using Newtonsoft.Json.Linq;

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
}

public sealed record SessionControllerInfo(string SessionId,string? HostId,string ClientId,DateTime ClaimedUtc);

public sealed class GatewayAccessPolicy {
	public string Mode { get; }
	public GatewayAccessPolicy() { var configured=Environment.GetEnvironmentVariable("DGSPY_ACCESS_MODE")?.Trim().ToLowerInvariant(); Mode=configured is null or "" or "full-control" ? "full-control" : configured=="inspect-only" ? configured : throw new InvalidOperationException("DGSPY_ACCESS_MODE must be full-control or inspect-only."); }
	internal GatewayAccessPolicy(string mode) { Mode=mode; }
	public void AuthorizeMutation(string operation) { if(Mode=="inspect-only") throw new GatewayControlException("access_denied",$"Gateway access mode 'inspect-only' denies '{operation}'."); }
}

public sealed class GatewayAuditLog {
	readonly object sync=new(); readonly string path; readonly long maxBytes;
	public GatewayAuditLog() : this(Environment.GetEnvironmentVariable("DGSPY_AUDIT_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy","gateway-audit.jsonl"),5*1024*1024) { }
	internal GatewayAuditLog(string path,long maxBytes) { this.path=path; this.maxBytes=maxBytes; }
	public void Write(string auditId,string clientId,string? hostId,string? sessionId,string operation,long? expectedVersion,string outcome,string? errorCode) {
		var record=new { timestamp_utc=DateTime.UtcNow.ToString("O"),audit_id=auditId,controller_id=clientId,host_id=hostId,session_id=sessionId,operation,expected_state_version=expectedVersion,outcome,error_code=errorCode };
		var line=System.Text.Json.JsonSerializer.Serialize(record)+Environment.NewLine;
		try { lock(sync) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); if(File.Exists(path) && new FileInfo(path).Length+line.Length>maxBytes) { var previous=path+".1"; if(File.Exists(previous)) File.Delete(previous); File.Move(path,previous); } File.AppendAllText(path,line); } } catch { }
	}
}

public sealed class GatewayToolExecutor {
	readonly HostRouter router; readonly SessionControllers controllers; readonly GatewayAccessPolicy access; readonly GatewayAuditLog audit;
	public GatewayToolExecutor(HostRouter router,SessionControllers controllers,GatewayAccessPolicy access,GatewayAuditLog audit) { this.router=router; this.controllers=controllers; this.access=access; this.audit=audit; }
	public async Task<RpcResponse> ExecuteAsync(string operation,JObject arguments,string clientId,CancellationToken token) {
		var hostId=(string?)arguments["host_id"]; var sessionId=(string?)arguments["session_id"]; var expected=(long?)arguments["expected_state_version"];
		var mutates=CapabilityCatalog.Operations.Any(item=>item.Operation==operation && item.MutatesSession); var controlMutation=operation is "claim_session" or "release_session";
		var auditId=mutates || controlMutation ? Guid.NewGuid().ToString("N") : null;
		try {
			if(operation=="get_session_controller") { if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required."); var current=controllers.Get(sessionId); return RpcResponse.Success(Guid.NewGuid().ToString("N"),current is null ? new { session_id=sessionId,owned=false,controller_id=(string?)null } : new { session_id=sessionId,owned=true,controller_id=(string?)current.ClientId }); }
			if(operation=="claim_session") { access.AuthorizeMutation(operation); if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required."); var state=await GetStateAsync(arguments,token); var claimed=controllers.Claim(sessionId,hostId,clientId); audit.Write(auditId!,clientId,hostId,sessionId,operation,null,"succeeded",null); return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,controller_id=claimed.ClientId,state_version=StateVersion(state) }); }
			if(operation=="release_session") { access.AuthorizeMutation(operation); if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments","session_id is required."); var released=controllers.Release(sessionId,clientId); audit.Write(auditId!,clientId,hostId,sessionId,operation,null,"succeeded",null); return RpcResponse.Success(Guid.NewGuid().ToString("N"),new { session_id=sessionId,released=true,controller_id=released.ClientId }); }
			if(mutates) {
				access.AuthorizeMutation(operation);
				if(operation is not ("attach" or "attach_endpoint" or "launch")) {
					if(string.IsNullOrWhiteSpace(sessionId)) throw new GatewayControlException("invalid_arguments",$"'{operation}' requires session_id.");
					controllers.Authorize(sessionId,clientId);
					if(!expected.HasValue) throw new GatewayControlException("state_version_required",$"'{operation}' requires expected_state_version.");
					var state=await GetStateAsync(arguments,token); var current=StateVersion(state);
					if(expected.Value!=current) throw new GatewayControlException("stale_state",$"Expected state {expected.Value}, current state is {current}.");
				}
			}
			var response=await router.CallAsync(new RpcRequest { Operation=operation,Arguments=(JObject)arguments.DeepClone(),DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds(operation)) },token);
			if(response.Error is null && (operation=="attach" || operation=="attach_endpoint" || operation=="launch")) { var started=JObject.FromObject(response.Result!); var created=(string?)started["session_id"]; if(!string.IsNullOrEmpty(created)) controllers.Claim(created,hostId,clientId); }
			if(response.Error is null && !string.IsNullOrWhiteSpace(sessionId) && (operation=="detach" || operation=="terminate")) controllers.ReleaseTerminal(sessionId);
			if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,response.Error is null ? "succeeded" : "failed",response.Error?.Code);
			return response;
		}
		catch(GatewayControlException ex) { if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,"rejected",ex.Code); return RpcResponse.Failure(Guid.NewGuid().ToString("N"),ex.Code,ex.Message); }
		catch(Exception ex) { if(auditId is not null) audit.Write(auditId,clientId,hostId,sessionId,operation,expected,"failed","internal_error"); return RpcResponse.Failure(Guid.NewGuid().ToString("N"),"internal_error",ex.Message); }
	}
	async Task<object> GetStateAsync(JObject source,CancellationToken token) { var args=new JObject(); if(source["host_id"] is not null) args["host_id"]=source["host_id"]!.DeepClone(); args["session_id"]=source["session_id"]!.DeepClone(); var response=await router.CallAsync(new RpcRequest { Operation="get_session_state",Arguments=args,DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds("get_session_state")) },token); if(response.Error is not null) throw new GatewayControlException(response.Error.Code,response.Error.Message); return response.Result!; }
	static long StateVersion(object state) => (long?)JObject.FromObject(state)["state_version"] ?? throw new GatewayControlException("invalid_state","Host did not report state_version.");
}
