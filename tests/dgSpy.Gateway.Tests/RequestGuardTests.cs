using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
	public void Every_session_mutation_schema_advertises_state_version_guard() {
		var tools=ProtocolJson.ToNode(ToolCatalog.All)!.AsArray();
		foreach(var operation in CapabilityCatalog.Operations.Where(item=>item.MutatesSession)) {
			var tool=tools.OfType<JsonObject>().Single(item=>(string?)item["name"]==operation.Operation);
			if(tool["inputSchema"]?["properties"]?["session_id"] is not null)
			{
				Assert.NotNull(tool["inputSchema"]?["properties"]?["expected_state_version"]);
				Assert.Contains("expected_state_version",ProtocolJson.FromNode<string[]>(tool["inputSchema"]?["required"]) ?? Array.Empty<string>());
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
			var responder=Task.Run(async ()=>{ var request=ProtocolJson.Deserialize<RpcRequest>((await remoteReader.ReadLineAsync())!)!; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,new { ok=true }))); });
			var response=await router.CallAsync(new RpcRequest { Operation="get_host_info",Arguments=new JsonObject { ["host_id"]="host-a" } },default);
			await responder; listener.Stop(); Assert.Null(response.Error); Assert.Equal(true,(bool?)ProtocolJson.ToObject(response.Result!)["ok"]);
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Executor_claims_launch_and_enforces_controller_and_state_version() {
		var directory=CreateRegistryDirectory(); var auditPath=Path.Combine(directory,"audit.jsonl");
		try {
			var json=ProtocolJson.Serialize(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} }); var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true}; Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var responder=Task.Run(async ()=>{ for(var i=0;i<4;i++){ var request=ProtocolJson.Deserialize<RpcRequest>((await remoteReader.ReadLineAsync())!)!; object result=request.Operation switch { "launch"=>new SessionState { SessionId="session-a",State="running",StateVersion=7 }, "get_session_state"=>new SessionState { SessionId="session-a",State="running",StateVersion=7 }, "pause"=>new SessionState { SessionId="session-a",State="paused",StateVersion=8 }, _=>new { ok=true } }; await remoteWriter.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,result))); } });
			var clients=new McpClientSessions(TimeSpan.FromMinutes(5)); clients.Touch("client-a"); clients.Touch("client-b"); var controllers=new SessionControllers(clients); var audit=new GatewayAuditLog(auditPath,4096);
			var inspectOnly=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("inspect-only"),audit); Assert.Equal("access_denied",(await inspectOnly.ExecuteAsync("launch",ProtocolJson.ToObject(new { host_id="host-a",filename="target.exe" }),"client-a",default)).Error?.Code);
			var executor=new GatewayToolExecutor(router,controllers,new GatewayAccessPolicy("full-control"),audit);
			Assert.Equal("invalid_arguments",(await executor.ExecuteAsync("set_exception_policy",ProtocolJson.ToObject(new { host_id="host-a",name="Example" }),"client-a",default)).Error?.Code);
			var launched=await executor.ExecuteAsync("launch",ProtocolJson.ToObject(new { host_id="host-a",filename="target.exe" }),"client-a",default); Assert.Null(launched.Error); Assert.Equal("client-a",controllers.Get("session-a")?.ClientId);
			var selection=ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",expected_state_version=7 });
			Assert.Equal("session_owned",(await executor.ExecuteAsync("pause",selection,"client-b",default)).Error?.Code);
			Assert.Equal("state_version_required",(await executor.ExecuteAsync("pause",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a" }),"client-a",default)).Error?.Code);
			Assert.Equal("stale_state",(await executor.ExecuteAsync("pause",ProtocolJson.ToObject(new { host_id="host-a",session_id="session-a",expected_state_version=6 }),"client-a",default)).Error?.Code);
			Assert.Null((await executor.ExecuteAsync("pause",selection,"client-a",default)).Error); await responder; listener.Stop(); Assert.Contains("state_version_required",File.ReadAllText(auditPath));
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
