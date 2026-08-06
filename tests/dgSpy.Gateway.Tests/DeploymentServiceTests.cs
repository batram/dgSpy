using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

[CollectionDefinition("deployment-environment",DisableParallelization=true)]
public sealed class DeploymentEnvironmentCollection { }

[Collection("deployment-environment")]
public sealed class DeploymentServiceTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-deployment-tests-"+Guid.NewGuid().ToString("N"));
	readonly Dictionary<string,string?> previous=new();
	public DeploymentServiceTests() {
		Set("DGSPY_STATE_ROOT",Path.Combine(root,"state")); Set("DGSPY_INSTALL_ROOT",Path.Combine(root,"install"));
		Set("DGSPY_PACKAGE_ROOT",Path.Combine(root,"state","packages")); Set("DGSPY_REMOTE_PAYLOAD_ROOT",CreatePayload());
		Directory.CreateDirectory(Path.Combine(root,"state")); File.WriteAllText(Path.Combine(root,"state","host.id"),"test-local"); File.WriteAllText(Path.Combine(root,"state","rpc.token"),"test-token");
	}
	[Fact]
	public async Task Remote_package_is_created_from_bundled_payload_without_a_plan_or_source_build() {
		var service=new DeploymentService(); var router=new HostRouter();
		var result=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"use_tls",false}},router,default)))!;
		var archive=(string)result["archive_path"]!; Assert.True(File.Exists(archive)); Assert.Equal("authenticated_plaintext",(string?)result["transport"]);
		var registry=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state","packages","gateway-hosts.json")))!; Assert.Equal("remote-a",(string?)registry["hosts"]?[0]?["host_id"]);
	}
	[Fact]
	public async Task Remote_package_defaults_to_mutual_tls_and_can_replace_the_same_host() {
		var service=new DeploymentService(); var router=new HostRouter(); var request=new JsonObject{{"host_id","remote-tls"},{"gateway_address","192.168.2.115"}};
		var first=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",request,router,default)))!;
		var second=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",request,router,default)))!;
		Assert.Equal("mutual_tls",(string?)first["transport"]); Assert.True((bool?)second["replaced_existing_host"]); Assert.True(File.Exists(Path.Combine(root,"state","packages","remote-tls-client.cer")));
		var registry=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state","packages","gateway-hosts.json")))!; Assert.Single(registry["hosts"]!.AsArray()); Assert.Equal(7353,(int?)registry["tls"]?["port"]);
	}
	[Fact]
	public void Local_host_is_installed_directly_from_the_bundled_payload_and_reused() {
		var service=new DeploymentService(); Assert.True(service.EnsureBundledLocalHost(default)); Assert.False(service.EnsureBundledLocalHost(default));
		var current=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!; var version=(string)current["active_version"]!;
		Assert.True(File.Exists(Path.Combine(root,"install","versions",version,"dnSpy.exe"))); Assert.True(File.Exists(Path.Combine(root,"install","current","Start-dgSpy.cmd")));
	}
	[Fact]
	public async Task Missing_bundled_payload_is_an_installation_error_not_a_build_request() {
		Environment.SetEnvironmentVariable("DGSPY_REMOTE_PAYLOAD_ROOT",Path.Combine(root,"missing-payload")); var service=new DeploymentService(); var router=new HostRouter();
		var error=await Assert.ThrowsAsync<GatewayControlException>(()=>service.ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"}},router,default));
		Assert.Equal("installation_incomplete",error.Code); Assert.Contains("Reinstall dgSpy",error.Message); Assert.DoesNotContain("build.ps1",error.Message,StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("source root",error.Message,StringComparison.OrdinalIgnoreCase);
	}
	[Fact]
	public async Task Remote_output_is_confined_to_the_configured_package_root() {
		var service=new DeploymentService(); var router=new HostRouter();
		var error=await Assert.ThrowsAsync<GatewayControlException>(()=>service.ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"output_root",Path.Combine(root,"outside")}},router,default));
		Assert.Equal("path_outside_package_root",error.Code);
	}
	string CreatePayload() { var path=Path.Combine(root,"payload"); foreach(var directory in new[]{"bin","bin\\Extensions\\dgSpy","launcher"}) Directory.CreateDirectory(Path.Combine(path,directory)); foreach(var file in new[]{"dnSpy.exe","bin\\dnSpy.dll","bin\\dnSpy.Contracts.DnSpy.dll","bin\\hostfxr.dll","bin\\hostpolicy.dll","bin\\coreclr.dll","bin\\clrjit.dll","bin\\Extensions\\dgSpy\\dgSpy.Extension.x.dll","launcher\\Start-dgSpyRemoteHost.ps1","launcher\\Start-dgSpyRemoteHost.cmd"}) File.WriteAllText(Path.Combine(path,file),file); return path; }
	void Set(string name,string value) { previous[name]=Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name,value); }
	public void Dispose() { foreach(var item in previous) Environment.SetEnvironmentVariable(item.Key,item.Value); if(Directory.Exists(root)) Directory.Delete(root,true); }
}
