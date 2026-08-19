using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using dgSpy.Protocol;

namespace dgSpy.Gateway;

internal sealed class LocalHostLaunchGate {
	readonly SemaphoreSlim gate=new(1,1);
	public async Task<T> RunAsync<T>(Func<Task<T>> action,CancellationToken token) {
		await gate.WaitAsync(token);
		try { return await action(); }
		finally { gate.Release(); }
	}
}

public sealed class DeploymentService {
	readonly string stateRoot;
	readonly string installRoot;
	readonly string packageRoot;
	readonly RemoteHostListener? remoteListener;
	readonly GatewayDevelopmentTranscript transcript;
	readonly LocalHostLaunchGate localHostLaunch=new();
	internal static string DefaultStateRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
	public DeploymentService(RemoteHostListener? remoteListener=null,GatewayDevelopmentTranscript? transcript=null) {
		this.remoteListener=remoteListener;
		this.transcript=transcript ?? new GatewayDevelopmentTranscript();
		stateRoot=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? DefaultStateRoot();
		installRoot=Environment.GetEnvironmentVariable("DGSPY_INSTALL_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","dgSpy");
		packageRoot=Path.GetFullPath(Environment.GetEnvironmentVariable("DGSPY_PACKAGE_ROOT") ?? Path.Combine(stateRoot,"packages"));
	}
	public static bool IsGatewayOperation(string operation) => operation is "get_started" or "doctor" or "get_workflow_help" or "get_local_deployment" or "launch_local_host" or "rollback_local_deployment" or "uninstall_local_deployment" or "create_remote_host_package" or "get_remote_host_readiness" or "revoke_remote_host";
	public static bool IsMutation(string operation) => operation is "launch_local_host" or "rollback_local_deployment" or "uninstall_local_deployment" or "create_remote_host_package" or "revoke_remote_host";

	public async Task<object> ExecuteAsync(string operation,JsonObject args,HostRouter router,CancellationToken token) => operation switch {
		"get_started" => await GetStartedAsync(router,token),
		"doctor" => await DoctorAsync(router,token),
		"get_workflow_help" => WorkflowCatalog.Lookup((string?)args["topic"]),
		"get_local_deployment" => GetLocal(),
		"launch_local_host" => await localHostLaunch.RunAsync(()=>LaunchLocalAsync(args,router,token),token),
		"rollback_local_deployment" => Rollback(args),
		"uninstall_local_deployment" => Uninstall(args),
		"create_remote_host_package" => await CreateRemoteAsync(args,router,token),
		"get_remote_host_readiness" => await RemoteReadinessAsync((string?)args["host_id"],router,token),
		"revoke_remote_host" => RevokeRemote(args,router),
		_ => throw new GatewayControlException("unknown_tool",$"Unknown Gateway operation '{operation}'.")
	};

	async Task<object> GetStartedAsync(HostRouter router,CancellationToken token) {
		var hosts=await router.ListHostsAsync(token); var local=GetLocal(); var serialized=hosts.Select(item=>System.Text.Json.JsonSerializer.Serialize(item)).ToArray(); var connected=serialized.Any(item=>item.Contains("\"state\":\"connected\"",StringComparison.Ordinal)); var degraded=serialized.Any(item=>item.Contains("\"state\":\"degraded\"",StringComparison.Ordinal));
		// Staleness outranks "you are connected": a connected host running superseded code is the case that
		// wastes the most time, because everything else looks healthy.
		var stale=(bool?)System.Text.Json.JsonSerializer.SerializeToNode(local)!["payload"]?["stale"]==true;
		// Build identity belongs in the first call an agent makes, not only in doctor: by the time
		// anything looks wrong enough to run diagnostics, the wrong conclusions have already been drawn
		// from tool descriptions that were never questioned. A transcript that opens with both commits
		// makes a later report either reproducible against a known tree or visibly not.
		var skew=GatewayBuild.Skew(hosts); var skewed=(bool?)System.Text.Json.JsonSerializer.SerializeToNode(skew)!["skewed"]==true;
		return new { gateway="ready",gateway_build=GatewayBuild.Describe(),build_skew=skew,development_transcript=transcript.Describe(),access_mode=Environment.GetEnvironmentVariable("DGSPY_ACCESS_MODE") ?? "full-control",local_deployment=local,hosts,recommended_next_action=stale ? "the installed payload is newer than the running deployment: call launch_local_host with replace=true before trusting any result, and finish or hand off any live session first because replacing ends it" : skewed ? "the Gateway and the debugger host are not the same build: read build_skew before trusting any tool description or response shape, and quote both commits in anything you report" : connected ? "select a connected host, then attach or launch; once attached, find symbols with search" : degraded ? "inspect dispatcher and evaluation queue faults before issuing debugger control" : "call launch_local_host, or create one remote host package" };
	}
	async Task<object> DoctorAsync(HostRouter router,CancellationToken token) {
		var checks=new List<object>();
		checks.Add(Check("state_root",Directory.Exists(stateRoot),stateRoot,Directory.Exists(stateRoot)?null:"Created on first mutation."));
		checks.Add(Check("local_deployment",true,Directory.Exists(installRoot)?installRoot:"not installed",Directory.Exists(installRoot)?null:"Optional: call launch_local_host; it installs the bundled host automatically."));
		var payload=RemotePayloadRoot(); try { ValidateRemotePayload(payload); checks.Add(Check("bundled_host_payload",true,payload,null)); } catch(GatewayControlException ex) { checks.Add(Check("bundled_host_payload",false,payload,ex.Message)); }
		// A stale deployment is not a broken one: every other check passes while the debugger answers from
		// code that no longer exists in the tree. Doctor has to say so out loud or nobody finds out.
		var freshness=DeploymentFreshness(); var freshnessJson=System.Text.Json.JsonSerializer.SerializeToNode(freshness)!;
		checks.Add(Check("deployment_freshness",(bool?)freshnessJson["stale"]!=true,(string?)freshnessJson["detail"] ?? "",(string?)freshnessJson["recovery"]));
		var registry=Environment.GetEnvironmentVariable("DGSPY_HOSTS_FILE"); checks.Add(Check("host_registry",string.IsNullOrWhiteSpace(registry)||File.Exists(registry),registry ?? "implicit local host",string.IsNullOrWhiteSpace(registry)||File.Exists(registry)?null:"Configured registry is missing."));
		object[] hosts; try { hosts=await router.ListHostsAsync(token); var serialized=hosts.Select(item=>System.Text.Json.JsonSerializer.Serialize(item)).ToArray(); var connected=serialized.Count(item=>item.Contains("\"state\":\"connected\"",StringComparison.Ordinal)); var degraded=serialized.Count(item=>item.Contains("\"state\":\"degraded\"",StringComparison.Ordinal)); var available=connected+degraded;
			// A contained dispatcher fault leaves a host connected and usable, so it must not fail this
			// check — but it is still the trace of something that went wrong on the debugger thread, and
			// silence about it is how a fault went unnoticed until control operations stopped working.
			var faulted=serialized.Count(item=>item.Contains("\"dispatcher_state\":\"faulted\"",StringComparison.Ordinal));
			var unavailable=serialized.Count(item=>item.Contains("\"dispatcher_state\":\"unavailable\"",StringComparison.Ordinal));
			var ok=connected>0 && degraded==0; checks.Add(Check("hosts",ok,$"{hosts.Length} registered, {connected} connected, {degraded} degraded, {faulted} with contained dispatcher faults, {unavailable} with a dead dispatcher",ok?(faulted>0?"A host recorded a contained dispatcher fault; read last_dispatcher_fault and re-read session state before trusting anything from around that time.":null):unavailable>0?"A host's debugger thread is gone: it cannot run any control operation and its sessions are dead. Read dispatcher_recovery on that host.":available>0?"Inspect the host dispatcher/evaluation fault fields before retrying control operations.":"Start dnSpy locally or connect a provisioned remote host.")); } catch(Exception ex) { hosts=Array.Empty<object>(); checks.Add(Check("hosts",false,ex.GetType().Name,"Repair the host registry or credentials.")); }
		// Skew fails the check rather than merely reporting it, for the same reason deployment freshness
		// does: every other check passes while the answers come from two different trees, and a finding
		// that leaves healthy=true is one an agent scanning for trouble sails straight past.
		var skew=GatewayBuild.Skew(hosts); var skewJson=System.Text.Json.JsonSerializer.SerializeToNode(skew)!;
		checks.Add(Check("build_skew",(bool?)skewJson["skewed"]!=true,
			$"gateway {GatewayBuild.BuildLabel}; {(string?)skewJson["gateway_vs_hosts"]?["detail"]} {(string?)skewJson["gateway_process_vs_disk"]?["detail"]}".Trim(),
			(string?)skewJson["gateway_vs_hosts"]?["recovery"] ?? (string?)skewJson["gateway_process_vs_disk"]?["recovery"]));
		return new { healthy=checks.All(c=>(bool)c.GetType().GetProperty("ok")!.GetValue(c)!),gateway_build=GatewayBuild.Describe(),build_skew=skew,development_transcript=transcript.Describe(),state_root=stateRoot,install_root=installRoot,checks,hosts };
	}
	static object Check(string name,bool ok,string detail,string? recovery) => new { name,ok,detail,recovery };
	object GetLocal() { var current=ReadCurrent(); var active=(string?)current?["active_version"]; return new { installed=active is not null,active_version=active,previous_version=(string?)current?["previous_version"],host_id=(string?)current?["host_id"],install_path=active is null ? null : Path.Combine(installRoot,"versions",active),launcher=Path.Combine(installRoot,"current","Start-dgSpy.cmd"),payload=DeploymentFreshness() }; }
	// Starting a second dnSpy while one is already up is the failure this method exists to prevent. The
	// running process keeps the RPC endpoint, so the new one composes, finds the port taken, and sits
	// there doing nothing while every answer keeps coming from the build that was supposed to be
	// superseded. Nothing in the result distinguishes that from success, which is what makes it
	// expensive: it is discovered only when results start disagreeing with the source.
	//
	// So this never starts a process while one is alive. Exactly one of three things happens, and the
	// result says which: adopt the running host when it is already the build we would deploy, refuse
	// with the process id and both builds when it is not, or -- only when the caller asked for it --
	// replace it, which detaches its targets first and then closes it.
	async Task<object> LaunchLocalAsync(JsonObject args,HostRouter router,CancellationToken token) {
		var replace=(bool?)args["replace"]==true; var allowTerminate=(bool?)args["allow_terminate"]==true; var elevated=(bool?)args["elevated"]==true;
		// Before anything is closed or installed. Elevation goes through ShellExecute, which cannot carry
		// an environment block -- the consent service builds the child's -- so an elevated host only works
		// where it would compute the same values unaided. Discovering that after replace=true had already
		// detached and closed the running host would leave the caller with no host at all and a refusal.
		if(elevated) RequireElevatedLaunchEnvironment(stateRoot,Environment.GetEnvironmentVariable);
		// The running-host decision is made before anything is deployed. Installing first would move
		// current.json onto a version that is not the one running, and deployment freshness -- the check
		// that catches a host answering from superseded code -- compares exactly those two things.
		var payloadRoot=RemotePayloadRoot(); ValidateRemotePayload(payloadRoot); var payloadExtensionSha=ExtensionSha(payloadRoot);
		var running=RunningManagedHosts();
		if(running.Length>0) {
			var live=await ConnectedLocalHostAsync(router,token,running.Select(process=>process.Id).ToArray());
			// Which tree the running host loaded from, taken from the host's own report rather than from
			// active_version. An adopted host can be running a version the pointer no longer names, so
			// anything resolved by active version -- a payload to inject, most of all -- could come from a
			// tree the host is not running.
			var runningRoot=live is null ? null : RunningHostRoot((string?)live["extension_path"]);
			var matches=RunningTreeMatchesPayload(runningRoot,(string?)live?["extension_sha256"],payloadRoot,()=>HashTreeCached(payloadRoot),DeployedTreePayloadSha,ExtensionSha,HashDeployedTreeCached);
			// Adoption is the honest answer to "make sure the local host is running" when it already is,
			// running the code we would have deployed. Reporting started=true for a process we did not
			// start would be a lie the caller cannot check.
			// Elevation is not a property of the payload, so a host that matches by build can still be the
			// wrong host for this call. Adopting it while the caller asked for elevation would answer
			// "connected" to a request for access the adopted process does not have, and the caller would
			// only find out when a target stayed invisible.
			var runningElevation=running.Length==1 ? ProcessIdentity.IsElevated(running[0].Id) : null;
			var decision=DecideRunningHost(elevated,runningElevation,replace);
			if(matches && running.Length==1 && decision==RunningHostDecision.RefuseElevation)
				RequireAdoptedHostElevated(running[0].Id,runningElevation);
			if(matches && running.Length==1 && decision==RunningHostDecision.Adopt) {
				var adoptedCurrent=ReadCurrent();
				return new { started=false,adopted=true,installed=false,redeployed=false,replaced=false,active_version=(string?)adoptedCurrent?["active_version"],
					extension_sha256=payloadExtensionSha,host_root=runningRoot,process_id=(int?)live!["dnspy_process_id"] ?? running[0].Id,connected=true,host_id=(string?)adoptedCurrent?["host_id"],
					elevated=runningElevation,recovery=(string?)null,detail="A local host already running this exact payload was adopted rather than duplicated." };
			}
			var describe=string.Join(", ",running.Select(process=>$"pid {process.Id}"));
			var runningBuild=live is null ? "not answering RPC" : $"build {(string?)live["build_label"] ?? "unknown"}, extension {Shorten((string?)live["extension_sha256"])}";
			// A tree rebuilt in place is the case that used to adopt, so name it: the paths agree and the
			// bytes do not, and "the tree it is running is not the payload" alone would read as a bug.
			var rebuiltInPlace=runningRoot is not null && PathsEqual(runningRoot,payloadRoot) && !string.Equals((string?)live?["extension_sha256"],ExtensionSha(runningRoot),StringComparison.OrdinalIgnoreCase);
			var runningTree=live is null ? "" : runningRoot is null ? " The Gateway could not tell which tree it loaded its extension from, so it cannot be adopted."
				: rebuiltInPlace ? $" It is running from '{runningRoot}', which is this payload directory, but that directory has been rebuilt since the host loaded its extension: the process holds extension {Shorten((string?)live["extension_sha256"])} while the directory now supplies {Shorten(ExtensionSha(runningRoot))}, so the host and the HookLab payload it would inject come from different generations."
				: $" It is running from '{runningRoot}'.";
			if(!replace)
				throw new GatewayControlException("host_already_running",
					$"A managed dnSpy is already running ({describe}; {runningBuild}) and owns the RPC endpoint, but the tree it is running is not the payload this deployment would install (extension {Shorten(payloadExtensionSha)}).{runningTree} Starting a second host would leave two processes contending for the endpoint, and every answer would keep coming from the old one while this call reported success. Call launch_local_host again with replace=true to detach its targets, close it, and start the installed payload -- that ends any debugging session it holds. To keep the session, finish with the running host instead.");
			await ReplaceRunningHostAsync(running,live,router,allowTerminate,token);
		}

		var installed=EnsureBundledLocalHost(token); var current=ReadCurrent()!; var version=(string)current["active_version"]!;
		var versionRoot=Path.Combine(installRoot,"versions",version); var executable=Path.Combine(versionRoot,"dnSpy.exe"); ValidateDnSpy(versionRoot);
		var deployedSha=DeployedPayloadSha(version); var deployedExtensionSha=ExtensionSha(versionRoot); var hostId=(string?)current["host_id"];
		// The host the gateway starts is a headless one, and only this switch tells dnSpy that. Without
		// it dnSpy pulls itself to the foreground on every debugger stop, and its "stop debugging?"
		// prompt on close waits for a person who is not there -- which blocks the orderly exit that
		// detaches targets before the process dies. Every other launcher (the remote host, the smokes)
		// already passes it; the gateway was the one that did not.
		ProcessStartInfo start;
		if(elevated) {
			BackfillOwnWindowsEnvironment();
			start=new ProcessStartInfo(executable,"--dgspy-no-window-activation") { UseShellExecute=true,Verb="runas" };
		}
		else { start=new ProcessStartInfo(executable,"--dgspy-no-window-activation") { UseShellExecute=false }; start.Environment["DGSPY_STATE_ROOT"]=stateRoot; BackfillWindowsEnvironment(start.Environment); }
		// The scan above finds dnSpy processes under this install root. A host installed somewhere else --
		// another dgSpy install, a hand-started build -- is invisible to it and still owns the port, which
		// is the two-hosts state by another route. The endpoint itself is the only thing that answers this
		// without guessing, so ask it directly and refuse rather than launch into an owned port.
		RequireLocalRpcEndpointFree(LocalRpcPort(),running.Length>0);
		Process process;
		try { process=Process.Start(start) ?? throw new InvalidOperationException("dnSpy did not start."); }
		catch(System.ComponentModel.Win32Exception ex) when (elevated && ex.NativeErrorCode==ErrorCancelled) {
			throw new GatewayControlException("elevation_declined",
				$"The elevation prompt for the local host was dismissed or denied, so no host was started{(running.Length>0 ? " -- and the host that was running has already been replaced, so there is now no local host at all" : "")}. Approve the User Account Control prompt and call launch_local_host again, or call it without elevated=true for a host that runs at the Gateway's own integrity level.");
		}
		// Elevation is reported from what happened, not from what was asked. A successful runas start is
		// itself the proof for the elevated path; every other path has to ask the process.
		var launchedElevation=elevated ? true : ProcessIdentity.IsElevated(process.Id);
		for(var attempt=0;attempt<20;attempt++) {
			await Task.Delay(250,token);
			var hosts=await router.ListHostsAsync(token);
			if(hosts.Any(item=>System.Text.Json.JsonSerializer.Serialize(item).Contains("\"state\":\"connected\"",StringComparison.Ordinal)))
				return new { started=true,adopted=false,installed,redeployed=installed,replaced=running.Length>0,active_version=version,payload_sha256=deployedSha,extension_sha256=deployedExtensionSha,process_id=process.Id,connected=true,host_id=hostId,elevated=launchedElevation,recovery=(string?)null };
		}
		return new { started=true,adopted=false,installed,redeployed=installed,replaced=running.Length>0,active_version=version,payload_sha256=deployedSha,extension_sha256=deployedExtensionSha,process_id=process.Id,connected=false,host_id=hostId,elevated=launchedElevation,recovery="Call doctor; dnSpy may still be composing extensions." };
	}
	const int ErrorCancelled=1223;

	internal enum RunningHostDecision { Adopt,RefuseElevation,Replace }
	/// <summary>What to do with a running host that already matches the installed payload. Elevation is
	/// the only thing that can make a matching host the wrong one, and the caller who passed replace has
	/// already said what to do about that -- so refusing must not pre-empt them. Refusing regardless made
	/// host_not_elevated advertise a recovery ("call again with replace=true and elevated=true") that the
	/// code could never reach, so following the instruction reproduced the error verbatim.</summary>
	internal static RunningHostDecision DecideRunningHost(bool elevated,bool? runningElevation,bool replace) =>
		!elevated || runningElevation==true ? RunningHostDecision.Adopt
		: replace ? RunningHostDecision.Replace
		: RunningHostDecision.RefuseElevation;

	/// <summary>Refuses to adopt a running host when the caller asked for an elevated one and cannot be
	/// shown that it is. An undeterminable token fails the same way as a medium-integrity one: the whole
	/// point of the flag is access the caller could not otherwise get, and a guess about that is worth
	/// nothing.</summary>
	internal static void RequireAdoptedHostElevated(int processId,bool? elevation) {
		if(elevation==true) return;
		throw elevation==false
			? new GatewayControlException("host_not_elevated",
				$"A local host (pid {processId}) is already running the installed payload, but it is not elevated, so adopting it would not give the elevated debugger access this call asked for -- processes owned by other users and by services would stay invisible exactly as they are now. It was left running and nothing was started. Call launch_local_host again with replace=true and elevated=true to close it and launch an elevated host, which ends any debugging session it holds, or drop elevated to adopt the host as it is.")
			: new GatewayControlException("host_elevation_unknown",
				$"A local host (pid {processId}) is already running the installed payload, but the Gateway could not read its token to prove whether it is elevated, so it cannot honestly report that this call produced elevated access. It was left running and nothing was started. Call launch_local_host with replace=true and elevated=true to close it and launch a host whose elevation is known, or drop elevated to adopt the host as it is.");
	}

	/// <summary>The variables an elevated launch cannot deliver. The host reads its state root, identity,
	/// credential and port from its own environment, and a ShellExecute child gets none of ours -- so an
	/// elevated launch is only sound where the host would compute the same values unaided. Same user, so
	/// the default state root under LOCALAPPDATA resolves identically on both sides; anything the operator
	/// overrode does not, and the resulting host would either answer on a port nobody dials or fail the
	/// handshake with a credential nobody shares.</summary>
	internal static void RequireElevatedLaunchEnvironment(string stateRoot,Func<string,string?> readEnvironment) {
		var expected=DefaultStateRoot();
		if(!PathsEqual(stateRoot,expected))
			throw new GatewayControlException("elevated_launch_unsupported_environment",
				$"An elevated local host cannot be started while the Gateway uses a custom state root ('{stateRoot}'): elevation goes through ShellExecute, which cannot pass DGSPY_STATE_ROOT to the child, so the elevated host would read '{expected}' instead and the two sides would not share a credential. Nothing was started. Run the Gateway with the default state root, or start the host elevated yourself with DGSPY_STATE_ROOT set and call launch_local_host without elevated to adopt it.");
		var overridden=new[]{"DGSPY_HOST_ID","DGSPY_RPC_TOKEN","DGSPY_RPC_PORT"}.Where(name=>!string.IsNullOrWhiteSpace(readEnvironment(name))).ToArray();
		if(overridden.Length>0)
			throw new GatewayControlException("elevated_launch_unsupported_environment",
				$"An elevated local host cannot be started while {string.Join(" and ",overridden)} {(overridden.Length==1 ? "is" : "are")} set: elevation goes through ShellExecute, which cannot pass {(overridden.Length==1 ? "it" : "them")} to the child, so the elevated host would generate or default {(overridden.Length==1 ? "its own value" : "its own values")} and the Gateway could not reach it. Nothing was started. Unset {(overridden.Length==1 ? "it" : "them")} and retry, or start the host elevated yourself with {(overridden.Length==1 ? "it" : "them")} set and call launch_local_host without elevated to adopt it.");
	}
	static bool PathsEqual(string left,string right) {
		static string Normalize(string path) { try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch { return path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar); } }
		return string.Equals(Normalize(left),Normalize(right),StringComparison.OrdinalIgnoreCase);
	}

	static int LocalRpcPort() => int.TryParse(Environment.GetEnvironmentVariable("DGSPY_RPC_PORT"),out var port) ? port : 7351;
	/// <summary>Whether something already accepts connections on the local RPC endpoint. Only a completed
	/// connection counts as owned. This check is defence in depth behind the running-host scan, so a probe
	/// that cannot run must not block a launch that scan already cleared -- refusing to start any host at
	/// all is the worse failure of the two.</summary>
	internal static bool LocalRpcEndpointOwned(int port,Func<int,bool> connect) { try { return connect(port); } catch { return false; } }
	static void RequireLocalRpcEndpointFree(int port,bool replaced) {
		if(!LocalRpcEndpointOwned(port,TryConnectLoopback)) return;
		throw new GatewayControlException("local_rpc_endpoint_owned",
			$"Something is already listening on the local debugger endpoint 127.0.0.1:{port}, and it is not a dnSpy from this managed install, so nothing was started{(replaced ? " -- the host that was running has already been replaced, so recover before retrying" : "")}. Launching now would produce a second host that composes, finds the port taken, and sits idle while every answer keeps coming from whatever owns the port. Close the process holding it -- another dgSpy install or a hand-started dnSpy are the usual owners -- and call launch_local_host again.");
	}
	internal static bool TryConnectLoopback(int port) {
		using var client=new System.Net.Sockets.TcpClient();
		var connect=client.ConnectAsync(IPAddress.Loopback,port);
		// A refused connection faults the task, and on loopback that is the ordinary answer for a free
		// port -- not an error to propagate.
		try { return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected; } catch(AggregateException) { return false; }
	}

	/// <summary>Closing a host that holds an ICorDebug attachment kills the target with it -- verified,
	/// not assumed. So a replacement detaches every session first and only then closes the process. A
	/// target the engine cannot detach from without killing it stops the replacement instead, because
	/// destroying whatever the user was debugging is never an acceptable side effect of a redeploy.</summary>
	async Task ReplaceRunningHostAsync(Process[] running,JsonNode? live,HostRouter router,bool allowTerminate,CancellationToken token) {
		// Before the detaches, not after the kill. A medium-integrity Gateway cannot terminate an elevated
		// host, and the natural place to discover that is Process.Kill -- by which point every session has
		// already been detached to make the close safe. The caller would have lost the debugging state the
		// detach was protecting and still be looking at the host it asked to replace.
		RequireReplacementPermitted(running.Select(process=>(SafePid(process),ProcessIdentity.IsElevated(SafePid(process)))).ToArray(),ProcessIdentity.IsElevated(Environment.ProcessId));
		RequireReplacementSessionVisibility(running.Length,live,allowTerminate);
		if(live is not null) {
			foreach(var session in await LocalSessionsAsync(router,allowTerminate,token)) {
				var sessionId=(string?)session["session_id"]; if(string.IsNullOrEmpty(sessionId)) continue;
				if((bool?)session["can_detach_without_terminating"]!=true && !allowTerminate)
					throw new GatewayControlException("replace_would_terminate",
						$"The running host cannot detach from session '{sessionId}' (processes {string.Join(", ",session["process_ids"]?.AsArray().Select(node=>(int?)node) ?? Enumerable.Empty<int?>())}) without terminating the target, so replacing it would destroy that process. Finish with the running host, or pass allow_terminate=true to accept losing the target.");
				var detach=new JsonObject { ["session_id"]=sessionId,["expected_lifecycle_version"]=(long?)session["lifecycle_version"] ?? 0,["allow_terminate"]=allowTerminate };
				var response=await router.CallAsync(new RpcRequest { Operation="detach",Arguments=detach,DeadlineUtc=DateTime.UtcNow.AddSeconds(20) },token);
				if(response.Error is not null)
					throw new GatewayControlException("replace_detach_failed",$"The running host could not detach session '{sessionId}' ({response.Error.Code}: {response.Error.Message}), so it was left running and nothing was replaced. Its target is still attached and would die with it.");
			}
		}
		foreach(var process in running) {
			try { if(!process.HasExited) { process.Kill(); process.WaitForExit(15000); } } catch(InvalidOperationException) { }
			catch(Exception ex) { throw new GatewayControlException("replace_failed",$"Could not close the running host (pid {process.Id}): {ex.Message}. Close it manually, then call launch_local_host again."); }
		}
		// The listener socket is not free the instant the process is, and starting into a taken port is
		// exactly the two-hosts state this method exists to avoid.
		for(var attempt=0;attempt<20 && running.Any(IsStillAlive);attempt++) await Task.Delay(250,token);
		await Task.Delay(500,token);
		// Both waits above are bounded, and neither was checked: WaitForExit's Boolean result was
		// discarded and the poll loop simply ran out of attempts. A host that outlived them returned as
		// success, and the caller then started a second dnSpy into the endpoint the first still holds --
		// the exact contention this method exists to prevent, reached by the path meant to prevent it.
		// Prove it instead, and fail without launching when the proof does not hold.
		RequireReplacedHostsExited(running.Select(process=>(Pid:SafePid(process),Alive:IsStillAlive(process))).ToArray());
	}

	/// <summary>Whether a process this method killed is still running. Failure to query <c>HasExited</c>
	/// is not proof that the process is gone: treating an access or OS failure as exit would let the
	/// replacement start beside a host that may still own the RPC endpoint. Fail closed instead.</summary>
	static bool IsStillAlive(Process process) => IsStillAlive(SafePid(process),()=>process.HasExited);
	internal static bool IsStillAlive(int processId,Func<bool> hasExited) {
		try { return !hasExited(); }
		catch(Exception ex) {
			throw new GatewayControlException("replace_failed",$"Could not confirm that the managed dnSpy host pid {processId} exited ({ex.GetType().Name}: {ex.Message}), so no replacement was started. Close it manually, then call launch_local_host again.");
		}
	}
	static int SafePid(Process process) { try { return process.Id; } catch { return 0; } }

	/// <summary>Refuses to report a replacement that did not happen. Separated from the process handling
	/// so the boundary that matters -- one surviving host among several dead ones -- is testable without
	/// an unkillable process.</summary>
	internal static void RequireReplacedHostsExited(IReadOnlyList<(int Pid,bool Alive)> hosts) {
		var alive=hosts.Where(host=>host.Alive).Select(host=>host.Pid).ToArray();
		if(alive.Length==0) return;
		throw new GatewayControlException("replace_failed",
			$"The managed dnSpy host{(alive.Length==1 ? "" : "s")} {string.Join(", ",alive.Select(pid=>"pid "+pid))} did not exit after being closed, so no replacement was started: launching one now would leave two hosts fighting for the same RPC endpoint. Close {(alive.Length==1 ? "it" : "them")} manually, then call launch_local_host again.");
	}
	/// <summary>Refuses a replacement the Gateway could not finish. Killing a process is a write, and the
	/// integrity policy that lets a medium-integrity Gateway read an elevated host's identity does not let
	/// it end the process. Only a proven elevated host blocks: an unreadable token leaves the existing
	/// behaviour alone, because guessing here would refuse ordinary replacements on a machine where the
	/// token query is unavailable.</summary>
	internal static void RequireReplacementPermitted(IReadOnlyList<(int Pid,bool? Elevated)> running,bool? gatewayElevated) {
		if(gatewayElevated!=false) return;
		var elevated=running.Where(host=>host.Elevated==true).Select(host=>host.Pid).ToArray();
		if(elevated.Length==0) return;
		throw new GatewayControlException("replace_requires_elevation",
			$"The running host{(elevated.Length==1 ? "" : "s")} {string.Join(", ",elevated.Select(pid=>"pid "+pid))} {(elevated.Length==1 ? "is" : "are")} elevated and this Gateway is not, so it cannot close {(elevated.Length==1 ? "it" : "them")}. Nothing was detached and nothing was started: stopping at the kill instead would have detached every session first and then failed anyway, losing the debugging state for no gain. Close the elevated dnSpy yourself and call launch_local_host again, or keep using it -- an elevated host serves an unelevated Gateway perfectly well.");
	}

	internal static void RequireReplacementSessionVisibility(int runningCount,JsonNode? live,bool allowTerminate) {
		if(allowTerminate) return;
		if(live is null)
			throw new GatewayControlException("replace_session_state_unknown","The running host is not answering RPC, so the Gateway cannot prove that replacing it is safe. It was left running. Recover the host and retry, or pass allow_terminate=true to accept that killing it may destroy an attached target.");
		if(runningCount!=1)
			throw new GatewayControlException("replace_session_state_unknown",$"There are {runningCount} managed dnSpy processes but only one RPC endpoint, so the Gateway cannot prove that every process is free of attached targets. They were left running. Close the extras safely, or pass allow_terminate=true to accept that killing them may destroy attached targets.");
	}

	/// <summary>dnSpy processes started from this managed install. Any one of them owns, or is about to
	/// fight for, the RPC endpoint. The user's own separate dnSpy lives elsewhere and is never touched.</summary>
	Process[] RunningManagedHosts() {
		var root=Path.Combine(installRoot,"versions")+Path.DirectorySeparatorChar;
		var found=new List<Process>();
		foreach(var process in Process.GetProcessesByName("dnSpy")) {
			var path=ProcessIdentity.ImagePath(process);
			if(path is not null && path.StartsWith(root,StringComparison.OrdinalIgnoreCase)) found.Add(process); else process.Dispose();
		}
		return found.ToArray();
	}
	/// <summary>The registered host that is one of these processes. Identified by process id rather than
	/// by position in the list: with a remote host also registered, taking the first connected entry
	/// would compare a machine across the network against the payload installed on this one.</summary>
	async Task<JsonNode?> ConnectedLocalHostAsync(HostRouter router,CancellationToken token,int[] processIds) {
		foreach(var item in await router.ListHostsAsync(token)) {
			var node=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(item))!;
			if((string?)node["state"] is not ("connected" or "degraded") || node["host"] is not JsonNode host) continue;
			if((int?)host["dnspy_process_id"] is int id && processIds.Contains(id)) return host;
		}
		return null;
	}
	async Task<JsonObject[]> LocalSessionsAsync(HostRouter router,bool allowTerminate,CancellationToken token) {
		var response=await router.CallAsync(new RpcRequest { Operation="list_sessions",DeadlineUtc=DateTime.UtcNow.AddSeconds(10) },token);
		return ReadLocalSessions(response,allowTerminate);
	}
	internal static JsonObject[] ReadLocalSessions(RpcResponse response,bool allowTerminate) {
		if(response.Error is not null || response.Result is null) {
			if(allowTerminate) return Array.Empty<JsonObject>();
			var detail=response.Error is null ? "the host returned no result" : $"{response.Error.Code}: {response.Error.Message}";
			throw new GatewayControlException("replace_session_state_unknown",$"The Gateway could not list the running host's sessions ({detail}), so it cannot prove that replacing it is safe. The host was left running. Retry after recovery, or pass allow_terminate=true to accept that killing it may destroy an attached target.");
		}
		var node=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(response.Result));
		var array=node as JsonArray ?? node?["result"] as JsonArray;
		if(array is not null && array.All(item=>item is JsonObject)) return array.OfType<JsonObject>().ToArray();
		if(allowTerminate) return Array.Empty<JsonObject>();
		throw new GatewayControlException("replace_session_state_unknown","The running host returned a malformed session list, so the Gateway cannot prove that replacing it is safe. The host was left running. Retry after recovery, or pass allow_terminate=true to accept that killing it may destroy an attached target.");
	}
	/// <summary>The host tree a running process actually loaded its extension from, derived from the
	/// extension_path it reports. dnSpy loads the extension from
	/// <c>&lt;root&gt;\bin\Extensions\dgSpy\dgSpy.Extension.x.dll</c>, so the root is four levels up -- and a
	/// path with any other shape is not a tree this Gateway can reason about. Unknown returns null, which
	/// every caller must treat as "no match": adopting a host whose tree cannot be identified is exactly the
	/// outcome the whole comparison exists to prevent.</summary>
	internal static string? RunningHostRoot(string? extensionPath) {
		if(string.IsNullOrWhiteSpace(extensionPath)) return null;
		string full; try { full=Path.GetFullPath(extensionPath!); } catch { return null; }
		var root=full;
		for(var level=0;level<4;level++) { root=Path.GetDirectoryName(root)!; if(string.IsNullOrEmpty(root)) return null; }
		var expected=Path.Combine("bin","Extensions","dgSpy","dgSpy.Extension.x.dll");
		string relative; try { relative=Path.GetRelativePath(root,full); } catch { return null; }
		return string.Equals(relative,expected,StringComparison.OrdinalIgnoreCase) ? Path.TrimEndingDirectorySeparator(root) : null;
	}

	/// <summary>Whether the tree a host is running is the payload this deployment would install, and whether
	/// the process is actually running the code that is in that tree now. Both halves are needed, and the two
	/// earlier answers each had only one of them.
	///
	/// The extension digest alone answered this while the extension assembly was the only thing that moved
	/// per build; with the HookLab payload in the tree it is not, and a payload-only change was adopted as if
	/// nothing had changed. Replacing it with a tree comparison then lost the other half: both of those paths
	/// answer *where the bytes came from*, not *what the bytes are now*. Path equality adopted on the path;
	/// the manifest branch compared a recorded provenance field written at deployment time.
	///
	/// So there are two gates, in cost order.
	///
	/// **The loaded-extension digest, always.** The host reports the SHA-256 of the assembly this process
	/// loaded; we hash the assembly that is at that path now. A tree rebuilt or edited in place after dnSpy
	/// loaded its extension fails here -- the process holds the old extension while the directory supplies a
	/// newer HookLab payload, which is exactly the cross-generation host/payload pairing the whole comparison
	/// exists to prevent. This is one file hash, not a tree read, and it is the *only* content check available
	/// for the payload-root case: there the deployment and the payload are one directory, so hashing the tree
	/// would compare it with itself and always agree. A digest the host could not compute (it reports
	/// "unknown") means no match, per the rule that unknown is never adoption.
	///
	/// **Then, for a deployed tree, the bytes.** The recorded <c>payload_sha256</c> is kept as a cheap
	/// pre-filter -- a deployment that never claimed to be this payload is refused without reading a quarter
	/// of a gigabyte -- but a claim is not proof, so when it does claim to match, the deployed tree is hashed
	/// and compared with the installed payload's hash. That is the cost decision: the tree read is paid only
	/// on the path that is about to answer "yes", it is metadata-stamp cached like the payload hash, and the
	/// alternative is adopting a version directory that was altered after deployment without its manifest
	/// being touched. The deployment manifest is excluded from that hash because it is written into the tree
	/// after the copy and therefore is not part of what was copied. This relies on the deployed tree being
	/// immutable while it runs: dnSpy writes its settings to %APPDATA%\dnSpy unless a dnSpy.xml already sits
	/// beside the binaries, and no packaged tree ships one. If that ever changes, this fails closed -- a
	/// refusal to adopt, not a false adoption.
	///
	/// Known residual: a change confined to the HookLab payload inside a *developer worktree* root, with the
	/// extension assembly untouched, still adopts. There is nothing left to compare it against -- the running
	/// tree is the installed payload -- and closing it needs the host to report the payload digest it resolved,
	/// which it does not yet.</summary>
	internal static bool RunningTreeMatchesPayload(string? runningRoot,string? runningExtensionSha,string payloadRoot,Func<string> installedTreeSha,Func<string,string?> recordedPayloadSha,Func<string,string?> extensionSha,Func<string,string?> deployedTreeSha) {
		if(string.IsNullOrEmpty(runningRoot)) return false;
		if(string.IsNullOrWhiteSpace(runningExtensionSha) || string.Equals(runningExtensionSha,"unknown",StringComparison.OrdinalIgnoreCase)) return false;
		string? loaded; try { loaded=extensionSha(runningRoot!); } catch { return false; }
		if(string.IsNullOrEmpty(loaded) || !string.Equals(loaded,runningExtensionSha,StringComparison.OrdinalIgnoreCase)) return false;
		if(PathsEqual(runningRoot!,payloadRoot)) return true;
		var recorded=recordedPayloadSha(runningRoot!);
		if(string.IsNullOrEmpty(recorded)) return false;
		string installed; try { installed=installedTreeSha(); } catch { return false; }
		if(string.IsNullOrEmpty(installed) || !string.Equals(recorded,installed,StringComparison.OrdinalIgnoreCase)) return false;
		string? actual; try { actual=deployedTreeSha(runningRoot!); } catch { return false; }
		return !string.IsNullOrEmpty(actual) && string.Equals(actual,installed,StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Hash of the extension assembly under a payload or deployment root, which is what a
	/// running host reports as extension_sha256. It no longer decides adoption on its own -- a payload-only
	/// change moved past it -- but it is half of the decision again, as the gate that ties the running process
	/// to the bytes at that path now rather than to the bytes it was deployed from. See
	/// <see cref="RunningTreeMatchesPayload"/>.</summary>
	internal static string? ExtensionSha(string root) {
		var path=Path.Combine(root,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll");
		try { return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null; } catch { return null; }
	}
	static string Shorten(string? sha) => string.IsNullOrEmpty(sha) ? "unknown" : sha!.Substring(0,Math.Min(12,sha!.Length));
	object Rollback(JsonObject args) { RequireConfirm(args); var current=ReadCurrent() ?? throw new GatewayControlException("local_not_installed","No managed local deployment exists."); var previous=(string?)current["previous_version"] ?? throw new GatewayControlException("rollback_unavailable","No previous local version is retained."); var active=(string)current["active_version"]!; if(!Directory.Exists(Path.Combine(installRoot,"versions",previous))) throw new GatewayControlException("rollback_unavailable","The retained previous version directory is missing."); current["active_version"]=previous; current["previous_version"]=active; current["updated_utc"]=DateTime.UtcNow; WriteCurrent(current); WriteCurrentLauncher(); return new { rolled_back=true,active_version=previous,previous_version=active }; }
	object Uninstall(JsonObject args) { RequireConfirm(args); if(Directory.Exists(installRoot)) Directory.Delete(installRoot,true); foreach(var shortcut in new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"dgSpy.cmd"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","dgSpy.cmd")}) if(File.Exists(shortcut)&&File.ReadAllText(shortcut).Contains(installRoot,StringComparison.OrdinalIgnoreCase)) File.Delete(shortcut); if((bool?)args["remove_settings"]==true && Directory.Exists(stateRoot)) Directory.Delete(stateRoot,true); return new { uninstalled=true,settings_preserved=(bool?)args["remove_settings"]!=true }; }

	async Task<object> CreateRemoteAsync(JsonObject args,HostRouter router,CancellationToken token) {
		var hostId=SafeSegment((string?)args["host_id"] ?? throw new GatewayControlException("invalid_arguments","host_id is required."));
		var address=((string?)args["gateway_address"] ?? throw new GatewayControlException("invalid_arguments","gateway_address is required.")).Trim();
		if(string.IsNullOrWhiteSpace(address)) throw new GatewayControlException("invalid_arguments","gateway_address is required.");
		var compression=((string?)args["compression"] ?? "optimal").ToLowerInvariant();
		var compressionLevel=compression switch { "none"=>CompressionLevel.NoCompression,"fastest"=>CompressionLevel.Fastest,"optimal"=>CompressionLevel.Optimal,_=>throw new GatewayControlException("invalid_arguments","compression must be none, fastest, or optimal.") };
		var useTls=(bool?)args["use_tls"] ?? true; var output=Path.GetFullPath((string?)args["output_root"] ?? packageRoot);
		if(output!=packageRoot&&!output.StartsWith(packageRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new GatewayControlException("path_outside_package_root",$"output_root must stay below '{packageRoot}'.");
		var payload=RemotePayloadRoot();
		try { ValidateRemotePayload(payload); }
		catch(GatewayControlException) { throw; }
		catch(Exception ex) { throw new GatewayControlException("installation_incomplete",$"The installed remote-host payload is unusable: {ex.Message}. Reinstall dgSpy from a complete release package."); }
		var registry=Path.Combine(packageRoot,"gateway-hosts.json"); var replacing=RegistryContainsHost(registry,hostId);
		Directory.CreateDirectory(output); Directory.CreateDirectory(packageRoot);
		var bundleName=$"dgSpy-remote-host-{hostId}-win-x64";
		var archive=Path.Combine(output,bundleName+".zip"); var temporaryArchive=archive+".tmp-"+Guid.NewGuid().ToString("N");
		try {
			// Every file that differs from the installed payload, keyed by its archive entry name. The
			// package used to be built by copying the whole ~700MB self-contained tree into a staging
			// directory, personalizing a handful of files there, and zipping that: three full passes over
			// the tree to change nine files. The overlay is written straight into the archive instead, so
			// the payload is read once and never copied.
			var personalized=new SortedDictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
			var credential=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); personalized["state/host.id"]=Utf8(hostId); personalized["state/rpc.token"]=Utf8(credential);
			var remote=new JsonObject { ["format_version"]=1,["host_id"]=hostId,["gateway_address"]=address,["gateway_port"]=useTls?7353:7352,["transport"]=useTls?"tls":"plaintext" };
			var gatewayHost=new JsonObject { ["host_id"]=hostId,["display_name"]=hostId,["transport"]=useTls?"outbound_tls":"outbound",["token_file"]=hostId+".token" };
			byte[]? serverPfx=null,serverCer=null,clientCer=null; string? serverPassword=null,clientPassword=null;
			if(useTls) {
				var serverPfxPath=Path.Combine(packageRoot,"gateway-server.pfx"); var serverCerPath=Path.Combine(packageRoot,"gateway-server.cer"); var serverPasswordPath=Path.Combine(packageRoot,"gateway-server.password");
				if(File.Exists(serverPfxPath)&&File.Exists(serverCerPath)&&File.Exists(serverPasswordPath)) { serverPfx=File.ReadAllBytes(serverPfxPath); serverCer=File.ReadAllBytes(serverCerPath); serverPassword=File.ReadAllText(serverPasswordPath).Trim(); }
				else { serverPassword=NewSecret(); (serverPfx,serverCer)=CreateCertificate(address,true,serverPassword); }
				clientPassword=NewSecret(); var client=CreateCertificate(hostId,false,clientPassword); clientCer=client.Cer;
				personalized["certificates/client.pfx"]=client.Pfx; personalized["certificates/client.password"]=Utf8(clientPassword); personalized["certificates/gateway-server.cer"]=serverCer!;
				remote["client_certificate_file"]="certificates/client.pfx"; remote["client_certificate_password_file"]="certificates/client.password"; remote["gateway_certificate_file"]="certificates/gateway-server.cer"; gatewayHost["client_certificate_file"]=hostId+"-client.cer";
			}
			personalized["remote-host.json"]=Utf8(remote.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true })+"\n");
			personalized["manifest.json"]=Utf8(RemoteManifest(payload,personalized,bundleName,token));
			WriteRemoteArchive(temporaryArchive,payload,personalized,compressionLevel,token);
			File.WriteAllText(Path.Combine(packageRoot,hostId+".token"),credential,new UTF8Encoding(false));
			if(useTls) { File.WriteAllBytes(Path.Combine(packageRoot,"gateway-server.pfx"),serverPfx!); File.WriteAllBytes(Path.Combine(packageRoot,"gateway-server.cer"),serverCer!); File.WriteAllText(Path.Combine(packageRoot,"gateway-server.password"),serverPassword!,new UTF8Encoding(false)); File.WriteAllBytes(Path.Combine(packageRoot,hostId+"-client.cer"),clientCer!); }
			UpdateRemoteRegistry(registry,hostId,address,gatewayHost,useTls); File.Move(temporaryArchive,archive,true);
			var updated=HostRegistry.Load(registry,includeLocal:true); var listener=remoteListener ?? throw new GatewayControlException("listener_unavailable","The running Gateway does not expose its remote listener lifecycle service.");
			var listenerReadiness=listener.EnsureConfigured(registry); router.Reload(updated);
			await Task.CompletedTask; return new { host_id=hostId,archive_path=archive,sha256=HashFile(archive),compression,transport=useTls?"mutual_tls":"authenticated_plaintext",launch_command=@".\dnSpy.exe",launcher_alternative=@".\launcher\Start-dgSpyRemoteHost.cmd",replaced_existing_host=replacing,transferred=false,executed=false,gateway_restart_required=false,gateway_ready=true,listener=listenerReadiness };
		} finally { if(File.Exists(temporaryArchive)) File.Delete(temporaryArchive); }
	}
	async Task<object> RemoteReadinessAsync(string? hostId,HostRouter router,CancellationToken token) { if(string.IsNullOrWhiteSpace(hostId)) throw new GatewayControlException("invalid_arguments","host_id is required."); var hosts=await router.ListHostsAsync(token); var selected=hosts.Select(item=>System.Text.Json.JsonSerializer.Serialize(item)).FirstOrDefault(json=>json.Contains($"\"host_id\":\"{hostId}\"",StringComparison.Ordinal)); return new { host_id=hostId,registered=selected is not null,connected=selected?.Contains("\"state\":\"connected\"",StringComparison.Ordinal)==true,hosts }; }
	object RevokeRemote(JsonObject args,HostRouter router) { RequireConfirm(args); var hostId=(string?)args["host_id"] ?? throw new GatewayControlException("invalid_arguments","host_id is required."); var registry=Environment.GetEnvironmentVariable("DGSPY_HOSTS_FILE") ?? Path.Combine(packageRoot,"gateway-hosts.json"); if(!File.Exists(registry)) return new { host_id=hostId,revoked=false,already_absent=true }; var root=JsonNode.Parse(File.ReadAllText(registry))!.AsObject(); var hosts=root["hosts"]!.AsArray(); var removed=hosts.Where(node=>(string?)node?["host_id"]==hostId).ToArray(); foreach(var node in removed) hosts.Remove(node); AtomicWrite(registry,root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); if(removed.Length>0) { var updated=HostRegistry.Load(registry,includeLocal:true); var listener=remoteListener ?? throw new GatewayControlException("listener_unavailable","The running Gateway does not expose its remote listener lifecycle service."); listener.EnsureConfigured(registry); router.Reload(updated); } return new { host_id=hostId,revoked=removed.Length>0,already_absent=removed.Length==0,credentials_retained=true,gateway_restart_required=false,recovery=removed.Length>0?"The live connection was closed and the old credential is no longer accepted. Delete retained credential files after confirming no rollback is required.":null }; }

	// The deployment identity must be derived from everything that can change, because anything it leaves
	// out is a code change the gateway will deploy over silently. This once hashed dnSpy.exe alone — an
	// apphost stub generated from the project name, byte-identical across every rebuild — so a rebuilt
	// dnSpy.dll and dgSpy.Extension.x.dll produced the same fingerprint, the active==version check below
	// short-circuited, and the gateway kept running a tree that was days old while reporting success.
	internal bool EnsureBundledLocalHost(CancellationToken token) {
		var payload=RemotePayloadRoot(); ValidateRemotePayload(payload); var hostId=DefaultHostId();
		var payloadSha=HashTreeCached(payload);
		var version="bundled-"+payloadSha.Substring(0,12).ToLowerInvariant();
		var current=ReadCurrent(); var active=(string?)current?["active_version"];
		// Trusting the name alone reuses a directory whose contents were never checked. The recorded hash
		// is what makes "already installed" a claim about content instead of about a string.
		if(active==version && string.Equals(DeployedPayloadSha(version),payloadSha,StringComparison.OrdinalIgnoreCase)) { ValidateDnSpy(Path.Combine(installRoot,"versions",version)); return false; }
		var destination=Path.Combine(installRoot,"versions",version); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		// A directory whose recorded hash disagrees with its name is a partial or interrupted copy. Never
		// overwrite it in place: dnSpy may be running out of it. Deploy beside it instead.
		if(Directory.Exists(destination) && !string.Equals(DeployedPayloadSha(version),payloadSha,StringComparison.OrdinalIgnoreCase)) {
			var unique=2; while(Directory.Exists(destination+"-"+unique)) unique++;
			version=version+"-"+unique; destination=Path.Combine(installRoot,"versions",version);
		}
		if(!Directory.Exists(destination)) { var staging=destination+".staging-"+Guid.NewGuid().ToString("N"); try { CopyTree(payload,staging,token); ValidateDnSpy(staging); File.WriteAllText(Path.Combine(staging,DeploymentManifestName),System.Text.Json.JsonSerializer.Serialize(new { version,host_id=hostId,created_utc=DateTime.UtcNow,source="bundled_payload",payload_sha256=payloadSha,packaged=ReadStagedManifest(),sha256=HashTree(staging) })); Directory.Move(staging,destination); } catch { if(Directory.Exists(staging)) Directory.Delete(staging,true); throw; } }
		Directory.CreateDirectory(stateRoot); EnsureSecret(Path.Combine(stateRoot,"gateway.token")); EnsureSecret(Path.Combine(stateRoot,"rpc.token")); File.WriteAllText(Path.Combine(stateRoot,"host.id"),hostId);
		WriteCurrent(new JsonObject { ["active_version"]=version,["previous_version"]=active,["host_id"]=hostId,["updated_utc"]=DateTime.UtcNow }); WriteCurrentLauncher(); return true;
	}

	/// <summary>The payload hash a deployed version recorded for itself, or null when it predates the
	/// field or was never fully written.</summary>
	string? DeployedPayloadSha(string version) => DeployedTreePayloadSha(Path.Combine(installRoot,"versions",version));
	/// <summary>The same record read from a deployment root rather than a version name, because the tree a
	/// host is running is known by its path and the active-version pointer may name a different one.</summary>
	internal static string? DeployedTreePayloadSha(string root) {
		var manifest=Path.Combine(root,DeploymentManifestName);
		if(!File.Exists(manifest)) return null;
		try { return (string?)JsonNode.Parse(File.ReadAllText(manifest))?["payload_sha256"]; } catch { return null; }
	}
	/// <summary>The packaging manifest that ships beside the staged payload, carrying the commit it was
	/// built from. Recorded verbatim in the deployment so a deployed tree can name its own provenance.</summary>
	JsonNode? ReadStagedManifest() {
		var manifest=Path.Combine(Path.GetDirectoryName(RemotePayloadRoot())!,"manifest.json");
		if(!File.Exists(manifest)) return null;
		try { return JsonNode.Parse(File.ReadAllText(manifest)); } catch { return null; }
	}

	/// <summary>Compares the payload that is installed against the one that is deployed and running. This
	/// is the check that was missing: every stage reported success about its own step, and nothing ever
	/// compared one stage's output with the next stage's input.</summary>
	object DeploymentFreshness() {
		var current=ReadCurrent(); var active=(string?)current?["active_version"];
		if(active is null) return new { known=true,stale=false,detail="No managed local deployment yet." };
		string payloadSha;
		try { var payload=RemotePayloadRoot(); ValidateRemotePayload(payload); payloadSha=HashTreeCached(payload); }
		catch(Exception ex) { return new { known=false,stale=false,detail=$"Installed payload could not be hashed: {ex.Message}" }; }
		var deployed=DeployedPayloadSha(active);
		if(deployed is null) return new { known=false,stale=true,staged_payload_sha256=payloadSha,active_version=active,detail="The active deployment predates payload verification and cannot prove what it contains.",recovery="Call launch_local_host to redeploy the installed payload; add replace=true if a host from that deployment is still running." };
		var stale=!string.Equals(deployed,payloadSha,StringComparison.OrdinalIgnoreCase);
		return new { known=true,stale,staged_payload_sha256=payloadSha,active_payload_sha256=deployed,active_version=active,
			detail=stale?"The installed payload is newer than the running deployment; dnSpy is executing older code.":"The active deployment matches the installed payload.",
			recovery=stale?"Call launch_local_host with replace=true; it detaches the running host's targets, closes it, and launches the installed payload. Any debugging session it holds ends, so finish or hand off that session first.":null };
	}
	string RemotePayloadRoot() {
		var configured=Environment.GetEnvironmentVariable("DGSPY_REMOTE_PAYLOAD_ROOT"); if(!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
		var shared=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")); if(File.Exists(Path.Combine(shared,"dnSpy.exe"))) return shared;
		return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"remote-host-payload","win-x64"));
	}
	/// <summary>The files without which an install is not usable. The HookLab payload belongs here for the
	/// same reason the extension does: it is deployed with the tree, every readiness path runs through this
	/// check, and leaving it out meant an install missing it passed doctor, launch_local_host,
	/// EnsureBundledLocalHost, DeploymentFreshness and CreateRemoteAsync and failed only when a payload
	/// action ran -- at which point the failure is attributed to the action rather than to the install.
	/// Both files are required: the bootstrap without its manifest cannot have its digest verified, and the
	/// verifier refuses to hand on bytes it cannot check.</summary>
	static void ValidateRemotePayload(string payload) {
		var required=new[]{"dnSpy.exe",Path.Combine("bin","dnSpy.dll"),Path.Combine("bin","dnSpy.Contracts.DnSpy.dll"),Path.Combine("bin","hostfxr.dll"),Path.Combine("bin","hostpolicy.dll"),Path.Combine("bin","coreclr.dll"),Path.Combine("bin","clrjit.dll"),Path.Combine("bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),Path.Combine("bin","Extensions","dgSpy","dgSpy.Protocol.dll"),Path.Combine("bin","Extensions","dgSpy","HookLab.Contracts.dll"),Path.Combine("bin","Extensions","dgSpy","HookLab.Host.Transport.dll"),HookLabPayloadRelativePath,HookLabManifestRelativePath,Path.Combine("launcher","Start-dgSpyRemoteHost.ps1"),Path.Combine("launcher","Start-dgSpyRemoteHost.cmd")};
		var missing=required.Where(path=>!File.Exists(Path.Combine(payload,path))).ToArray(); if(missing.Length>0) throw new GatewayControlException("installation_incomplete",$"The installed remote-host payload is incomplete ({string.Join(", ",missing)}). Reinstall dgSpy from a complete release package; runtime builds are not supported.");
		var rootProtocol=Path.Combine(payload,"bin","dgSpy.Protocol.dll"); var extensionProtocol=Path.Combine(payload,"bin","Extensions","dgSpy","dgSpy.Protocol.dll");
		if(File.Exists(rootProtocol) && !File.ReadAllBytes(rootProtocol).SequenceEqual(File.ReadAllBytes(extensionProtocol))) throw new GatewayControlException("installation_incomplete","The app-base and extension dgSpy.Protocol.dll files differ; dnSpy would silently load the stale app-base contract. Reinstall from one complete package.");
		VerifyPackagedFile(payload,Path.Combine("bin","Extensions","dgSpy","dgSpy.Extension.x.dll"),"extension_sha256");
		VerifyPackagedFile(payload,File.Exists(rootProtocol)?Path.Combine("bin","dgSpy.Protocol.dll"):Path.Combine("bin","Extensions","dgSpy","dgSpy.Protocol.dll"),"protocol_sha256");
		var packaged=PackagedHookLabRecord(payload); VerifyHookLabPayload(payload,packaged.Sha,packaged.Readable);
	}
	static void VerifyPackagedFile(string payloadRoot,string relativePath,string manifestField) {
		var record=PackagedDigestRecord(payloadRoot,manifestField);
		if(!record.Readable) throw new GatewayControlException("installation_incomplete","The package manifest beside the payload root could not be read, so packaged assembly digests cannot be checked.");
		if(string.IsNullOrWhiteSpace(record.Sha)) return;
		var actual=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(payloadRoot,relativePath)))).ToLowerInvariant();
		if(!string.Equals(actual,record.Sha,StringComparison.OrdinalIgnoreCase)) throw new GatewayControlException("installation_incomplete",$"The staged {relativePath} digest is {actual}, but the package manifest records {record.Sha}.");
	}
	static (bool Readable,string? Sha) PackagedDigestRecord(string payloadRoot,string field) {
		var parent=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot))); if(string.IsNullOrEmpty(parent)) return (true,null);
		var manifest=Path.Combine(parent!,"manifest.json"); if(!File.Exists(manifest)) return (true,null);
		try { return (true,(string?)JsonNode.Parse(File.ReadAllText(manifest))?[field]); } catch { return (false,null); }
	}

	internal static readonly string HookLabPayloadRelativePath=Path.Combine("hooklab","hooklab-bootstrap.net48.payload");
	internal static readonly string HookLabManifestRelativePath=Path.Combine("hooklab","hooklab-payload-manifest.json");
	const string HookLabPayloadFileName="hooklab-bootstrap.net48.payload";
	const string HookLabPayloadEntryId="hooklab_bootstrap";

	/// <summary>The independent digest record the packaging step writes into the package manifest beside the
	/// payload root. Absent is not a failure; disagreeing is, because rewriting one record must not be
	/// enough. <b>Unreadable is also a failure</b>, and the distinction is the whole point of returning a
	/// readability flag rather than just the value: collapsing "cannot read it" into "there isn't one"
	/// fails open, so a malformed manifest would silently disable the only cross-check that catches a
	/// payload and its own manifest replaced together.
	///
	/// A manifest that parses but carries no digest is still allowed - that is a package predating the
	/// field, not a damaged one.</summary>
	static (bool Readable,string? Sha) PackagedHookLabRecord(string payloadRoot) => PackagedDigestRecord(payloadRoot,"hooklab_payload_sha256");

	/// <summary>Verifies the staged HookLab payload the way a consumer must, and at the moment readiness is
	/// reported rather than at the moment an action needs it.
	///
	/// Checking that the two filenames exist was the whole of this test before, so an empty payload, a
	/// truncated one, a malformed manifest and a digest mismatch all stayed healthy through doctor, deployment
	/// and remote-package creation and produced their first symptom inside a payload action -- where the
	/// failure reads as the action's fault rather than the install's. That is precisely the late failure the
	/// required-files entry was added to eliminate, surviving one layer down.
	///
	/// This mirrors the DgSpyTool layout verifier: parse the manifest,
	/// take the single hooklab_bootstrap entry, compare size, recompute SHA-256 over the bytes, and compare
	/// against the package manifest's independent record when there is one. It deliberately does not repeat
	/// that script's reachable-copy scan, which walks every file of a self-contained publish and belongs to
	/// packaging and install rather than to a per-call readiness check.
	///
	/// Cost: one read of a payload measured in hundreds of kilobytes, against the quarter-gigabyte tree hash
	/// these same paths already pay (and cache). Not cached here on purpose -- a stale "the payload is fine"
	/// is the answer that has no value.</summary>
	internal static void VerifyHookLabPayload(string payloadRoot,string? packagedSha,bool packageRecordReadable=true) {
		// A package manifest that exists and cannot be read is not the same as one that is not there, and
		// treating it as absent would quietly turn the independent cross-check off for exactly the install
		// most likely to be damaged.
		if(!packageRecordReadable) throw Corrupt("the package manifest beside the payload root exists but could not be read, so the independent digest record cannot be checked");
		var payloadFile=Path.Combine(payloadRoot,HookLabPayloadRelativePath);
		var manifestFile=Path.Combine(payloadRoot,HookLabManifestRelativePath);
		JsonNode? manifest;
		try { manifest=JsonNode.Parse(File.ReadAllText(manifestFile)); }
		catch(Exception ex) { throw Corrupt($"its manifest '{manifestFile}' could not be read ({ex.GetType().Name}: {ex.Message})"); }
		var entries=(manifest?["payloads"] as JsonArray)?.OfType<JsonObject>().Where(entry=>(string?)entry["id"]==HookLabPayloadEntryId).ToArray() ?? Array.Empty<JsonObject>();
		if(entries.Length!=1) throw Corrupt($"its manifest '{manifestFile}' does not carry exactly one '{HookLabPayloadEntryId}' entry ({entries.Length} found)");
		var entry=entries[0];
		var named=(string?)entry["file"];
		if(!string.Equals(named,HookLabPayloadFileName,StringComparison.OrdinalIgnoreCase)) throw Corrupt($"its manifest names the file '{named ?? "(absent)"}' while this layout stages '{HookLabPayloadFileName}'");
		long? recordedSize; try { recordedSize=(long?)entry["size"]; } catch { recordedSize=null; }
		var recordedSha=(string?)entry["sha256"];
		if(recordedSize is null || string.IsNullOrWhiteSpace(recordedSha)) throw Corrupt($"its manifest entry records no usable size and digest");
		byte[] bytes;
		try { bytes=File.ReadAllBytes(payloadFile); }
		catch(Exception ex) { throw Corrupt($"'{payloadFile}' could not be read ({ex.GetType().Name}: {ex.Message})"); }
		if(bytes.LongLength!=recordedSize) throw Corrupt($"'{payloadFile}' is {bytes.LongLength} bytes and its manifest records {recordedSize}, so it is truncated, empty or was modified");
		var actual=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		if(!string.Equals(actual,recordedSha!.Trim(),StringComparison.OrdinalIgnoreCase)) throw Corrupt($"'{payloadFile}' hashes to {actual} and its manifest records {recordedSha}, so the bytes are not the ones that were packaged");
		// The second, independent record. A payload and its own manifest rewritten together agree with each
		// other and disagree with this, which is the only reason it is worth reading.
		if(!string.IsNullOrWhiteSpace(packagedSha) && !string.Equals(actual,packagedSha!.Trim(),StringComparison.OrdinalIgnoreCase))
			throw Corrupt($"'{payloadFile}' hashes to {actual} while the package manifest records {packagedSha}, so the payload and its own manifest were replaced together or come from another package");
		static GatewayControlException Corrupt(string detail) => new GatewayControlException("installation_incomplete",$"The installed HookLab payload cannot be verified: {detail}. Reinstall dgSpy from a complete release package; runtime builds are not supported.");
	}
	static bool RegistryContainsHost(string registry,string hostId) { if(!File.Exists(registry)) return false; var hosts=JsonNode.Parse(File.ReadAllText(registry))?["hosts"]?.AsArray(); return hosts?.Any(node=>(string?)node?["host_id"]==hostId)==true; }
	static void UpdateRemoteRegistry(string registry,string hostId,string gatewayAddress,JsonObject gatewayHost,bool useTls) {
		var root=File.Exists(registry)?JsonNode.Parse(File.ReadAllText(registry))!.AsObject():new JsonObject { ["hosts"]=new JsonArray() }; var hosts=root["hosts"]?.AsArray() ?? new JsonArray(); root["hosts"]=hosts;
		foreach(var existing in hosts.Where(node=>(string?)node?["host_id"]==hostId).ToArray()) hosts.Remove(existing);
		var listener=root["listener"]?.AsObject(); if(hosts.Count>0 && listener is not null && !string.Equals((string?)listener["address"],gatewayAddress,StringComparison.OrdinalIgnoreCase)) throw new GatewayControlException("listener_address_conflict",$"The Gateway already provisions remote hosts through '{(string?)listener["address"]}', not '{gatewayAddress}'. Reuse the existing Gateway address or revoke every other remote host first.");
		hosts.Add(gatewayHost);
		listener ??= new JsonObject { ["address"]=gatewayAddress }; listener["address"]=gatewayAddress; if(!useTls) listener["plaintext_port"]=7352; root["listener"]=listener;
		if(useTls) root["tls"]=new JsonObject { ["server_certificate_file"]="gateway-server.pfx",["server_certificate_password_file"]="gateway-server.password",["port"]=7353 };
		AtomicWrite(registry,root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
	}
	static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
	static (byte[] Pfx,byte[] Cer) CreateCertificate(string name,bool server,string password) {
		using var key=RSA.Create(3072); var request=new CertificateRequest($"CN=dgSpy {(server?"Gateway":"host "+name)}",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
		var eku=new OidCollection { new Oid(server?"1.3.6.1.5.5.7.3.1":"1.3.6.1.5.5.7.3.2") }; request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
		if(server) { var san=new SubjectAlternativeNameBuilder(); if(IPAddress.TryParse(name,out var address)) san.AddIpAddress(address); else san.AddDnsName(name); request.CertificateExtensions.Add(san.Build()); }
		using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5)); return (certificate.Export(X509ContentType.Pfx,password),certificate.Export(X509ContentType.Cert));
	}
	static byte[] Utf8(string value) => new UTF8Encoding(false).GetBytes(value);

	/// <summary>Hashes a file without materializing it. The archive is several hundred megabytes, and
	/// reading it into one <c>byte[]</c> to hash it bought a large-object allocation and a second full
	/// read for nothing.</summary>
	static string HashFile(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

	/// <summary>The package manifest, computed over the installed payload plus the personalized overlay
	/// without staging either. <c>state</c> is excluded because it holds the host credential, and a
	/// manifest cannot describe itself. Entries are ordered by their path with the platform separator, so
	/// the ordering is the one the staging-tree implementation produced.</summary>
	static string RemoteManifest(string payload,IReadOnlyDictionary<string,byte[]> personalized,string bundleName,CancellationToken token) {
		var entries=new List<(string Sort,string Path,long Size,string Sha)>();
		foreach(var path in Directory.EnumerateFiles(payload,"*",SearchOption.AllDirectories)) {
			token.ThrowIfCancellationRequested();
			var relative=Path.GetRelativePath(payload,path);
			// An overlaid file is described by the bytes that reach the archive, never by the payload copy
			// it replaces.
			if(ExcludedFromRemoteManifest(relative)||personalized.ContainsKey(relative.Replace('\\','/'))) continue;
			using var stream=File.OpenRead(path);
			entries.Add((relative,relative.Replace('\\','/'),stream.Length,Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
		}
		foreach(var pair in personalized) {
			if(ExcludedFromRemoteManifest(pair.Key)) continue;
			entries.Add((pair.Key.Replace('/',Path.DirectorySeparatorChar),pair.Key,pair.Value.LongLength,Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()));
		}
		var files=new JsonArray();
		foreach(var entry in entries.OrderBy(value=>value.Sort,StringComparer.OrdinalIgnoreCase)) files.Add(new JsonObject { ["path"]=entry.Path,["size"]=entry.Size,["sha256"]=entry.Sha });
		var manifest=new JsonObject { ["format_version"]=1,["bundle"]=bundleName,["target_framework"]="net10.0-windows",["runtime_identifier"]="win-x64",["self_contained"]=true,["files"]=files };
		return manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true })+"\n";
	}
	static bool ExcludedFromRemoteManifest(string relative) {
		var normalized=relative.Replace('\\','/');
		return normalized.StartsWith("state/",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(normalized).Equals("manifest.json",StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Writes the package straight from the installed payload with the personalized files
	/// overlaid. Streamed into the archive as it is built, so neither the tree nor any single entry is
	/// held in memory, and the payload is never copied to disk first.</summary>
	static void WriteRemoteArchive(string archivePath,string payload,IReadOnlyDictionary<string,byte[]> personalized,CompressionLevel level,CancellationToken token) {
		using var file=new FileStream(archivePath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
		using var zip=new ZipArchive(file,ZipArchiveMode.Create);
		foreach(var path in Directory.EnumerateFiles(payload,"*",SearchOption.AllDirectories)) {
			token.ThrowIfCancellationRequested();
			var name=Path.GetRelativePath(payload,path).Replace('\\','/');
			if(personalized.ContainsKey(name)) continue;
			zip.CreateEntryFromFile(path,name,level);
		}
		foreach(var pair in personalized) {
			token.ThrowIfCancellationRequested();
			using var stream=zip.CreateEntry(pair.Key,level).Open();
			stream.Write(pair.Value,0,pair.Value.Length);
		}
	}

	JsonObject? ReadCurrent() { var path=Path.Combine(installRoot,"current.json"); return File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():null; }
	void WriteCurrent(JsonObject current) { Directory.CreateDirectory(installRoot); AtomicWrite(Path.Combine(installRoot,"current.json"),current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); }
	void WriteCurrentLauncher() { var current=ReadCurrent()!; var directory=Path.Combine(installRoot,"current"); Directory.CreateDirectory(directory); var exe=Path.Combine(installRoot,"versions",(string)current["active_version"]!,"dnSpy.exe"); var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows); AtomicWrite(Path.Combine(directory,"Start-dgSpy.cmd"),$"@echo off\r\nif not defined windir set \"windir={windows}\"\r\nif not defined SystemRoot set \"SystemRoot={windows}\"\r\nset \"DGSPY_STATE_ROOT={stateRoot}\"\r\nstart \"dgSpy\" \"{exe}\" %*\r\n"); }
	// A client that hands us an environment without windir kills dnSpy during WPF startup: the static
	// constructor of MS.Internal.FontCache.Util builds an absolute Uri for the Fonts directory out of it,
	// and an empty value makes the path relative, so it throws UriFormatException behind a modal dialog
	// before any dgSpy code runs (observed with Codex as the MCP client). Setting anything on
	// ProcessStartInfo.Environment makes .NET compose the child's block from ours instead of letting it
	// inherit a normal one, so the gap propagates all the way into dnSpy. SystemRoot is not a substitute
	// for windir — removing windir alone reproduces it — so both are filled, from the OS rather than from
	// each other, and only where the caller left a gap.
	static void BackfillWindowsEnvironment(IDictionary<string,string?> environment) {
		var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows); if(string.IsNullOrWhiteSpace(windows)) return;
		foreach(var name in new[]{"windir","SystemRoot"}) if(!environment.TryGetValue(name,out var value)||string.IsNullOrWhiteSpace(value)) environment[name]=windows;
	}
	/// <summary>The same repair, applied to our own environment, for the one launch that cannot pass a
	/// block of its own: a ShellExecute child inherits whatever the caller has, so a client that handed
	/// the Gateway an environment without windir would otherwise still kill the elevated host in WPF
	/// startup. Filling a gap the process should never have had is safe; nothing here overwrites a value.
	/// </summary>
	static void BackfillOwnWindowsEnvironment() {
		var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows); if(string.IsNullOrWhiteSpace(windows)) return;
		foreach(var name in new[]{"windir","SystemRoot"}) if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) Environment.SetEnvironmentVariable(name,windows);
	}
	static void AtomicWrite(string path,string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary=path+".tmp-"+Guid.NewGuid().ToString("N"); File.WriteAllText(temporary,content,new UTF8Encoding(false)); File.Move(temporary,path,true); }
	static void CopyTree(string source,string destination,CancellationToken token) { foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,directory))); } Directory.CreateDirectory(destination); foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)) { token.ThrowIfCancellationRequested(); var target=Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target,false); } }
	static void ValidateDnSpy(string path) { if(!File.Exists(Path.Combine(path,"dnSpy.exe"))||!File.Exists(Path.Combine(path,"bin","dnSpy.Contracts.DnSpy.dll"))||!File.Exists(Path.Combine(path,"bin","Extensions","dgSpy","dgSpy.Extension.x.dll"))) throw new GatewayControlException("invalid_dnspy_source",$"'{path}' is not a packaged dnSpy directory containing the dgSpy extension."); }
	// HashTree reads the whole payload — roughly a quarter of a gigabyte. Freshness is now checked by
	// get_started, doctor and get_local_deployment, so paying that on every call would make routine
	// diagnostics slow enough that people stop running them. The stamp is metadata-only (no file reads)
	// and changes whenever any file is added, removed, resized or rewritten, so a hit is safe.
	readonly object hashCacheSync=new object();
	// Keyed rather than a single slot: adoption now hashes the deployed tree as well as the installed
	// payload, and one slot shared by two roots would miss on every call and read half a gigabyte per
	// launch_local_host.
	readonly Dictionary<string,(string Stamp,string Hash)> hashCache=new(StringComparer.OrdinalIgnoreCase);
	string HashTreeCached(string root) => HashTreeCached(root,null,"payload|"+root);
	/// <summary>The content hash of a deployed tree, taken from the files themselves rather than from the
	/// provenance its manifest recorded. The manifest is excluded because it is written after the copy, so
	/// it is not part of what was copied and the result is directly comparable with the payload's hash.</summary>
	internal string HashDeployedTreeCached(string root) => HashTreeCached(root,DeploymentManifestName,"deployed|"+root);
	string HashTreeCached(string root,string? exclude,string key) {
		var stamp=TreeStamp(root);
		lock(hashCacheSync) { if(hashCache.TryGetValue(key,out var cached) && cached.Stamp==stamp) return cached.Hash; }
		var hash=HashTree(root,exclude);
		lock(hashCacheSync) hashCache[key]=(stamp,hash);
		return hash;
	}
	internal const string DeploymentManifestName="deployment-manifest.json";
	static string TreeStamp(string root) {
		var count=0L; var bytes=0L; var newest=0L;
		foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) {
			var info=new FileInfo(file); count++; bytes+=info.Length;
			var written=info.LastWriteTimeUtc.Ticks; if(written>newest) newest=written;
		}
		return $"{root}|{count}|{bytes}|{newest}";
	}
	static string HashTree(string root) => HashTree(root,null);
	static string HashTree(string root,string? excludeRelative) { using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase)) { var relative=Path.GetRelativePath(root,file).Replace('\\','/'); if(excludeRelative is not null && string.Equals(relative,excludeRelative,StringComparison.OrdinalIgnoreCase)) continue; hash.AppendData(Encoding.UTF8.GetBytes(relative)); hash.AppendData(File.ReadAllBytes(file)); } return Convert.ToHexString(hash.GetHashAndReset()); }
	static void EnsureSecret(string path) { if(File.Exists(path)&&!string.IsNullOrWhiteSpace(File.ReadAllText(path))) return; Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); }
	static string SafeSegment(string value) { value=value.Trim(); if(string.IsNullOrWhiteSpace(value)||value.IndexOfAny(Path.GetInvalidFileNameChars())>=0||value is "." or "..") throw new GatewayControlException("invalid_name",$"'{value}' is not a safe identifier."); return value; }
	string DefaultHostId() { var path=Path.Combine(stateRoot,"host.id"); if(File.Exists(path)&&!string.IsNullOrWhiteSpace(File.ReadAllText(path))) return File.ReadAllText(path).Trim(); var value=$"{Environment.MachineName}\\{Environment.UserName}"; return "local-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0,12).ToLowerInvariant(); }
	static void RequireConfirm(JsonObject args) { if((bool?)args["confirm"]!=true) throw new GatewayControlException("confirmation_required","Set confirm=true after reviewing the exact target."); }
}
