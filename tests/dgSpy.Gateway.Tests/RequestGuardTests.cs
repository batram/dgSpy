using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using dgSpy.Protocol;
using Newtonsoft.Json;
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
			var json=JsonConvert.SerializeObject(new { hosts=new[]{new { host_id="host-a",address="10.0.0.5",port=7451,token_file="a.token" }} });
			Assert.Throws<InvalidOperationException>(()=>HostRegistry.FromJson(json,directory));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public void Outbound_hosts_have_no_dialable_endpoint_and_authenticate_exactly() {
		var directory=CreateRegistryDirectory();
		try {
			var json=JsonConvert.SerializeObject(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} });
			var registry=HostRegistry.FromJson(json,directory);
			var endpoint=registry.Select("host-a");
			Assert.True(endpoint.IsOutbound);
			var router=new HostRouter(registry);
			Assert.True(router.TryAuthenticate("host-a","token-a",out _));
			Assert.False(router.TryAuthenticate("host-a","token-b",out _));
			Assert.False(router.TryAuthenticate("host-b","token-a",out _));
		}
		finally { Directory.Delete(directory,true); }
	}

	[Fact]
	public async Task Outbound_connection_routes_requests_and_rejects_a_duplicate() {
		var directory=CreateRegistryDirectory();
		try {
			var json=JsonConvert.SerializeObject(new { hosts=new[]{new { host_id="host-a",transport="outbound",token_file="a.token" }} });
			var router=new HostRouter(HostRegistry.FromJson(json,directory));
			var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
			using var remote=new TcpClient(); var accept=listener.AcceptTcpClientAsync(); await remote.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port); using var gateway=await accept;
			var gatewayReader=new StreamReader(gateway.GetStream(),Encoding.UTF8,false,4096,true); var gatewayWriter=new StreamWriter(gateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			Assert.True(router.TryRegister("host-a",gateway,gatewayReader,gatewayWriter,out _));
			Assert.False(router.TryRegister("host-a",new TcpClient(),new StreamReader(Stream.Null),new StreamWriter(Stream.Null),out _));
			var remoteReader=new StreamReader(remote.GetStream(),Encoding.UTF8,false,4096,true); var remoteWriter=new StreamWriter(remote.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true};
			var responder=Task.Run(async ()=>{ var request=JsonConvert.DeserializeObject<RpcRequest>((await remoteReader.ReadLineAsync())!)!; await remoteWriter.WriteLineAsync(JsonConvert.SerializeObject(RpcResponse.Success(request.RequestId,new { ok=true }))); });
			var response=await router.CallAsync(new RpcRequest { Operation="get_host_info",Arguments=new Newtonsoft.Json.Linq.JObject { ["host_id"]="host-a" } },default);
			await responder; listener.Stop(); Assert.Null(response.Error); Assert.Equal(true,(bool?)Newtonsoft.Json.Linq.JObject.FromObject(response.Result!)["ok"]);
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

	static string RegistryJson(params (string HostId,int Port,string TokenFile)[] hosts) => JsonConvert.SerializeObject(new {
		hosts=hosts.Select(host=>new { host_id=host.HostId,display_name=host.HostId,address="127.0.0.1",port=host.Port,token_file=host.TokenFile }),
	});
}
