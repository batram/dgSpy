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
		Directory.CreateDirectory(Path.Combine(root,"state")); File.WriteAllText(Path.Combine(root,"state","host.id"),"test-local"); File.WriteAllText(Path.Combine(root,"state","rpc.token"),"test-token");
	}
	[Fact]
	public async Task Local_deployment_is_versioned_idempotent_and_rollbackable() {
		var source1=CreateSource("source-1","one"); var source2=CreateSource("source-2","two"); var service=new DeploymentService(); var router=new HostRouter();
		var plan1=(JsonObject)await service.ExecuteAsync("plan_local_deployment",new JsonObject{{"source_path",source1},{"version","1.0.0"},{"host_id","test-local"}},router,default);
		var deployed1=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("deploy_local_host",new JsonObject{{"plan_id",(string?)plan1["plan_id"]}},router,default)))!;
		Assert.Equal("1.0.0",(string?)deployed1["version"]); Assert.True(File.Exists(Path.Combine(root,"install","versions","1.0.0","deployment-manifest.json")));
		var plan2=(JsonObject)await service.ExecuteAsync("plan_local_deployment",new JsonObject{{"source_path",source2},{"version","2.0.0"},{"host_id","test-local"}},router,default);
		await service.ExecuteAsync("deploy_local_host",new JsonObject{{"plan_id",(string?)plan2["plan_id"]}},router,default);
		var rolled=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("rollback_local_deployment",new JsonObject{{"confirm",true}},router,default)))!;
		Assert.Equal("1.0.0",(string?)rolled["active_version"]); Assert.True(Directory.Exists(Path.Combine(root,"install","versions","2.0.0")));
	}
	[Fact]
	public async Task Plans_are_single_use_and_uninstall_preserves_settings_by_default() {
		var service=new DeploymentService(); var router=new HostRouter(); var source=CreateSource("source","one"); var plan=(JsonObject)await service.ExecuteAsync("plan_local_deployment",new JsonObject{{"source_path",source},{"version","1.0.0"}},router,default); var id=(string?)plan["plan_id"];
		await service.ExecuteAsync("deploy_local_host",new JsonObject{{"plan_id",id}},router,default);
		await Assert.ThrowsAsync<GatewayControlException>(()=>service.ExecuteAsync("deploy_local_host",new JsonObject{{"plan_id",id}},router,default));
		var removed=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("uninstall_local_deployment",new JsonObject{{"confirm",true}},router,default)))!;
		Assert.True((bool?)removed["settings_preserved"]); Assert.True(Directory.Exists(Path.Combine(root,"state"))); Assert.False(Directory.Exists(Path.Combine(root,"install")));
	}
	[Fact]
	public async Task Remote_output_is_confined_to_the_configured_package_root() {
		var service=new DeploymentService(); var router=new HostRouter();
		var error=await Assert.ThrowsAsync<GatewayControlException>(()=>service.ExecuteAsync("plan_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"output_root",Path.Combine(root,"outside")}},router,default));
		Assert.Equal("path_outside_package_root",error.Code);
	}
	string CreateSource(string name,string marker) { var path=Path.Combine(root,name); Directory.CreateDirectory(Path.Combine(path,"bin","Extensions","dgSpy")); File.WriteAllText(Path.Combine(path,"dnSpy.exe"),marker); File.WriteAllText(Path.Combine(path,"bin","dnSpy.Contracts.DnSpy.dll"),marker); File.WriteAllText(Path.Combine(path,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),marker); return path; }
	void Set(string name,string value) { previous[name]=Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name,value); }
	public void Dispose() { foreach(var item in previous) Environment.SetEnvironmentVariable(item.Key,item.Value); if(Directory.Exists(root)) Directory.Delete(root,true); }
}
