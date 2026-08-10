using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
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
		var router=new HostRouter(); using var listener=new RemoteHostListener(router); var service=new DeploymentService(listener);
		var result=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"use_tls",false}},router,default)))!;
		var archive=(string)result["archive_path"]!; Assert.True(File.Exists(archive)); Assert.Equal("authenticated_plaintext",(string?)result["transport"]); Assert.True((bool?)result["gateway_ready"]); Assert.False((bool?)result["gateway_restart_required"]);
		var registry=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state","packages","gateway-hosts.json")))!; Assert.Equal("remote-a",(string?)registry["hosts"]?[0]?["host_id"]); Assert.Equal("127.0.0.1",(string?)registry["listener"]?["address"]); Assert.Equal(7352,(int?)registry["listener"]?["plaintext_port"]);
		Assert.True(router.IsRegistered("remote-a"));
	}
	[Fact]
	public async Task Remote_package_defaults_to_mutual_tls_and_can_replace_the_same_host() {
		var router=new HostRouter(); using var listener=new RemoteHostListener(router); var service=new DeploymentService(listener); var request=new JsonObject{{"host_id","remote-tls"},{"gateway_address","127.0.0.1"}};
		var first=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",request,router,default)))!;
		var second=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("create_remote_host_package",request,router,default)))!;
		Assert.Equal("mutual_tls",(string?)first["transport"]); Assert.True((bool?)second["replaced_existing_host"]); Assert.True(File.Exists(Path.Combine(root,"state","packages","remote-tls-client.cer")));
		var registry=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state","packages","gateway-hosts.json")))!; Assert.Single(registry["hosts"]!.AsArray()); Assert.Equal(7353,(int?)registry["tls"]?["port"]);
	}
	[Fact]
	public async Task Replacing_and_revoking_a_remote_host_take_effect_in_the_live_router() {
		var router=new HostRouter(); using var listener=new RemoteHostListener(router); var service=new DeploymentService(listener); var request=new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"use_tls",false}};
		await service.ExecuteAsync("create_remote_host_package",request,router,default); var tokenPath=Path.Combine(root,"state","packages","remote-a.token"); var firstToken=File.ReadAllText(tokenPath);
		Assert.True(router.TryAuthenticate("remote-a",firstToken,false,null,out _));
		var socketListener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); socketListener.Start(); using var firstRemote=new System.Net.Sockets.TcpClient(); var firstAccept=socketListener.AcceptTcpClientAsync(); await firstRemote.ConnectAsync(System.Net.IPAddress.Loopback,((System.Net.IPEndPoint)socketListener.LocalEndpoint).Port); using var firstGateway=await firstAccept; Assert.True(router.TryRegister("remote-a",firstGateway,new StreamReader(firstGateway.GetStream(),Encoding.UTF8,false,4096,true),new StreamWriter(firstGateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true},out _));

		await service.ExecuteAsync("create_remote_host_package",request,router,default); var secondToken=File.ReadAllText(tokenPath);
		Assert.NotEqual(firstToken,secondToken); Assert.False(router.TryAuthenticate("remote-a",firstToken,false,null,out _)); Assert.True(router.TryAuthenticate("remote-a",secondToken,false,null,out _)); Assert.Equal(0,await ReadAfterClose(firstRemote));
		using var secondRemote=new System.Net.Sockets.TcpClient(); var secondAccept=socketListener.AcceptTcpClientAsync(); await secondRemote.ConnectAsync(System.Net.IPAddress.Loopback,((System.Net.IPEndPoint)socketListener.LocalEndpoint).Port); using var secondGateway=await secondAccept; Assert.True(router.TryRegister("remote-a",secondGateway,new StreamReader(secondGateway.GetStream(),Encoding.UTF8,false,4096,true),new StreamWriter(secondGateway.GetStream(),new UTF8Encoding(false),4096,true){AutoFlush=true},out _));

		var revoked=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await service.ExecuteAsync("revoke_remote_host",new JsonObject{{"host_id","remote-a"},{"confirm",true}},router,default)))!;
		Assert.True((bool?)revoked["revoked"]); Assert.False((bool?)revoked["gateway_restart_required"]); Assert.False(router.TryAuthenticate("remote-a",secondToken,false,null,out _)); Assert.Equal(0,await ReadAfterClose(secondRemote)); socketListener.Stop();
		static async Task<int> ReadAfterClose(System.Net.Sockets.TcpClient client) { var buffer=new byte[1]; using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2)); return await client.GetStream().ReadAsync(buffer,timeout.Token); }
	}
	[Fact]
	public async Task Ordinary_gateway_start_consumes_the_persisted_listener_configuration() {
		var provisioningRouter=new HostRouter(); using(var provisioningListener=new RemoteHostListener(provisioningRouter)) await new DeploymentService(provisioningListener).ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"},{"use_tls",false}},provisioningRouter,default);
		var registry=Path.Combine(root,"state","packages","gateway-hosts.json"); Set("DGSPY_HOSTS_FILE",registry); Set("DGSPY_INCLUDE_LOCAL_HOST","true");
		var restartedRouter=new HostRouter(); using var restartedListener=new RemoteHostListener(restartedRouter); await restartedListener.StartAsync(default);
		using var connection=new System.Net.Sockets.TcpClient(); await connection.ConnectAsync(System.Net.IPAddress.Loopback,7352); Assert.True(connection.Connected); await restartedListener.StopAsync(default);
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
	/// <summary>The HookLab payload ships with every install and is deployed with the tree, so an install
	/// without it is incomplete in exactly the way this check exists to catch. Before this, an install
	/// missing it passed doctor, launch_local_host, EnsureBundledLocalHost, DeploymentFreshness and
	/// create_remote_host_package, and produced its first symptom inside a payload action -- where the
	/// failure looks like the action's fault rather than the install's.</summary>
	[Theory]
	[InlineData(PayloadFile)]
	[InlineData(PayloadManifestFile)]
	public async Task An_install_without_the_hooklab_payload_fails_readiness_instead_of_the_first_action(string missing) {
		var healthy=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		Assert.True((bool?)healthy["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!["ok"]);

		File.Delete(Path.Combine(PayloadRoot(),missing));
		var doctor=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		var check=doctor["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!;
		Assert.False((bool?)check["ok"]);
		Assert.Contains(missing,(string?)check["recovery"]);
		// And every other entry point refuses for the same reason, rather than one of them letting the
		// incomplete install through.
		var error=await Assert.ThrowsAsync<GatewayControlException>(()=>new DeploymentService().ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"}},new HostRouter(),default));
		Assert.Equal("installation_incomplete",error.Code); Assert.Contains(missing,error.Message);
		Assert.Contains(missing,Assert.Throws<GatewayControlException>(()=>new DeploymentService().EnsureBundledLocalHost(default)).Message);
	}

	/// <summary>Adoption is decided from where the running process actually loaded its extension. A path of
	/// any other shape is not a tree the Gateway can reason about, and unknown has to mean "no match":
	/// adopting a host whose tree cannot be identified is the outcome the comparison exists to prevent.</summary>
	[Fact]
	public void The_running_host_tree_is_identified_from_the_extension_it_loaded() {
		Assert.Equal(@"C:\hosts\v1",DeploymentService.RunningHostRoot(@"C:\hosts\v1\bin\Extensions\dgSpy\dgSpy.Extension.x.dll"));
		// Case and separator variation come from the host, not from us.
		Assert.Equal(@"C:\hosts\v1",DeploymentService.RunningHostRoot(@"C:\hosts\v1\BIN\extensions\DGSPY\DgSpy.Extension.X.dll"));
		Assert.Equal(@"C:\hosts\v1",DeploymentService.RunningHostRoot(@"C:\hosts\v1\bin\Extensions\dgSpy\..\dgSpy\dgSpy.Extension.x.dll"));

		foreach(var unknown in new string?[]{null,"","   ",@"C:\hosts\v1\bin\dgSpy.Extension.x.dll",@"C:\hosts\v1\bin\Extensions\dgSpy\dnSpy.dll",@"C:\dgSpy.Extension.x.dll"})
			Assert.Null(DeploymentService.RunningHostRoot(unknown));
	}

	/// <summary>The gap: adoption compared one file, so a change confined to the HookLab payload directory
	/// was invisible -- launch_local_host reported installed=false with the same active_version while doctor,
	/// which whole-tree hashes, called the same deployment stale. The comparison now covers the tree.</summary>
	[Fact]
	public async Task A_change_confined_to_the_payload_directory_is_not_adopted_as_the_installed_payload() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var deployed=DeployedRoot(root); var payload=PayloadRoot();
		Assert.True(Matches(service,deployed,ExtensionShaOf(deployed),payload));

		StageHookLabPayload(payload,"a rebuilt HookLab bootstrap that was never deployed");
		Assert.False(Matches(service,deployed,ExtensionShaOf(deployed),payload));

		// Non-vacuity, asserted rather than claimed: the rule this replaced -- the extension digest the
		// running host reports against the one in the payload -- still says these two trees are the same,
		// so this test fails against that comparison and passes only against a tree-wide one.
		Assert.Equal(FileSha(Path.Combine(deployed,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll")),FileSha(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll")));
		// And doctor saw it all along, which is the disagreement that made this worth fixing.
		var local=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("get_local_deployment",new JsonObject(),new HostRouter(),default)))!;
		Assert.True((bool?)local["payload"]!["stale"]);
	}

	/// <summary>A deployment that cannot prove what it contains is not adopted. The manifest is the only
	/// record tying a deployed tree to the payload it was copied from; without it the Gateway would be
	/// adopting on the strength of a directory name.</summary>
	[Fact]
	public void A_running_tree_with_no_deployment_manifest_is_not_adopted() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var deployed=DeployedRoot(root); var payload=PayloadRoot();
		// A payload that cannot be hashed at all, taken first because it needs a tree that is otherwise
		// adoptable -- an unreadable payload must refuse a match that everything else would have granted.
		Assert.False(Matches(service,deployed,ExtensionShaOf(deployed),payload,()=>throw new IOException("payload unreadable")));

		File.Delete(Path.Combine(deployed,"deployment-manifest.json"));
		Assert.Null(DeploymentService.DeployedTreePayloadSha(deployed));
		Assert.False(Matches(service,deployed,ExtensionShaOf(deployed),payload));
		// So is a tree the host never named.
		Assert.False(Matches(service,null,ExtensionShaOf(deployed),payload,()=>throw new InvalidOperationException("must not be reached")));
	}

	/// <summary>The P1 the path branch carried. A host running out of the payload root was adopted on the
	/// path alone, so rebuilding or editing that directory after dnSpy had loaded its extension left the
	/// process holding the old extension while the directory supplied a newer HookLab payload -- the
	/// cross-generation host/payload pairing the comparison exists to prevent, and the one case the
	/// extension-digest rule it replaced would have caught. The digest the host reports for the assembly it
	/// loaded is now compared with the assembly at that path now.</summary>
	[Fact]
	public void A_payload_root_rebuilt_after_the_host_loaded_its_extension_is_not_adopted() {
		var service=new DeploymentService(); var payload=PayloadRoot();
		var loaded=ExtensionShaOf(payload);
		Assert.True(Matches(service,payload,loaded,payload,()=>throw new InvalidOperationException("must not be reached")));

		// The rebuild: a new extension on disk, and a new HookLab payload beside it. The running process
		// still reports the digest it loaded, because that is what it is still executing.
		File.WriteAllText(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),"a rebuilt extension the running host never loaded");
		StageHookLabPayload(payload,"a newer HookLab bootstrap from that same rebuild");
		Assert.NotEqual(loaded,ExtensionShaOf(payload));
		Assert.False(Matches(service,payload,loaded,payload,()=>throw new InvalidOperationException("must not be reached")));
		// Non-vacuity in the other direction: the paths still agree, which is all the previous rule looked at.
		Assert.Equal(Path.TrimEndingDirectorySeparator(payload),DeploymentService.RunningHostRoot(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll")),ignoreCase:true);
		// A host that restarted against the rebuilt tree reports the new digest and is adopted again.
		Assert.True(Matches(service,payload,ExtensionShaOf(payload),payload,()=>throw new InvalidOperationException("must not be reached")));
	}

	/// <summary>Unknown is never adoption, and the extension digest is no exception. A host that could not
	/// hash its own assembly reports "unknown"; adopting on that would be adopting on nothing.</summary>
	[Fact]
	public void A_host_that_cannot_report_what_it_loaded_is_not_adopted() {
		var service=new DeploymentService(); var payload=PayloadRoot();
		foreach(var reported in new string?[]{null,"","   ","unknown","UNKNOWN","0000000000000000000000000000000000000000000000000000000000000000"})
			Assert.False(Matches(service,payload,reported,payload,()=>throw new InvalidOperationException("must not be reached")));
	}

	/// <summary>The P1 the manifest branch carried. <c>payload_sha256</c> records where a deployed tree's
	/// bytes came from, not what they are now, so a version directory altered after deployment -- without its
	/// manifest being touched -- still adopted as matching. The deployed tree is now hashed.</summary>
	[Fact]
	public void A_deployed_tree_altered_after_deployment_is_not_adopted() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var deployed=DeployedRoot(root); var payload=PayloadRoot();
		Assert.True(Matches(service,deployed,ExtensionShaOf(deployed),payload));

		// Mutate the deployed tree only, and leave every record alone. This is the shape a hand-patched
		// install has, and it is invisible to anything that reads the manifest.
		File.WriteAllText(Path.Combine(deployed,PayloadFile),"a HookLab payload swapped into the deployment after the fact");
		// A fresh service, so this proves the comparison rather than the cache-invalidation stamp.
		Assert.False(Matches(new DeploymentService(),deployed,ExtensionShaOf(deployed),payload));

		// Non-vacuity, asserted rather than claimed: the recorded provenance the previous rule compared is
		// untouched and still equals the installed payload's hash, so the old comparison still says "match".
		Assert.Equal(DeploymentService.DeployedTreePayloadSha(deployed),StagedPayloadShaAsync().GetAwaiter().GetResult());
	}

	/// <summary>And the same for a file whose contents changed without changing anything the manifest
	/// records -- the extension the host is not currently running, replaced in the deployed tree.</summary>
	[Fact]
	public void A_deployed_tree_whose_files_were_edited_in_place_is_not_adopted() {
		var service=new DeploymentService();
		Assert.True(service.EnsureBundledLocalHost(default));
		var deployed=DeployedRoot(root); var payload=PayloadRoot();
		Assert.True(Matches(service,deployed,ExtensionShaOf(deployed),payload));
		File.WriteAllText(Path.Combine(deployed,"bin","dnSpy.dll"),"a dnSpy.dll patched inside the deployed tree");
		Assert.False(Matches(new DeploymentService(),deployed,ExtensionShaOf(deployed),payload));
	}

	/// <summary>A host running straight out of the installed payload -- the developer worktree case, where
	/// the deployment and the payload are one directory -- is the same tree by construction, and must adopt
	/// without a manifest it has no reason to carry.</summary>
	[Fact]
	public void A_host_running_out_of_the_payload_directory_itself_is_adopted() {
		var service=new DeploymentService(); var payload=PayloadRoot(); var loaded=ExtensionShaOf(payload);
		Assert.False(File.Exists(Path.Combine(payload,"deployment-manifest.json")));
		Assert.True(Matches(service,payload,loaded,payload,()=>throw new InvalidOperationException("must not be reached")));
		// Reached the way LaunchLocalAsync reaches it, from the path the host reports.
		Assert.True(Matches(service,DeploymentService.RunningHostRoot(Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll")),loaded,payload,()=>""));
		Assert.True(Matches(service,payload+Path.DirectorySeparatorChar,loaded,payload,()=>""));
	}

	/// <summary>The P2. ValidateRemotePayload checked filenames, so an empty payload, a truncated one, a
	/// malformed manifest and a digest mismatch all stayed healthy through doctor, deployment and
	/// remote-package creation, and produced their first symptom inside a payload action -- exactly the late
	/// failure the required-files entry was added to eliminate, one layer down. Each case is driven through
	/// doctor (which must stop reporting healthy) and through EnsureBundledLocalHost (which must refuse to
	/// deploy it), because the value of the check is that every readiness path shares it.</summary>
	[Theory]
	[InlineData("empty")]
	[InlineData("truncated")]
	[InlineData("digest_mismatch")]
	[InlineData("malformed_manifest")]
	[InlineData("manifest_without_entry")]
	[InlineData("manifest_names_another_file")]
	[InlineData("manifest_without_digest")]
	public async Task A_payload_that_is_present_but_unverifiable_fails_readiness_instead_of_the_first_action(string corruption) {
		var payload=PayloadRoot();
		var healthy=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		Assert.True((bool?)healthy["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!["ok"]);

		var file=Path.Combine(payload,PayloadFile); var recorded=new FileInfo(file).Length; var sha=FileSha(file);
		switch(corruption) {
			// The manifest keeps describing the payload that was staged; only the bytes moved.
			case "empty": File.WriteAllText(file,"",new UTF8Encoding(false)); break;
			case "truncated": File.WriteAllText(file,"a HookLab bootstrap payl",new UTF8Encoding(false)); break;
			// Same length, different bytes: size alone cannot see this one.
			case "digest_mismatch": File.WriteAllText(file,new string('x',(int)recorded),new UTF8Encoding(false)); break;
			case "malformed_manifest": File.WriteAllText(Path.Combine(payload,PayloadManifestFile),"{\"format_version\":1,\"payloads\":[",new UTF8Encoding(false)); break;
			case "manifest_without_entry": WriteHookLabManifest(payload,recorded,sha,id:"something_else"); break;
			case "manifest_names_another_file": WriteHookLabManifest(payload,recorded,sha,file:"hooklab-bootstrap.net8.payload"); break;
			case "manifest_without_digest": File.WriteAllText(Path.Combine(payload,PayloadManifestFile),"{\"format_version\":1,\"payloads\":[{\"id\":\"hooklab_bootstrap\",\"file\":\"hooklab-bootstrap.net48.payload\"}]}",new UTF8Encoding(false)); break;
		}

		var doctor=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		var check=doctor["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!;
		Assert.False((bool?)check["ok"]);
		Assert.False((bool?)doctor["healthy"]);
		Assert.Contains("HookLab payload cannot be verified",(string?)check["recovery"]);
		// Every other readiness path refuses for the same reason rather than one of them letting it through.
		var error=await Assert.ThrowsAsync<GatewayControlException>(()=>new DeploymentService().ExecuteAsync("create_remote_host_package",new JsonObject{{"host_id","remote-a"},{"gateway_address","127.0.0.1"}},new HostRouter(),default));
		Assert.Equal("installation_incomplete",error.Code); Assert.Contains("HookLab payload cannot be verified",error.Message);
		Assert.Contains("HookLab payload cannot be verified",Assert.Throws<GatewayControlException>(()=>new DeploymentService().EnsureBundledLocalHost(default)).Message);
	}

	/// <summary>A payload and its own manifest rewritten together agree with each other, which is why the
	/// package manifest's independent record is read as well: rewriting one record is not enough. Absent is
	/// not a failure -- an unpackaged worktree has no package manifest -- but disagreeing is.</summary>
	[Fact]
	public async Task A_payload_rewritten_together_with_its_own_manifest_is_caught_by_the_package_record() {
		var payload=PayloadRoot();
		// The package manifest sits beside the payload root, which is where the Gateway already reads the
		// packaged provenance it records into every deployment.
		File.WriteAllText(Path.Combine(root,"manifest.json"),"{\"format_version\":1,\"hooklab_payload_sha256\":\""+FileSha(Path.Combine(payload,PayloadFile))+"\"}",new UTF8Encoding(false));
		var healthy=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		Assert.True((bool?)healthy["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!["ok"]);

		// Both records under the payload root rewritten consistently: the manifest check below passes and
		// only the independent one can tell.
		StageHookLabPayload(payload,"a substituted HookLab bootstrap, staged with a manifest that describes it");
		var doctor=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		var check=doctor["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!;
		Assert.False((bool?)check["ok"]);
		Assert.Contains("the package manifest records",(string?)check["recovery"]);
	}

	/// <summary>A package manifest that exists and cannot be parsed must refuse, not fall back to the
	/// unpackaged-worktree path. Reading it is the only check that catches a payload and its own manifest
	/// rewritten together, so treating unreadable as absent turns that check off for precisely the install
	/// most likely to be damaged - and does it silently, while readiness still reports healthy.</summary>
	[Fact]
	public async Task A_package_manifest_that_cannot_be_read_refuses_rather_than_skipping_the_independent_check() {
		var payload=CreatePayload();
		Environment.SetEnvironmentVariable("DGSPY_PAYLOAD_ROOT",payload);
		File.WriteAllText(Path.Combine(root,"manifest.json"),"{\"format_version\":1,\"hooklab_payload_sha256\": \"unterminated",new UTF8Encoding(false));
		var doctor=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("doctor",new JsonObject(),new HostRouter(),default)))!;
		var check=doctor["checks"]!.AsArray().Single(item=>(string?)item?["name"]=="bundled_host_payload")!;
		Assert.False((bool?)check["ok"]);
		Assert.Contains("could not be read",(string?)check["recovery"]);
	}

	string CreatePayload() { var path=Path.Combine(root,"payload"); foreach(var directory in new[]{"bin","bin\\Extensions\\dgSpy","hooklab","launcher"}) Directory.CreateDirectory(Path.Combine(path,directory)); foreach(var file in new[]{"dnSpy.exe","bin\\dnSpy.dll","bin\\dnSpy.Contracts.DnSpy.dll","bin\\hostfxr.dll","bin\\hostpolicy.dll","bin\\coreclr.dll","bin\\clrjit.dll","bin\\Extensions\\dgSpy\\dgSpy.Extension.x.dll","launcher\\Start-dgSpyRemoteHost.ps1","launcher\\Start-dgSpyRemoteHost.cmd"}) File.WriteAllText(Path.Combine(path,file),file); StageHookLabPayload(path,"a HookLab bootstrap payload"); return path; }
	const string PayloadFile="hooklab\\hooklab-bootstrap.net48.payload";
	const string PayloadManifestFile="hooklab\\hooklab-payload-manifest.json";
	/// <summary>Writes the payload and a manifest that describes it, which is what a real staging step does.
	/// Rewriting only one of the two is a corruption case, and it gets its own tests below.</summary>
	static void StageHookLabPayload(string payloadRoot,string content) {
		var file=Path.Combine(payloadRoot,PayloadFile); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file,content,new UTF8Encoding(false));
		WriteHookLabManifest(payloadRoot,new FileInfo(file).Length,FileSha(file));
	}
	static void WriteHookLabManifest(string payloadRoot,long size,string sha256,string file="hooklab-bootstrap.net48.payload",string id="hooklab_bootstrap") =>
		File.WriteAllText(Path.Combine(payloadRoot,PayloadManifestFile),
			"{\"format_version\":1,\"payloads\":[{\"id\":\""+id+"\",\"file\":\""+file+"\",\"assembly_name\":\"HookLab.Bootstrap\",\"target_framework\":\"net48\",\"architecture\":\"x64\",\"delivery\":\"bytes_only\",\"size\":"+size+",\"sha256\":\""+sha256+"\"}]}\n",
			new UTF8Encoding(false));
	static string PayloadRoot() => Environment.GetEnvironmentVariable("DGSPY_REMOTE_PAYLOAD_ROOT")!;
	static string ExtensionShaOf(string root) => FileSha(Path.Combine(root,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"));
	/// <summary>The adoption comparison, wired the way <c>LaunchLocalAsync</c> wires it, so a test never
	/// exercises a combination of delegates production does not use.</summary>
	static bool Matches(DeploymentService service,string? runningRoot,string? reportedExtensionSha,string payloadRoot,Func<string>? installedTreeSha=null) =>
		DeploymentService.RunningTreeMatchesPayload(runningRoot,reportedExtensionSha,payloadRoot,
			installedTreeSha ?? (()=>StagedPayloadShaAsync().GetAwaiter().GetResult()),
			DeploymentService.DeployedTreePayloadSha,DeploymentService.ExtensionSha,service.HashDeployedTreeCached);
	/// <summary>The hash of the installed payload tree, read back from the Gateway's own freshness report so
	/// the test never reimplements the hashing it is supposed to be checking against.</summary>
	static async Task<string> StagedPayloadShaAsync() {
		var local=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(await new DeploymentService().ExecuteAsync("get_local_deployment",new JsonObject(),new HostRouter(),default)))!;
		return (string)local["payload"]!["staged_payload_sha256"]!;
	}
	static string DeployedRoot(string root) { var current=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"install","current.json")))!; return Path.Combine(root,"install","versions",(string)current["active_version"]!); }
	static string FileSha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
	void Set(string name,string value) { previous[name]=Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name,value); }
	public void Dispose() { foreach(var item in previous) Environment.SetEnvironmentVariable(item.Key,item.Value); if(Directory.Exists(root)) Directory.Delete(root,true); }
}
