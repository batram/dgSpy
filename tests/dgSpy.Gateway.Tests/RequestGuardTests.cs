using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using dgSpy.Gateway;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>
/// These cover the drive-by attack the gateway exists to stop: a page the user visits POSTing to
/// 127.0.0.1 to drive the debugger. Loopback binding alone does not prevent it.
/// </summary>
public class RequestGuardTests {
	const string Token = "secret-token";
	static readonly IPAddress Loopback = IPAddress.Loopback;

	[Fact]
	public void Local_client_with_the_token_is_allowed() =>
		Assert.Null(RequestGuard.Reject(origin: null, presentedToken: Token, Token, Loopback));

	[Fact]
	public void A_web_page_is_rejected_even_with_a_stolen_looking_request() {
		var rejection = RequestGuard.Reject("https://evil.example", presentedToken: Token, Token, Loopback);

		Assert.NotNull(rejection);
		Assert.Contains("evil.example", rejection);
	}

	[Theory]
	[InlineData("http://127.0.0.1:7350")]
	[InlineData("http://localhost:3000")]
	[InlineData("http://[::1]:8080")]
	public void Loopback_origins_are_allowed(string origin) =>
		Assert.Null(RequestGuard.Reject(origin, Token, Token, Loopback));

	[Theory]
	[InlineData("http://127.0.0.1.evil.example")]
	[InlineData("http://localhost.evil.example")]
	[InlineData("http://192.168.1.10")]
	[InlineData("not-a-url")]
	public void Origins_that_only_look_like_loopback_are_rejected(string origin) =>
		Assert.NotNull(RequestGuard.Reject(origin, Token, Token, Loopback));

    [Fact]
	public void Missing_token_is_rejected() =>
		Assert.NotNull(RequestGuard.Reject(null, presentedToken: null, Token, Loopback));

	[Fact]
	public void Wrong_token_is_rejected() =>
		Assert.NotNull(RequestGuard.Reject(null, presentedToken: "guess", Token, Loopback));

	[Fact]
	public void A_gateway_with_no_token_configured_refuses_to_serve() {
		// Fail closed: an unset token must not degrade into "no authentication required".
		var rejection = RequestGuard.Reject(null, presentedToken: null, expectedToken: "", Loopback);

		Assert.NotNull(rejection);
	}

	[Fact]
	public void Non_loopback_clients_are_refused_before_any_other_check() {
		var rejection = RequestGuard.Reject(null, Token, Token, IPAddress.Parse("10.0.0.5"));

		Assert.NotNull(rejection);
		Assert.Contains("10.0.0.5", rejection);
	}
}

public class RpcClientSettingsTests {
	[Fact]
	public void Matching_or_unconfigured_host_identity_is_accepted() {
		RpcClientSettings.EnsureExpectedHost(null,"host-a");
		RpcClientSettings.EnsureExpectedHost("host-a","host-a");
	}

	[Fact]
	public void Missing_or_mismatched_host_identity_is_rejected() {
		Assert.Throws<IOException>(()=>RpcClientSettings.EnsureExpectedHost(null,""));
		Assert.Throws<IOException>(()=>RpcClientSettings.EnsureExpectedHost("host-a","host-b"));
	}
}

public class SessionControlTests {
	[Theory]
	[InlineData("detach",false,true)]
	[InlineData("detach",true,false)]
	public void Controller_release_for_detach_requires_a_terminal_result(string operation,bool sessionActive,bool expected) {
		Assert.Equal(expected,GatewayToolExecutor.IsTerminalLifecycleResult(operation,new DetachResult { SessionActive=sessionActive }));
	}

	[Fact]
	public void Controller_release_for_terminate_requires_no_remaining_processes() {
		Assert.False(GatewayToolExecutor.IsTerminalLifecycleResult("terminate",new SessionState { ProcessIds=new[] { 42 } }));
		Assert.True(GatewayToolExecutor.IsTerminalLifecycleResult("terminate",new SessionState { ProcessIds=Array.Empty<int>() }));
	}

	[Theory]
	[InlineData("detach","lifecycle","expected_lifecycle_version")]
	[InlineData("continue","execution","expected_execution_version")]
	[InlineData("run_atomic_action","execution","expected_execution_version")]
	[InlineData("cancel_atomic_action","execution","expected_execution_version")]
	[InlineData("set_value","execution","expected_execution_version")]
	[InlineData("set_il_breakpoint","breakpoints","expected_breakpoints_version")]
	public void Mutation_guards_are_scoped_to_the_affected_state(string operation,string scope,string argument) {
		Assert.Equal(scope,MutationGuards.Scope(operation));
		Assert.Equal(argument,MutationGuards.Argument(operation));
	}
	[Fact]
	public void Active_controller_blocks_contention_but_expiry_allows_explicit_reclaim() {
		var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); var controllers=new SessionControllers(clients);
		clients.Touch("client-a"); clients.Touch("client-b"); controllers.Claim("session-a","host-a","client-a");
		Assert.Equal("client-a",controllers.Authorize("session-a","client-a").ClientId);
		Assert.Equal("session_owned",Assert.Throws<GatewayControlException>(()=>controllers.Claim("session-a","host-a","client-b")).Code);
		clients.SetLastSeen("client-a",DateTime.UtcNow-TimeSpan.FromMinutes(6));
		Assert.Equal("session_unowned",Assert.Throws<GatewayControlException>(()=>controllers.Authorize("session-a","client-a")).Code);
		Assert.Equal("client-b",controllers.Claim("session-a","host-a","client-b").ClientId);
	}

	[Fact]
	public void Release_changes_only_ownership() {
		var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); clients.Touch("client-a"); var controllers=new SessionControllers(clients);
		controllers.Claim("session-a","host-a","client-a");
		Assert.Equal("session-a",controllers.Release("session-a","client-a").SessionId);
		Assert.Null(controllers.Get("session-a"));
	}

	[Fact]
	public void Inspect_only_mode_denies_mutation() {
		var policy=new GatewayAccessPolicy("inspect-only");
		Assert.Equal("access_denied",Assert.Throws<GatewayControlException>(()=>policy.AuthorizeMutation("pause")).Code);
		new GatewayAccessPolicy("full-control").AuthorizeMutation("pause");
	}

	[Fact]
	public void Audit_log_is_redacted_and_rotates() {
		var directory=Path.Combine(Path.GetTempPath(),"dgspy-audit-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path=Path.Combine(directory,"audit.jsonl");
		try {
			var log=new GatewayAuditLog(path,300); log.Write("audit-a","client-a","host-a","session-a","set_value",4,"rejected","stale_state");
			var text=File.ReadAllText(path); Assert.Contains("stale_state",text); Assert.DoesNotContain("expression",text); Assert.DoesNotContain("credential",text);
			for(var i=0;i<20;i++) log.Write("audit-"+i,"client-a","host-a","session-a","pause",4,"succeeded",null);
			Assert.True(File.Exists(path+".1"));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Every_session_mutation_schema_advertises_scoped_version_guard_and_no_deprecated_alias() {
		var tools=ProtocolJson.ToNode(ToolCatalog.All)!.AsArray();
		foreach(var operation in CapabilityCatalog.Operations.Where(item=>item.MutatesSession)) {
			var tool=tools.OfType<JsonObject>().Single(item=>(string?)item["name"]==operation.Operation);
			if(tool["inputSchema"]?["properties"]?["session_id"] is not null)
			{
				var guard=MutationGuards.Argument(operation.Operation);
				// The deprecated expected_state_version alias is gone: a dead parameter on every mutating
				// schema taxes exactly the schema-budgeted blind-driving scenario this server exists for.
				Assert.Null(tool["inputSchema"]?["properties"]?["expected_state_version"]);
				Assert.NotNull(tool["inputSchema"]?["properties"]?[guard]);
				Assert.Contains(guard,ProtocolJson.FromNode<string[]>(tool["inputSchema"]?["required"]) ?? Array.Empty<string>());
				if(MutationGuards.RequiresStop(operation.Operation)) Assert.Contains("expected_stop_id",ProtocolJson.FromNode<string[]>(tool["inputSchema"]?["required"]) ?? Array.Empty<string>());
			}
		}
	}
}

public class HostRegistryTests {
	[Fact]
	public void One_registered_host_is_the_implicit_route() {
		var directory=CreateRegistryDirectory();
		try {
			var registry=HostRegistry.FromJson(RegistryJson(("host-a",7451,"a.token")),directory);

			Assert.Equal("host-a",registry.Select(null).HostId);
			Assert.Equal("host-a",registry.Select("host-a").HostId);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Multiple_hosts_require_an_explicit_known_host_id() {
		var directory=CreateRegistryDirectory();
		try {
			var registry=HostRegistry.FromJson(RegistryJson(("host-a",7451,"a.token"),("host-b",7452,"b.token")),directory);

			Assert.Equal("host-b",registry.Select("host-b").HostId);
			Assert.Equal("host_required",Assert.Throws<HostRoutingException>(()=>registry.Select(null)).Code);
			Assert.Equal("unknown_host",Assert.Throws<HostRoutingException>(()=>registry.Select("host-c")).Code);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Registry_rejects_duplicate_hosts_and_non_loopback_endpoints() {
		var directory=CreateRegistryDirectory();
		try {
			Assert.Throws<InvalidOperationException>(()=>HostRegistry.FromJson(RegistryJson(("host-a",7451,"a.token"),("host-a",7452,"b.token")),directory));
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",address="10.0.0.5",port=7451,token_file="a.token" }} });
			Assert.Throws<InvalidOperationException>(()=>HostRegistry.FromJson(json,directory));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Outbound_hosts_have_no_dialable_endpoint_and_authenticate_exactly() {
		var directory=CreateRegistryDirectory();
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} });
			var registry=HostRegistry.FromJson(json,directory);
			var endpoint=registry.Select("host-a");
			Assert.True(endpoint.IsOutbound);
			var router=new HostRouter(registry);
			Assert.True(router.TryAuthenticate("host-a","token-a",false,null,out _));
			Assert.False(router.TryAuthenticate("host-a","token-b",false,null,out _));
			Assert.False(router.TryAuthenticate("host-b","token-a",false,null,out _));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Refused_local_host_is_reported_as_host_unavailable_with_launch_recovery() {
		var directory=CreateRegistryDirectory();
		try {
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port;
			var disconnect=Task.Run(async ()=>{ using var connection=await listener.AcceptTcpClientAsync(); });
			var router=new HostRouter(HostRegistry.FromJson(RegistryJson(("host-a",port,"a.token")),directory));
			var response=await router.CallAsync(new RpcRequest { Operation="list_programs",Arguments=new JsonObject { ["host_id"]="host-a" },DeadlineUtc=DateTime.UtcNow.AddSeconds(2) },default);
			await disconnect; listener.Stop();

			Assert.Equal("host_unavailable",response.Error?.Code);
			Assert.Contains("launch_local_host",response.Error?.Message);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Canceled_host_call_is_reported_as_deadline_exceeded_not_host_unavailable() {
		var directory=CreateRegistryDirectory();
		try {
			var router=new HostRouter(HostRegistry.FromJson(RegistryJson(("host-a",1,"a.token")),directory));
			using var canceled=new CancellationTokenSource(); canceled.Cancel();
			var response=await router.CallAsync(new RpcRequest { Operation="search_symbols",Arguments=new JsonObject { ["host_id"]="host-a" } },canceled.Token);

			Assert.Equal("deadline_exceeded",response.Error?.Code);
			Assert.Contains("assume it may have applied",response.Error?.Message,StringComparison.OrdinalIgnoreCase);
			Assert.Contains("get_session_state",response.Error?.Message,StringComparison.Ordinal);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Tls_outbound_host_requires_the_pinned_certificate_and_tls_transport() {
		var directory=CreateRegistryDirectory();
		try {
			using var key=RSA.Create(2048); var request=new CertificateRequest("CN=host-a",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
			using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
			File.WriteAllBytes(Path.Combine(directory,"host-a.cer"),certificate.Export(X509ContentType.Cert));
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound_tls",token_file="a.token",client_certificate_file="host-a.cer" }} });
			var router=new HostRouter(HostRegistry.FromJson(json,directory)); var expected=certificate.GetCertHash(); var wrong=(byte[])expected.Clone(); wrong[0]^=0xff;
			Assert.False(router.TryAuthenticate("host-a","token-a",false,expected,out _));
			Assert.False(router.TryAuthenticate("host-a","token-a",true,null,out _));
			Assert.False(router.TryAuthenticate("host-a","token-a",true,wrong,out _));
			Assert.False(router.TryAuthenticate("host-a","token-b",true,expected,out _));
			Assert.True(router.TryAuthenticate("host-a","token-a",true,expected,out _));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Outbound_connection_routes_requests_and_rejects_a_duplicate() {
		var directory=CreateRegistryDirectory();
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} });
			var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
			using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			Assert.False(router.TryRegister("host-a",new TcpClient(),new StreamReader(Stream.Null),new StreamWriter(Stream.Null),out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var responder=Task.Run(async ()=>{ for(var i=0;i<2;i++){ var request=ProtocolJson.Deserialize<RpcRequest>((await remoteReader.ReadLineAsync())!)!; object result=i==0 ? new { ok=true } : new HostInfo { HostId="host-a",ConnectionState="degraded",DispatcherState="degraded",DispatcherFaultCount=1 }; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,result))); } });
			var response=await router.CallAsync(new RpcRequest { Operation="get_host_info",Arguments=new JsonObject { ["host_id"]="host-a" } },default);
			var hosts=await router.ListHostsAsync(default);
			await responder; listener.Stop(); Assert.Null(response.Error); Assert.Equal(true,(bool?)ProtocolJson.ToObject(response.Result!)["ok"]); Assert.Contains("\"state\":\"degraded\"",System.Text.Json.JsonSerializer.Serialize(hosts));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Executor_claims_launch_and_enforces_controller_and_scoped_execution_version() {
		var directory=CreateRegistryDirectory(); var auditPath=Path.Combine(directory,"audit.jsonl");
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} }); var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true}; Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var responder=Task.Run(async ()=>{ for(var i=0;i<4;i++){ var request=ProtocolJson.Deserialize<RpcRequest>((await remoteReader.ReadLineAsync())!)!; object result=request.Operation switch { "launch"=>new SessionState { SessionId="session-a",State="running",StateVersion=7,ExecutionVersion=3,LifecycleVersion=2,BreakpointsVersion=1 }, "get_session_state"=>new SessionState { SessionId="session-a",State="running",StateVersion=7,ExecutionVersion=3,LifecycleVersion=2,BreakpointsVersion=1 }, "pause"=>new SessionState { SessionId="session-a",State="paused",StateVersion=8,ExecutionVersion=4,LifecycleVersion=2,BreakpointsVersion=1 }, _=>new { ok=true } }; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,result))); } });
			var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); clients.Touch("client-a"); clients.Touch("client-b"); var controllers=new SessionControllers(clients); var audit=new GatewayAuditLog(auditPath,4096);
			var inspectOnly=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("inspect-only"),audit); Assert.Equal("access_denied",(await inspectOnly.ExecuteAsync("launch",ProtocolJson.ToObject(new { host_id="host-a",filename="target.exe" }),"client-a",default)).Error?.Code);
			var executor=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("full-control"),audit);
			Assert.Equal("invalid_arguments",(await executor.ExecuteAsync("set_exception_policy",ProtocolJson.ToObject(new { host_id="host-a",name="Example" }),"client-a",default)).Error?.Code);
			var launched=await executor.ExecuteAsync("launch",ProtocolJson.ToObject(new { host_id="host-a",filename="target.exe" }),"client-a",default); Assert.Null(launched.Error); Assert.Equal("client-a",controllers.Get("session-a")?.ClientId);
			var selection=ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",expected_execution_version=3 });
			Assert.Equal("session_owned",(await executor.ExecuteAsync("pause",selection,"client-b",default)).Error?.Code);
			Assert.Equal("execution_version_required",(await executor.ExecuteAsync("pause",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a" }),"client-a",default)).Error?.Code);
			Assert.Equal("stale_execution",(await executor.ExecuteAsync("pause",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",expected_execution_version=2 }),"client-a",default)).Error?.Code);
			Assert.Null((await executor.ExecuteAsync("pause",selection,"client-a",default)).Error); await responder; listener.Stop(); Assert.Contains("execution_version_required",File.ReadAllText(auditPath));
		}
		finally { Directory.Delete(directory,true); }
	}

	/// <summary>run_to_method is not one host call but four, and the gateway composes their arguments
	/// itself. It composed two of them wrongly and nothing noticed, because every test that touched
	/// run_to_* asserted on the tool's schema rather than on what the host was actually asked.
	///
	/// It dropped `module` and `expected_breakpoints_version` when creating the temporary breakpoint, so
	/// the host answered "module is required" to a caller who had passed module. It then guarded the
	/// resume with the deprecated `expected_state_version` alias carrying an execution_version, and the
	/// host compares that alias against state_version -- a different counter -- so the call died with a
	/// stale_state naming two numbers the caller never supplied. Both defects made a gateway composition
	/// bug read as caller error, which is exactly the failure mode this whole change set is about.</summary>
	[Fact]
	public async Task Run_to_method_forwards_the_arguments_its_composed_host_calls_require() {
		var directory=CreateRegistryDirectory(); var auditPath=Path.Combine(directory,"audit.jsonl");
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} }); var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true}; Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var seen=new System.Collections.Generic.List<RpcRequest>();
			// Serve until the connection closes rather than counting calls: a fixed count deadlocks the
			// test the moment the composition under test makes one call fewer, which is precisely the
			// change this test exists to catch.
			var responder=Task.Run(async ()=>{ for(string? line;(line=await remoteReader.ReadLineAsync()) is not null;){ var request=ProtocolJson.Deserialize<RpcRequest>(line)!; lock(seen) seen.Add(request); object result=request.Operation switch {
				"set_breakpoint"=>new { breakpoint_id=11,cursor_event_id=4,bound=true },
				"get_session_state"=>new SessionState { SessionId="session-a",State="paused",StateVersion=14,ExecutionVersion=2,LifecycleVersion=2,BreakpointsVersion=1 },
				// The host stamps every guarded response with the vector read after it applied. The
				// removal is the last call the composition makes, so its vector is the one the caller
				// must guard the next call with.
				"remove_breakpoint"=>new { breakpoint_id=11,removed=true,versions=new { lifecycle_version=2,execution_version=3,breakpoints_version=2,stop_id="stop-2",last_event_id=9 } },
				_=>new { ok=true } }; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,result))); } });
			var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); clients.Touch("client-a"); var controllers=new SessionControllers(clients); controllers.Claim("session-a","host-a","client-a"); var audit=new GatewayAuditLog(auditPath,4096);
			var executor=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("full-control"),audit);

			var response=await executor.ExecuteAsync("run_to_method",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",module="Target.exe",type="Target.Program",method="Tick",
				expected_execution_version=2,expected_breakpoints_version=0,expected_stop_id="stop-1" }),"client-a",default);
			listener.Stop();

			Assert.Null(response.Error);
			var created=seen.Single(request=>request.Operation=="set_breakpoint");
			Assert.Equal("Target.exe",(string?)created.Arguments["module"]);
			Assert.Equal(0,(int?)created.Arguments["expected_breakpoints_version"]);
			var resumed=seen.Single(request=>request.Operation=="continue");
			Assert.Equal(2,(int?)resumed.Arguments["expected_execution_version"]);
			Assert.Null(resumed.Arguments["expected_state_version"]);
			// The temporary breakpoint must actually come back out: breakpoints outlive a session and
			// rebind on the next attach, so a leaked one stops a later run for no reason anybody can see.
			var removed=seen.Single(request=>request.Operation=="remove_breakpoint");
			Assert.Equal(11,(int?)removed.Arguments["breakpoint_id"]);
			Assert.Equal(1,(int?)removed.Arguments["expected_breakpoints_version"]);
			// A composition is where the counters are hardest to guess: four guarded host calls moved
			// them and only the gateway saw the intermediate responses. Handing back no vector made the
			// caller re-read state after every run_to_*, which is the interposed get_session_state this
			// change set exists to delete. It must be the post-cleanup vector, not the wait's: removing
			// the temporary breakpoint moved breakpoints_version again.
			var versions=ProtocolJson.ToNode(response.Result)?["versions"];
			Assert.NotNull(versions);
			Assert.Equal(2,(int?)versions!["breakpoints_version"]);
			Assert.Equal(3,(int?)versions["execution_version"]);
			Assert.Equal("stop-2",(string?)versions["stop_id"]);
			Assert.Equal(9,(int?)versions["last_event_id"]);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Theory]
	[InlineData(false,7)]
	[InlineData(true,3)]
	public async Task Trace_calls_returns_the_last_observed_version_vector(bool stepTimesOut,int requestCount) {
		var directory=CreateRegistryDirectory(); var auditPath=Path.Combine(directory,"audit.jsonl");
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} }); var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true}; Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var versions=new { lifecycle_version=2,execution_version=4,breakpoints_version=1,stop_id="stop-2",last_event_id=9 };
			var responder=Task.Run(async ()=>{ for(var index=0;index<requestCount;index++){ var request=ProtocolJson.Deserialize<RpcRequest>((await remoteReader.ReadLineAsync())!)!; object result=request.Operation switch {
				"get_session_state"=>new SessionState { SessionId="session-a",State="paused",ExecutionVersion=3,StopId="stop-1" },
				"step_into"=>new { completed=true,cursor_event_id=4,versions },
				"wait_for_stop"=>stepTimesOut ? new { events=Array.Empty<object>(),timed_out=true,versions } : new { events=new[]{new { thread_id="1:2" }},timed_out=false,versions },
				"get_callstack"=>Array.Empty<object>(),
				_=>new { ok=true },
			}; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,result))); } });
			var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); clients.Touch("client-a"); var controllers=new SessionControllers(clients); controllers.Claim("session-a","host-a","client-a"); var audit=new GatewayAuditLog(auditPath,4096);
			var executor=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("full-control"),audit);

			var response=await executor.ExecuteAsync("trace_calls",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",expected_execution_version=3,expected_stop_id="stop-1",max_steps=1 }),"client-a",default);
			await responder; listener.Stop();

			Assert.Null(response.Error);
			var result=ProtocolJson.ToNode(response.Result)!.AsObject();
			Assert.Equal(stepTimesOut ? "step_timeout" : "bound_reached",(string?)result["reason"]);
			Assert.Equal(4,(int?)result["versions"]?["execution_version"]);
			Assert.Equal("stop-2",(string?)result["versions"]?["stop_id"]);
			Assert.Equal(9,(int?)result["versions"]?["last_event_id"]);
		}
		finally { Directory.Delete(directory,true); }
	}

	static string CreateRegistryDirectory() {
		var directory=Path.Combine(Path.GetTempPath(),"dgspy-host-registry-"+Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory,"a.token"),"token-a");
		File.WriteAllText(Path.Combine(directory,"b.token"),"token-b");
		return directory;
	}

	static string RegistryJson(params (string HostId,int Port,string TokenFile)[] hosts) => ProtocolJson.Serialize(new {
		hosts=hosts.Select(host=>new { host_id=host.HostId,display_name=host.HostId,address="127.0.0.1",port=host.Port,token_file=host.TokenFile }),
	});
}
