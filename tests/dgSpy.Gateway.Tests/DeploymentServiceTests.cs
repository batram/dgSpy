using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using dgSpy.Protocol;
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
	// The comparison functions are unit-tested on their own, but nothing else drives the composition:
	// the live smoke never calls get_started or doctor, so a serialization slip in the field that carries
	// the whole point would have surfaced only in front of an agent.
	[Fact]
	public async Task Get_started_names_the_gateway_build_and_carries_a_skew_verdict() {
		var result=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("get_started",new JsonObject(),new HostRouter(),default)))!;
		Assert.False(string.IsNullOrWhiteSpace((string?)result["gateway_build"]?["build_label"]));
		Assert.Contains((string?)result["gateway_build"]?["provenance"],new[]{"unpackaged","package_manifest"});
		Assert.NotNull(result["build_skew"]?["gateway_vs_hosts"]); Assert.NotNull(result["build_skew"]?["gateway_process_vs_disk"]);
		// The test assembly is a repository build with no host registered, so there is nothing to be
		// skewed against. A verdict of "skewed" here would mean the check fires on every dev tree.
		Assert.False((bool?)result["build_skew"]?["skewed"]);
	}
	[Fact]
	public async Task Doctor_checks_build_skew_and_passes_it_when_there_is_nothing_to_compare() {
		var result=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		var check=result["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="build_skew")!;
		Assert.True((bool?)check["ok"]); Assert.Contains("gateway ",(string?)check["detail"]!);
		Assert.False(string.IsNullOrWhiteSpace((string?)result["gateway_build"]?["version"]));
	}

	// The regression. The deployment fingerprint was the hash of dnSpy.exe alone - an apphost stub generated
	// from the project name, byte-identical across every rebuild - so rebuilding the managed assemblies
	// produced the same version name, "already installed" short-circuited, and the gateway kept serving a
	// tree that was days old while every stage still reported success. Mutating everything *except*
	// dnSpy.exe is precisely the case that used to go undetected.
	[Fact]
	public void Rebuilt_managed_assemblies_are_redeployed_even_when_the_apphost_stub_is_unchanged() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var first=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		var stub=File.ReadAllBytes(Path.Combine(root,"payload","dnSpy.exe"));

		var payload=Environment.GetEnvironmentVariable("DGSPY_REMOTE_PAYLOAD_ROOT")!;
		File.WriteAllText(Path.Combine(payload,"bin","dnSpy.dll"),"rebuilt dnSpy.dll with different content");
		File.WriteAllText(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),"rebuilt extension with different content");
		Assert.Equal(stub,File.ReadAllBytes(Path.Combine(payload,"dnSpy.exe")));

		var rebuilt=new DeploymentService();
		Assert.True(rebuilt.EnsureBundledLocalHost(default));
		var second=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		Assert.NotEqual(first,second);
		Assert.Equal("rebuilt extension with different content",File.ReadAllText(Path.Combine(root,"install","versions",second,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll")));
	}

	[Fact]
	public async Task An_installed_payload_newer_than_the_deployment_is_reported_as_stale() {
		var service=new DeploymentService(); var router=new HostRouter();
		Assert.True(service.EnsureBundledLocalHost(default));
		var fresh=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("get_local_deployment",new JsonObject(),router,default)))!;
		Assert.False((bool?)fresh["payload"]!["stale"]);

		var payload=Environment.GetEnvironmentVariable("DGSPY_REMOTE_PAYLOAD_ROOT")!;
		File.WriteAllText(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),"a newer extension that was never deployed");
		var afterRebuild=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("get_local_deployment",new JsonObject(),router,default)))!;
		Assert.True((bool?)afterRebuild["payload"]!["stale"]);
		Assert.Contains("newer than the running deployment",(string?)afterRebuild["payload"]!["detail"]);
		Assert.NotNull((string?)afterRebuild["payload"]!["recovery"]);
	}

	/// <summary>host_not_elevated tells the caller to retry with replace=true and elevated=true. That
	/// recovery was unreachable: the refusal fired inside the adoption branch before replace was ever
	/// consulted, so following the instruction reproduced the error verbatim. A refusal whose own recovery
	/// cannot be executed is worse than no refusal, because it costs the caller a round trip to learn
	/// nothing.</summary>
	[Fact]
	public void An_elevated_relaunch_replaces_a_medium_integrity_host_instead_of_refusing_forever() {
		Assert.Equal(DeploymentService.RunningHostDecision.Replace,DeploymentService.DecideRunningHost(true,false,true));
		Assert.Equal(DeploymentService.RunningHostDecision.Replace,DeploymentService.DecideRunningHost(true,null,true));
		Assert.Equal(DeploymentService.RunningHostDecision.RefuseElevation,DeploymentService.DecideRunningHost(true,false,false));
		Assert.Equal(DeploymentService.RunningHostDecision.RefuseElevation,DeploymentService.DecideRunningHost(true,null,false));

		// An already-elevated host is adopted rather than needlessly replaced, even when replace was
		// offered -- replace is permission to close it, not an instruction to.
		Assert.Equal(DeploymentService.RunningHostDecision.Adopt,DeploymentService.DecideRunningHost(true,true,true));
		Assert.Equal(DeploymentService.RunningHostDecision.Adopt,DeploymentService.DecideRunningHost(true,true,false));
		// Callers who never asked about elevation are unaffected by any of it.
		foreach(var elevation in new bool?[]{true,false,null})
			Assert.Equal(DeploymentService.RunningHostDecision.Adopt,DeploymentService.DecideRunningHost(false,elevation,false));
	}

	/// <summary>The whole value of elevated=true is access the caller could not otherwise get, so the one
	/// answer that must never come back is a cheerful adoption of a medium-integrity host. An
	/// undeterminable token is refused for the same reason a false one is: the caller cannot check the
	/// claim afterwards, and would discover the truth only when a target stayed invisible.</summary>
	[Fact]
	public void Adoption_refuses_a_host_that_is_not_provably_elevated_when_elevation_was_asked_for() {
		DeploymentService.RequireAdoptedHostElevated(4242,true);

		var medium=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireAdoptedHostElevated(4242,false));
		Assert.Equal("host_not_elevated",medium.Code);
		Assert.Contains("pid 4242",medium.Message);
		Assert.Contains("replace=true",medium.Message);

		var unknown=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireAdoptedHostElevated(4242,null));
		Assert.Equal("host_elevation_unknown",unknown.Code);
		Assert.Contains("pid 4242",unknown.Message);
	}

	/// <summary>Elevation goes through ShellExecute, which cannot hand the child an environment block, so
	/// every DGSPY_* value the operator overrode would silently fail to reach the elevated host: it would
	/// generate its own credential, or bind a port nobody dials, and the launch would report a started
	/// host the Gateway can never talk to. Refuse those combinations before starting anything.</summary>
	[Fact]
	public void Elevated_launch_refuses_environments_it_cannot_hand_to_the_child() {
		var clean=DeploymentService.DefaultStateRoot();
		DeploymentService.RequireElevatedLaunchEnvironment(clean,_=>null);
		// The same directory named differently is still the same directory.
		DeploymentService.RequireElevatedLaunchEnvironment(clean+Path.DirectorySeparatorChar,_=>null);
		DeploymentService.RequireElevatedLaunchEnvironment(Path.Combine(clean,"..","dgSpy"),_=>null);
		// Only the variables the host itself reads matter; the client-facing token is not one of them.
		DeploymentService.RequireElevatedLaunchEnvironment(clean,name=>name=="DGSPY_TOKEN"?"abc":null);

		var custom=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireElevatedLaunchEnvironment(Path.Combine(root,"state"),_=>null));
		Assert.Equal("elevated_launch_unsupported_environment",custom.Code);
		Assert.Contains("Nothing was started",custom.Message);

		foreach(var name in new[]{"DGSPY_HOST_ID","DGSPY_RPC_TOKEN","DGSPY_RPC_PORT"}) {
			var overridden=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireElevatedLaunchEnvironment(clean,candidate=>candidate==name?"set":null));
			Assert.Equal("elevated_launch_unsupported_environment",overridden.Code);
			Assert.Contains(name,overridden.Message);
		}
	}

	/// <summary>Defence in depth behind the running-host scan, which only sees dnSpy under this install
	/// root: a host installed elsewhere owns the port just as effectively. A probe that cannot run must
	/// not block the launch, because refusing to start any host at all is the worse of the two failures.
	/// </summary>
	[Fact]
	public void Endpoint_occupancy_blocks_a_launch_only_on_a_completed_connection() {
		Assert.True(DeploymentService.LocalRpcEndpointOwned(7351,_=>true));
		Assert.False(DeploymentService.LocalRpcEndpointOwned(7351,_=>false));
		Assert.False(DeploymentService.LocalRpcEndpointOwned(7351,_=>throw new InvalidOperationException("probe broken")));

		// Against real sockets, because the failure that matters is not a branch: a refused connection
		// faults the connect task, and reading that fault as an error rather than as an empty port would
		// refuse every launch on a healthy machine.
		var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start();
		var occupied=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
		try { Assert.True(DeploymentService.TryConnectLoopback(occupied)); }
		finally { listener.Stop(); }
		Assert.False(DeploymentService.TryConnectLoopback(occupied));
	}

	/// <summary>The scan must survive an integrity boundary. MainModule needs PROCESS_VM_READ and is
	/// refused against an elevated host, which is exactly the host this flag creates; QueryFullProcessImageName
	/// needs only PROCESS_QUERY_LIMITED_INFORMATION. This pins the P/Invoke signatures, whose failure mode
	/// is a silent null that reads as "no such process".</summary>
	[Fact]
	public void Process_identity_answers_for_a_process_it_can_open() {
		using var self=System.Diagnostics.Process.GetCurrentProcess();
		Assert.Equal(Environment.ProcessPath,ProcessIdentity.ImagePath(self),StringComparer.OrdinalIgnoreCase);
		Assert.NotNull(ProcessIdentity.IsElevated(self.Id));
		Assert.Null(ProcessIdentity.IsElevated(0));
	}

	/// <summary>Killing a process is a write, and an unelevated Gateway is refused it against an elevated
	/// host even though it can read that host's identity. Discovering that at Process.Kill costs the
	/// caller every session, because the replacement detaches them all first to make the close safe -- so
	/// the refusal has to come before any of that. An unreadable token must not refuse, or a machine where
	/// the query is unavailable could never replace a host at all.</summary>
	[Fact]
	public void Replacement_refuses_before_detaching_when_it_could_never_close_the_host() {
		var elevatedHost=new[]{(4242,(bool?)true)};
		var refused=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireReplacementPermitted(elevatedHost,false));
		Assert.Equal("replace_requires_elevation",refused.Code);
		Assert.Contains("pid 4242",refused.Message);
		Assert.Contains("Nothing was detached",refused.Message);

		// An elevated Gateway closes an elevated host, and elevation it cannot read is left to the
		// existing failure path rather than guessed at.
		DeploymentService.RequireReplacementPermitted(elevatedHost,true);
		DeploymentService.RequireReplacementPermitted(elevatedHost,null);
		DeploymentService.RequireReplacementPermitted(new[]{(4242,(bool?)null)},false);
		DeploymentService.RequireReplacementPermitted(new[]{(4242,(bool?)false)},false);
	}

	[Fact]
	public void Replacement_refuses_to_kill_hosts_whose_sessions_cannot_all_be_observed() {
		var unavailable=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireReplacementSessionVisibility(1,null,false));
		Assert.Equal("replace_session_state_unknown",unavailable.Code);

		var ambiguous=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireReplacementSessionVisibility(2,new JsonObject(),false));
		Assert.Equal("replace_session_state_unknown",ambiguous.Code);

		DeploymentService.RequireReplacementSessionVisibility(1,null,true);
		DeploymentService.RequireReplacementSessionVisibility(2,new JsonObject(),true);
	}

	/// <summary>A replacement that did not replace anything must not report success. Both waits in
	/// ReplaceRunningHostAsync are bounded and neither was checked -- WaitForExit's Boolean was discarded
	/// and the poll loop just ran out of attempts -- so a host that outlived them let the caller start a
	/// second dnSpy into the endpoint the first still held. That is the two-hosts contention the
	/// replacement path exists to prevent, arrived at through the path meant to prevent it.
	///
	/// The boundary worth pinning is one survivor among the dead: an "any exited, so we are fine" reading
	/// passes that case and is wrong.</summary>
	[Fact]
	public void Replacement_fails_rather_than_launching_beside_a_host_that_would_not_exit() {
		DeploymentService.RequireReplacedHostsExited(Array.Empty<(int,bool)>());
		DeploymentService.RequireReplacedHostsExited(new[]{(1234,false)});
		DeploymentService.RequireReplacedHostsExited(new[]{(1234,false),(5678,false)});

		var survivor=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireReplacedHostsExited(new[]{(1234,false),(5678,true)}));
		Assert.Equal("replace_failed",survivor.Code);
		Assert.Contains("pid 5678",survivor.Message);
		// The one that died is not what the operator has to act on, so it is not named.
		Assert.DoesNotContain("pid 1234",survivor.Message);
		Assert.Contains("two hosts",survivor.Message);

		var several=Assert.Throws<GatewayControlException>(()=>DeploymentService.RequireReplacedHostsExited(new[]{(1234,true),(5678,true)}));
		Assert.Equal("replace_failed",several.Code);
		Assert.Contains("pid 1234",several.Message);
		Assert.Contains("pid 5678",several.Message);
		Assert.Contains("hosts",several.Message);
	}

	[Fact]
	public void Replacement_fails_closed_when_process_exit_cannot_be_queried() {
		Assert.False(DeploymentService.IsStillAlive(1234,()=>true));
		Assert.True(DeploymentService.IsStillAlive(1234,()=>false));

		var unknown=Assert.Throws<GatewayControlException>(()=>DeploymentService.IsStillAlive(5678,()=>throw new InvalidOperationException("query failed")));
		Assert.Equal("replace_failed",unknown.Code);
		Assert.Contains("pid 5678",unknown.Message);
		Assert.Contains("Could not confirm",unknown.Message);
	}

	[Fact]
	public void Replacement_treats_failed_or_malformed_session_reads_as_unknown_not_empty() {
		var failed=Assert.Throws<GatewayControlException>(()=>DeploymentService.ReadLocalSessions(RpcResponse.Failure("request","host_unavailable","gone"),false));
		Assert.Equal("replace_session_state_unknown",failed.Code);
		var missing=Assert.Throws<GatewayControlException>(()=>DeploymentService.ReadLocalSessions(new RpcResponse(),false));
		Assert.Equal("replace_session_state_unknown",missing.Code);
		var malformed=Assert.Throws<GatewayControlException>(()=>DeploymentService.ReadLocalSessions(RpcResponse.Success("request",new { unexpected=true }),false));
		Assert.Equal("replace_session_state_unknown",malformed.Code);
		var mixed=Assert.Throws<GatewayControlException>(()=>DeploymentService.ReadLocalSessions(RpcResponse.Success("request",new object[]{new { session_id="ok" },"bad"}),false));
		Assert.Equal("replace_session_state_unknown",mixed.Code);

		Assert.Empty(DeploymentService.ReadLocalSessions(RpcResponse.Failure("request","host_unavailable","gone"),true));
		Assert.Empty(DeploymentService.ReadLocalSessions(RpcResponse.Success("request",Array.Empty<object>()),false));
	}

	// A version directory that cannot prove its provenance is an interrupted copy: the tree landed but the
	// manifest naming what it contains never did. dnSpy may already be running out of it, so repair deploys
	// beside it. Reusing it on the strength of its name alone is what let a half-copied tree serve requests.
	[Fact]
	public void A_deployment_directory_that_cannot_prove_its_provenance_is_deployed_beside_not_reused() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var version=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		var deployed=Path.Combine(root,"install","versions",version);
		File.Delete(Path.Combine(deployed,"deployment-manifest.json"));
		File.WriteAllText(Path.Combine(deployed,"bin","dnSpy.dll"),"left behind by an interrupted copy");
		File.Delete(Path.Combine(root,"install","current.json"));

		var repaired=new DeploymentService();
		Assert.True(repaired.EnsureBundledLocalHost(default));
		var replacement=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		Assert.NotEqual(version,replacement);
		// The unprovable tree is left exactly as it was; the good one is deployed alongside it.
		Assert.Equal("left behind by an interrupted copy",File.ReadAllText(Path.Combine(deployed,"bin","dnSpy.dll")));
		Assert.Equal("bin\\dnSpy.dll",File.ReadAllText(Path.Combine(root,"install","versions",replacement,"bin","dnSpy.dll")));
	}

	// The complement: a directory that *can* prove it matches the installed payload is re-adopted rather
	// than copied again, so recovering a lost current.json costs nothing.
	[Fact]
	public void A_deployment_directory_that_matches_the_payload_is_readopted_without_recopying() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var version=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		File.Delete(Path.Combine(root,"install","current.json"));

		Assert.True(new DeploymentService().EnsureBundledLocalHost(default));
		var readopted=(string)JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!["active_version"]!;
		Assert.Equal(version,readopted);
		Assert.Single(Directory.GetDirectories(Path.Combine(root,"install","versions")));
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
