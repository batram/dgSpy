using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using dgSpy.Protocol;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace dgSpy.Gateway;

public sealed class HostEndpoint {
	public string HostId { get; }
	public string DisplayName { get; }
	public IPAddress Address { get; }
	public int Port { get; }
	internal string Token { get; }
	public bool IsOutbound { get; }
	public bool RequiresTls { get; }
	internal byte[]? ClientCertificateHash { get; }

	internal HostEndpoint(string hostId,string displayName,IPAddress address,int port,string token,bool isOutbound=false,bool requiresTls=false,byte[]? clientCertificateHash=null) {
		HostId=hostId; DisplayName=displayName; Address=address; Port=port; Token=token; IsOutbound=isOutbound; RequiresTls=requiresTls; ClientCertificateHash=clientCertificateHash;
	}
}

public sealed class HostRegistry {
	readonly Dictionary<string,HostEndpoint> endpoints;
	public IReadOnlyCollection<HostEndpoint> Endpoints => endpoints.Values;

	HostRegistry(IEnumerable<HostEndpoint> endpoints) {
		this.endpoints=endpoints.ToDictionary(endpoint=>endpoint.HostId,StringComparer.Ordinal);
	}

	public static HostRegistry Load() {
		var path=Environment.GetEnvironmentVariable("DGSPY_HOSTS_FILE");
		if(string.IsNullOrWhiteSpace(path)) return Local();
		return Load(path,string.Equals(Environment.GetEnvironmentVariable("DGSPY_INCLUDE_LOCAL_HOST"),"true",StringComparison.OrdinalIgnoreCase));
	}

	internal static HostRegistry Load(string path,bool includeLocal) {
		var configured=FromJsonCore(File.ReadAllText(path),Path.GetDirectoryName(Path.GetFullPath(path))!,allowEmpty:includeLocal);
		if(!includeLocal) return configured;
		var combined=configured.Endpoints.ToList(); var local=Local().Endpoints.Single(); if(combined.Any(endpoint=>endpoint.HostId==local.HostId)) throw new InvalidOperationException($"Configured host_id '{local.HostId}' conflicts with the managed local host."); combined.Add(local); return new HostRegistry(combined);
	}

	public static HostRegistry FromJson(string json,string baseDirectory) => FromJsonCore(json,baseDirectory,allowEmpty:false);
	static HostRegistry FromJsonCore(string json,string baseDirectory,bool allowEmpty) {
		var document=ProtocolJson.Deserialize<HostRegistryDocument>(json) ?? throw new InvalidOperationException("The host registry is invalid JSON.");
		if (!allowEmpty && document.Hosts.Length==0) throw new InvalidOperationException("The host registry contains no hosts.");
		var endpoints=new List<HostEndpoint>();
		var ids=new HashSet<string>(StringComparer.Ordinal);
		foreach (var host in document.Hosts) {
			if (string.IsNullOrWhiteSpace(host.HostId)) throw new InvalidOperationException("Every registered host requires host_id.");
			if (!ids.Add(host.HostId)) throw new InvalidOperationException($"Duplicate host_id '{host.HostId}'.");
			var requiresTls=string.Equals(host.Transport,"outbound_tls",StringComparison.Ordinal); var outbound=requiresTls || string.Equals(host.Transport,"outbound",StringComparison.Ordinal);
			if (!outbound && (!IPAddress.TryParse(host.Address,out var parsed) || !IPAddress.IsLoopback(parsed)))
				throw new InvalidOperationException($"Host '{host.HostId}' must use a loopback tunnel endpoint, not '{host.Address}'.");
			var address=outbound ? IPAddress.None : IPAddress.Parse(host.Address);
			if (!outbound && host.Port is <1 or >65535) throw new InvalidOperationException($"Host '{host.HostId}' has an invalid port.");
			var token=ResolveToken(host,baseDirectory);
			byte[]? clientCertificateHash=null;
			if (requiresTls) { var certificatePath=ResolvePath(host.ClientCertificateFile,baseDirectory,$"client certificate for host '{host.HostId}'"); using var certificate=new X509Certificate2(certificatePath); clientCertificateHash=certificate.GetCertHash(); }
			endpoints.Add(new HostEndpoint(host.HostId,host.DisplayName ?? host.HostId,address,host.Port,token,outbound,requiresTls,clientCertificateHash));
		}
		return new HostRegistry(endpoints);
	}

	public bool TryGet(string hostId,out HostEndpoint endpoint) => endpoints.TryGetValue(hostId,out endpoint!);

	public HostEndpoint Select(string? requestedHostId) {
		if (!string.IsNullOrWhiteSpace(requestedHostId)) {
			if (TryGet(requestedHostId,out var selected)) return selected;
			throw new HostRoutingException("unknown_host",$"Host '{requestedHostId}' is not registered.");
		}
		if (endpoints.Count==1) return endpoints.Values.Single();
		throw new HostRoutingException("host_required","host_id is required when the Gateway has multiple registered hosts.");
	}

	static HostRegistry Local() {
		var root=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
		var configuredId=Environment.GetEnvironmentVariable("DGSPY_HOST_ID");
		var hostId=!string.IsNullOrWhiteSpace(configuredId) ? configuredId.Trim() : ReadRequired(Path.Combine(root,"host.id"),"host identity");
		var token=RpcClientSettings.LoadToken();
		var port=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_RPC_PORT"),out var configuredPort) ? configuredPort : 7351;
		return new HostRegistry(new[]{new HostEndpoint(hostId,$"dgSpy {hostId}",IPAddress.Loopback,port,token)});
	}

	static string ResolveToken(HostRegistration host,string baseDirectory) {
		var sources=(string.IsNullOrWhiteSpace(host.TokenEnvironment) ? 0 : 1)+(string.IsNullOrWhiteSpace(host.TokenFile) ? 0 : 1);
		if (sources!=1) throw new InvalidOperationException($"Host '{host.HostId}' requires exactly one of token_environment or token_file.");
		if (!string.IsNullOrWhiteSpace(host.TokenEnvironment)) {
			var value=Environment.GetEnvironmentVariable(host.TokenEnvironment);
			if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Credential environment variable '{host.TokenEnvironment}' for host '{host.HostId}' is missing.");
			return value.Trim();
		}
		var path=Path.IsPathRooted(host.TokenFile!) ? host.TokenFile! : Path.Combine(baseDirectory,host.TokenFile!);
		return ReadRequired(path,$"credential for host '{host.HostId}'");
	}

	static string ReadRequired(string path,string purpose) {
		if (!File.Exists(path)) throw new InvalidOperationException($"The {purpose} file '{path}' does not exist.");
		var value=File.ReadAllText(path).Trim();
		if (string.IsNullOrEmpty(value)) throw new InvalidOperationException($"The {purpose} file '{path}' is empty.");
		return value;
	}
	static string ResolvePath(string? configured,string baseDirectory,string purpose) { if(string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException($"The {purpose} file is not configured."); var path=Path.IsPathRooted(configured) ? configured : Path.Combine(baseDirectory,configured); if(!File.Exists(path)) throw new InvalidOperationException($"The {purpose} file '{path}' does not exist."); return path; }

	sealed class HostRegistryDocument { [JsonPropertyName("hosts")] public HostRegistration[] Hosts { get; set; }=Array.Empty<HostRegistration>(); }
	sealed class HostRegistration {
		[JsonPropertyName("host_id")] public string HostId { get; set; }="";
		[JsonPropertyName("display_name")] public string? DisplayName { get; set; }
		[JsonPropertyName("address")] public string Address { get; set; }="127.0.0.1";
		[JsonPropertyName("port")] public int Port { get; set; }
		[JsonPropertyName("transport")] public string? Transport { get; set; }
		[JsonPropertyName("token_environment")] public string? TokenEnvironment { get; set; }
		[JsonPropertyName("token_file")] public string? TokenFile { get; set; }
		[JsonPropertyName("client_certificate_file")] public string? ClientCertificateFile { get; set; }
	}
}

public sealed class HostRouter {
	readonly object sync=new(); HostRegistry registry;
	Dictionary<string,IHostRpcClient> clients;
	public HostRouter() : this(HostRegistry.Load()) { }
	internal HostRouter(HostRegistry registry) {
		this.registry=registry;
		clients=CreateClients(registry);
	}
	static Dictionary<string,IHostRpcClient> CreateClients(HostRegistry registry) => registry.Endpoints.ToDictionary(endpoint=>endpoint.HostId,CreateClient,StringComparer.Ordinal);
	static IHostRpcClient CreateClient(HostEndpoint endpoint) => endpoint.IsOutbound ? new RegisteredRpcClient(endpoint) : new EndpointRpcClient(endpoint);
	internal bool IsRegistered(string hostId) { lock(sync) return registry.TryGet(hostId,out _); }
	internal void Reload(HostRegistry updated) {
		Dictionary<string,IHostRpcClient> previous,next;
		lock(sync) {
			previous=clients; next=new Dictionary<string,IHostRpcClient>(StringComparer.Ordinal);
			foreach(var endpoint in updated.Endpoints) {
				if(previous.TryGetValue(endpoint.HostId,out var existing) && existing.Matches(endpoint)) next.Add(endpoint.HostId,existing);
				else next.Add(endpoint.HostId,CreateClient(endpoint));
			}
			registry=updated; clients=next;
		}
		foreach(var retired in previous.Values.Where(client=>!next.Values.Contains(client))) retired.Dispose();
	}
	internal bool TryAuthenticate(string hostId,string token,bool isTls,byte[]? clientCertificateHash,out string error) {
		HostEndpoint endpoint; lock(sync) if (!registry.TryGet(hostId,out endpoint!) || !endpoint.IsOutbound) { error="Unknown outbound host."; return false; }
		if (endpoint.RequiresTls!=isTls) { error=$"Host '{hostId}' requires {(endpoint.RequiresTls ? "TLS" : "plaintext")}."; return false; }
		if (endpoint.RequiresTls && (clientCertificateHash is null || endpoint.ClientCertificateHash is null || !RpcCredential.FixedTimeEquals(clientCertificateHash,endpoint.ClientCertificateHash))) { error="Client certificate does not match host_id."; return false; }
		if (!RpcCredential.FixedTimeEquals(token,endpoint.Token)) { error="Invalid host credential."; return false; }
		error=""; return true;
	}
	internal bool IsKnownClientCertificate(byte[] hash) { lock(sync) return registry.Endpoints.Any(endpoint=>endpoint.RequiresTls && endpoint.ClientCertificateHash is not null && RpcCredential.FixedTimeEquals(hash,endpoint.ClientCertificateHash)); }
	internal bool TryRegister(string hostId,TcpClient client,StreamReader reader,StreamWriter writer,out string error) {
		if(!TryPrepareRegistration(hostId,client,reader,writer,out var activate,out error)) return false;
		activate(); return true;
	}
	internal bool TryPrepareRegistration(string hostId,TcpClient client,StreamReader reader,StreamWriter writer,out Action activate,out string error) {
		IHostRpcClient? value; lock(sync) clients.TryGetValue(hostId,out value);
		if (value is not RegisteredRpcClient registered) { activate=()=>{ }; error="Unknown outbound host."; return false; }
		return registered.TryPrepareRegistration(client,reader,writer,out activate,out error);
	}

	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		HostEndpoint? endpoint=null;
		try {
			var requestedHostId=(string?)request.Arguments["host_id"];
			IHostRpcClient client; lock(sync) { endpoint=registry.Select(requestedHostId); client=clients[endpoint.HostId]; }
			request.Arguments.Remove("host_id");
			return await client.CallAsync(request,cancellationToken);
		}
		catch (HostRoutingException ex) { return RpcResponse.Failure(request.RequestId,ex.Code,ex.Message); }
		catch (OperationCanceledException) {
			return RpcResponse.Failure(request.RequestId,"deadline_exceeded","The debugger operation exceeded its deadline. If it can mutate debugger or target state, assume it may have applied: read get_session_state and the relevant list/read tool before deciding whether to retry. For a read-only query, narrow its filters and retry.");
		}
		catch (Exception ex) when (ex is IOException || ex is SocketException) {
			var recovery=endpoint is not null && !endpoint.IsOutbound
				? "Call launch_local_host, then retry the original tool."
				: $"Reconnect provisioned host '{endpoint?.HostId ?? "unknown"}', then retry the original tool.";
			return RpcResponse.Failure(request.RequestId,"host_unavailable",$"Debugger host '{endpoint?.HostId ?? "unknown"}' is unavailable: {ex.Message} {recovery}");
		}
	}

	public async Task<object[]> ListHostsAsync(CancellationToken cancellationToken) {
		var hosts=new List<object>();
		(HostEndpoint Endpoint,IHostRpcClient Client)[] snapshot; lock(sync) snapshot=registry.Endpoints.Select(endpoint=>(endpoint,clients[endpoint.HostId])).OrderBy(item=>item.endpoint.HostId,StringComparer.Ordinal).ToArray();
		foreach (var item in snapshot) { var endpoint=item.Endpoint;
			try {
				var response=await item.Client.CallAsync(new RpcRequest { Operation="get_host_info",DeadlineUtc=DateTime.UtcNow.AddSeconds(5) },cancellationToken);
				var info=response.Error is null ? ProtocolJson.ToObject(response.Result!) : null;
				var state=response.Error is null ? (string?)info?["connection_state"] ?? "connected" : "error";
				hosts.Add(new { host_id=endpoint.HostId,display_name=endpoint.DisplayName,state,host=response.Result,error=response.Error });
			}
			catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException) {
				hosts.Add(new { host_id=endpoint.HostId,display_name=endpoint.DisplayName,state="unavailable",host=(object?)null,error=new RpcError { Code="host_unavailable",Message=ex.Message } });
			}
		}
		return hosts.ToArray();
	}

	public async Task SendHeartbeatAsync(CancellationToken cancellationToken) {
		IHostRpcClient[] snapshot; lock(sync) snapshot=clients.Values.ToArray();
		await Task.WhenAll(snapshot.Select(client=>SendHeartbeatAsync(client,cancellationToken)));
	}
	static async Task SendHeartbeatAsync(IHostRpcClient client,CancellationToken cancellationToken) {
		try { await client.CallAsync(new RpcRequest { Operation="gateway_heartbeat",DeadlineUtc=DateTime.UtcNow.AddSeconds(3) },cancellationToken); }
		catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException) { }
	}
}

public sealed class GatewayHeartbeat : BackgroundService {
	readonly HostRouter router;
	public GatewayHeartbeat(HostRouter router) { this.router=router; }
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		while (!stoppingToken.IsCancellationRequested) {
			await router.SendHeartbeatAsync(stoppingToken);
			try { await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken); }
			catch (OperationCanceledException) { return; }
		}
	}
}

interface IHostRpcClient : IDisposable { bool Matches(HostEndpoint endpoint); Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken); }

sealed class EndpointRpcClient : IHostRpcClient {
	readonly HostEndpoint endpoint;
	public EndpointRpcClient(HostEndpoint endpoint) { this.endpoint=endpoint; }
	public bool Matches(HostEndpoint candidate) => !candidate.IsOutbound && endpoint.HostId==candidate.HostId && endpoint.Address.Equals(candidate.Address) && endpoint.Port==candidate.Port && RpcCredential.FixedTimeEquals(endpoint.Token,candidate.Token);
	public void Dispose() { }

	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		using var deadline=new CancellationTokenSource();
		if (request.DeadlineUtc is DateTime deadlineUtc) deadline.CancelAfter(deadlineUtc-DateTime.UtcNow > TimeSpan.Zero ? deadlineUtc-DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
		using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
		cancellationToken=linked.Token;
		using var client=new TcpClient();
		await client.ConnectAsync(endpoint.Address,endpoint.Port,cancellationToken);
		using var stream=client.GetStream();
		using var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true) { AutoFlush=true };
		using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true);
		var ping=new RpcRequest { Operation="ping",HostId=endpoint.HostId,AuthenticationToken=endpoint.Token,DeadlineUtc=DateTime.UtcNow.AddSeconds(3) };
		await writer.WriteLineAsync(ProtocolJson.Serialize(ping));
		var handshakeLine=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe during handshake.");
		var response=ProtocolJson.Deserialize<RpcResponse>(handshakeLine) ?? throw new IOException("Invalid dgSpy handshake response.");
		if (response.Version!=ProtocolVersion.Current || response.Error is not null)
			throw new IOException(response.Error?.Message ?? $"dgSpy protocol mismatch: expected {ProtocolVersion.Current}, received {response.Version}.");
		var handshake=(response.Result as JsonObject)?.Deserialize<Handshake>(ProtocolJson.Options) ?? throw new IOException("The dgSpy extension returned an invalid handshake.");
		RpcClientSettings.EnsureExpectedHost(endpoint.HostId,handshake.HostId);
		request.HostId=handshake.HostId; request.AuthenticationToken=endpoint.Token;
		await writer.WriteLineAsync(ProtocolJson.Serialize(request));
		var line=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe.");
		return ProtocolJson.Deserialize<RpcResponse>(line) ?? throw new IOException("Invalid RPC response.");
	}
}

sealed class RegisteredRpcClient : IHostRpcClient {
	readonly HostEndpoint endpoint; readonly object sync=new(); ReverseConnection? connection;
	public RegisteredRpcClient(HostEndpoint endpoint) { this.endpoint=endpoint; }
	public bool Matches(HostEndpoint candidate) => candidate.IsOutbound && endpoint.HostId==candidate.HostId && endpoint.RequiresTls==candidate.RequiresTls && RpcCredential.FixedTimeEquals(endpoint.Token,candidate.Token) && CertificateEquals(endpoint.ClientCertificateHash,candidate.ClientCertificateHash);
	static bool CertificateEquals(byte[]? left,byte[]? right) => left is null ? right is null : right is not null && RpcCredential.FixedTimeEquals(left,right);
	public bool TryPrepareRegistration(TcpClient client,StreamReader reader,StreamWriter writer,out Action activate,out string error) {
		lock(sync) {
			if (connection is { SocketAlive:true }) { activate=()=>{ }; error=$"Host '{endpoint.HostId}' already has a live connection."; return false; }
			connection?.Dispose(); var pending=new ReverseConnection(client,reader,writer); connection=pending; activate=pending.Activate; error=""; return true;
		}
	}
	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		using var deadline=new CancellationTokenSource();
		if(request.DeadlineUtc is not null) deadline.CancelAfter(RpcTimeout.Resolve(null,request.DeadlineUtc,DateTime.UtcNow,RpcTimeout.MaximumMilliseconds));
		using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
		ReverseConnection? active; lock(sync) active=connection;
		if (active is null || !active.IsAlive) throw new IOException($"Host '{endpoint.HostId}' is not connected.");
		request.HostId=endpoint.HostId; request.AuthenticationToken=endpoint.Token;
		request.TimeoutMs=RpcTimeout.RemainingMilliseconds(request.DeadlineUtc,DateTime.UtcNow,RpcTimeout.MaximumMilliseconds);
		return await active.CallAsync(request,linked.Token);
	}
	public void Dispose() { lock(sync) { connection?.Dispose(); connection=null; } }
}

sealed class ReverseConnection : IDisposable {
	readonly TcpClient client; readonly StreamReader reader; readonly StreamWriter writer;
	readonly SemaphoreSlim writes=new(1,1);
	readonly ConcurrentDictionary<string,TaskCompletionSource<RpcResponse>> pending=new(StringComparer.Ordinal);
	readonly CancellationTokenSource closed=new();
	Task? responses; volatile bool active; int disposed;
	public bool SocketAlive { get { try { return client.Connected && !(client.Client.Poll(0,SelectMode.SelectRead) && client.Available==0); } catch { return false; } } }
	public bool IsAlive => active && SocketAlive;
	public ReverseConnection(TcpClient client,StreamReader reader,StreamWriter writer) { this.client=client; this.reader=reader; this.writer=writer; }
	public void Activate() { active=true; responses=Task.Run(ReadResponsesAsync); }
	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		if(!IsAlive) throw new IOException("The remote host is not connected.");
		var completion=new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		if(!pending.TryAdd(request.RequestId,completion)) throw new IOException($"Duplicate outbound request id '{request.RequestId}'.");
		try {
			await writes.WaitAsync(cancellationToken);
			try { await writer.WriteLineAsync(ProtocolJson.Serialize(request)); }
			finally { writes.Release(); }
			return await completion.Task.WaitAsync(cancellationToken);
		}
		finally { pending.TryRemove(request.RequestId,out _); }
	}
	async Task ReadResponsesAsync() {
		Exception failure;
		try {
			string? line;
			while((line=await reader.ReadLineAsync(closed.Token)) is not null) {
				var response=ProtocolJson.Deserialize<RpcResponse>(line) ?? throw new IOException("Invalid remote host response.");
				if(pending.TryRemove(response.RequestId,out var completion)) completion.TrySetResult(response);
			}
			failure=new IOException("The remote host disconnected.");
		}
		catch(OperationCanceledException) when(closed.IsCancellationRequested) { failure=new IOException("The remote host connection closed."); }
		catch(Exception ex) { failure=ex; }
		active=false;
		foreach(var item in pending.ToArray()) if(pending.TryRemove(item.Key,out var completion)) completion.TrySetException(failure);
	}
	public void Dispose() {
		if(Interlocked.Exchange(ref disposed,1)!=0) return;
		active=false; closed.Cancel(); client.Dispose();
		var failure=new IOException("The remote host connection closed.");
		foreach(var item in pending.ToArray()) if(pending.TryRemove(item.Key,out var completion)) completion.TrySetException(failure);
	}
}

public sealed class RemoteHostListener : BackgroundService {
	readonly HostRouter router; readonly object sync=new(); readonly Dictionary<string,ListenerState> listeners=new(StringComparer.Ordinal); readonly CancellationTokenSource shutdown=new(); int disposed;
	IReadOnlyList<ListenerFailure> unavailable=Array.Empty<ListenerFailure>();
	public RemoteHostListener(HostRouter router) { this.router=router; }
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		using var registration=stoppingToken.Register(shutdown.Cancel);
		var configured=RemoteListenerConfiguration.Load(); if(configured is not null) EnsureListening(configured);
		try { await Task.Delay(Timeout.Infinite,stoppingToken); } catch(OperationCanceledException) { }
	}
	internal object EnsureConfigured(string registryPath) {
		var configured=RemoteListenerConfiguration.FromRegistry(registryPath); EnsureListening(configured);
		lock(sync) return new { address=configured.Addresses[0].ToString(),addresses=configured.Addresses.Select(item=>item.ToString()).ToArray(),plaintext_port=configured.PlaintextPort,tls_port=configured.TlsPort,listening=Bound(),unavailable=Unavailable() };
	}
	/// <summary>What is bound right now and what is not. With more than one address in play the listener
	/// can be half up -- one interface bound, another refused -- and nothing else in the Gateway reports
	/// that, so a host provisioned through the refused address would just never connect.</summary>
	public object Describe() { lock(sync) return new { listening=Bound(),unavailable=Unavailable() }; }
	string[] Bound() => listeners.Keys.OrderBy(value=>value,StringComparer.Ordinal).ToArray();
	object[] Unavailable() => unavailable.Select(item=>(object)new { endpoint=Format(item),address=item.Address,port=item.Port,tls=item.Tls,error=item.Error }).ToArray();
	// A Gateway is often reachable on more than one address -- a LAN adapter plus a Hyper-V or WSL virtual
	// switch is the ordinary case -- and each remote host dials whichever address it was provisioned with.
	// So every configured address gets its own bound socket, and one of them failing must not take the
	// others down with it: a virtual adapter that is currently down, or a port already taken on just that
	// interface, would otherwise cost every working host its Gateway.
	void EnsureListening(RemoteListenerConfiguration configured) {
		var required=new List<(IPAddress Address,int Port,bool Tls)>();
		foreach(var address in configured.Addresses) {
			if(configured.PlaintextPort is int plaintext) required.Add((address,plaintext,false));
			if(configured.TlsPort is int tls) required.Add((address,tls,true));
		}
		var keys=new HashSet<string>(required.Select(item=>Key(item.Address,item.Port,item.Tls)),StringComparer.Ordinal);
		// Retire what is no longer configured before binding what is, so that replacing the set never has
		// the old and new shape of the same port live at once -- widening to 0.0.0.0:7352 while
		// 192.168.2.115:7352 is still up leaves two sockets accepting on one port until the sweep. Windows
		// does allow that overlap (measured: a wildcard binds over a specific address and the reverse,
		// despite TcpListener's exclusive address use), so this is about not depending on that, not about
		// working around it. Nothing is lost by going first: a retired key is by definition an address the
		// new configuration does not want.
		KeyValuePair<string,ListenerState>[] retired;
		lock(sync) { retired=listeners.Where(pair=>!keys.Contains(pair.Key)).ToArray(); foreach(var item in retired) listeners.Remove(item.Key); }
		foreach(var item in retired) item.Value.Dispose();
		var failures=new List<ListenerFailure>();
		foreach(var item in required) Ensure(item.Address,item.Port,item.Tls,item.Tls?configured.LoadCertificate:null,failures);
		bool bound; lock(sync) { bound=listeners.Keys.Any(keys.Contains); unavailable=failures; }
		// Losing one address out of several is degraded; losing all of them is a configuration error that
		// has to be raised, because otherwise provisioning reports a ready Gateway that nothing can reach.
		if(keys.Count>0 && !bound) throw new GatewayControlException("listener_bind_failed",$"No remote-host listener address could be bound: {string.Join("; ",failures.Select(item=>$"{Format(item)} {item.Error}"))}");
	}
	// IPEndPoint rather than string concatenation: an IPv6 wildcard renders as "[::]:7352" instead of the
	// ":::7352" that reads as a typo, and IPv4 keys keep the shape they have always had.
	static string Key(IPAddress address,int port,bool useTls) => $"{new IPEndPoint(address,port)}:{useTls}";
	void Ensure(IPAddress address,int port,bool useTls,Func<X509Certificate2>? certificate,List<ListenerFailure> failures) {
		var key=Key(address,port,useTls);
		lock(sync) if(listeners.ContainsKey(key)) return;
		X509Certificate2? loaded=null; TcpListener? listener=null;
		try {
			loaded=certificate?.Invoke();
			listener=new TcpListener(address,port); listener.Start(16);
			var state=new ListenerState(listener,loaded); listener=null; loaded=null;
			lock(sync) { if(listeners.ContainsKey(key)) { state.Dispose(); return; } listeners.Add(key,state); state.Task=ListenAsync(state,useTls,shutdown.Token); }
		}
		catch(Exception ex) { try { listener?.Stop(); } catch { } loaded?.Dispose(); failures.Add(new ListenerFailure(address.ToString(),port,useTls,ex.Message)); }
	}
	static string Format(ListenerFailure failure) => new IPEndPoint(IPAddress.Parse(failure.Address),failure.Port).ToString();
	async Task ListenAsync(ListenerState state,bool useTls,CancellationToken token) { try { while(!token.IsCancellationRequested) { var client=await state.Listener.AcceptTcpClientAsync(token); _=RegisterAsync(client,useTls,state.Certificate,token); } } catch(OperationCanceledException) when(token.IsCancellationRequested) { } catch(ObjectDisposedException) { } }
	async Task RegisterAsync(TcpClient client,bool useTls,X509Certificate2? serverCertificate,CancellationToken token) {
		try {
			Stream stream=client.GetStream(); byte[]? clientCertificateHash=null;
			if(useTls) { var tls=new SslStream(stream,false,(_,certificate,__,___)=>certificate is not null && router.IsKnownClientCertificate(certificate.GetCertHash())); await tls.AuthenticateAsServerAsync(serverCertificate!,true,SslProtocols.Tls12,false); stream=tls; clientCertificateHash=tls.RemoteCertificate?.GetCertHash(); }
			var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true); var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true){AutoFlush=true};
			var line=await reader.ReadLineAsync(token); var request=line is null ? null : ProtocolJson.Deserialize<RpcRequest>(line); string error="Invalid registration.";
			if (request is null || request.Operation!="register_host" || request.Version!=ProtocolVersion.Current || string.IsNullOrWhiteSpace(request.HostId) || !router.TryAuthenticate(request.HostId,request.AuthenticationToken ?? "",useTls,clientCertificateHash,out error) || !router.TryPrepareRegistration(request.HostId,client,reader,writer,out var activate,out error)) {
				await writer.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Failure(request?.RequestId ?? "","registration_rejected",error))); client.Dispose(); return;
			}
			await writer.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,new HostRegistration { HostId=request.HostId })));
			activate();
		} catch { client.Dispose(); }
	}
	public override void Dispose() { if(Interlocked.Exchange(ref disposed,1)!=0) return; ListenerState[] active; lock(sync) active=listeners.Values.ToArray(); foreach(var item in active) item.Dispose(); try { shutdown.Cancel(); } catch(ObjectDisposedException) { } catch(AggregateException ex) when(ex.InnerExceptions.All(inner=>inner is ObjectDisposedException)) { } base.Dispose(); }
	sealed class ListenerState : IDisposable { public TcpListener Listener { get; } public X509Certificate2? Certificate { get; } public Task? Task { get; set; } public ListenerState(TcpListener listener,X509Certificate2? certificate) { Listener=listener; Certificate=certificate; } public void Dispose() { Listener.Stop(); Certificate?.Dispose(); } }
	sealed record ListenerFailure(string Address,int Port,bool Tls,string Error);
}

/// <summary>The Gateway's remote-host listener is a set of addresses, not one address. A machine that
/// reaches its hosts over several interfaces -- a LAN adapter plus a Hyper-V or WSL virtual switch is
/// the ordinary case -- provisions each host with the address that host can actually dial, so the
/// listener has to be bound to every one of them. <c>0.0.0.0</c> (or <c>any</c>, <c>all</c>, <c>*</c>)
/// means every interface, and absorbs any address listed beside it: binding a wildcard and a specific
/// address on the same port collides, and the wildcard already covers the specific one.</summary>
static class ListenerAddresses {
	public const string Any="0.0.0.0";
	static readonly char[] Separators={',',';',' '};
	public static bool IsAny(string value) { var text=value.Trim(); return text==Any || text=="::" || text=="*" || string.Equals(text,"any",StringComparison.OrdinalIgnoreCase) || string.Equals(text,"all",StringComparison.OrdinalIgnoreCase); }
	public static string[] Split(string value) => value.Split(Separators,StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
	/// <summary>The set as it is persisted and reported: trimmed, de-duplicated, order preserved so the
	/// first address stays the one older Gateways read from the scalar <c>address</c> field.</summary>
	public static string[] Normalize(IEnumerable<string> values) {
		var result=new List<string>();
		foreach(var value in values) {
			var text=value.Trim(); if(text.Length==0) continue;
			if(IsAny(text)) return new[]{Any};
			if(!result.Contains(text,StringComparer.OrdinalIgnoreCase)) result.Add(text);
		}
		return result.ToArray();
	}
	/// <summary>The addresses to actually bind. A DNS hostname is what a remote host dials, not something
	/// this machine can bind to: it names the Gateway from outside and may resolve to any of its
	/// interfaces, so it widens the bind to all of them rather than failing provisioning outright, which
	/// is what a hostname used to do the moment the listener was activated.</summary>
	public static IPAddress[] Bindable(IEnumerable<string> values) {
		var result=new List<IPAddress>(); var any=false;
		foreach(var value in Normalize(values)) {
			if(IsAny(value) || !IPAddress.TryParse(value,out var address)) { any=true; continue; }
			if(!result.Contains(address)) result.Add(address);
		}
		if(any) return Socket.OSSupportsIPv6 ? new[]{IPAddress.Any,IPAddress.IPv6Any} : new[]{IPAddress.Any};
		return result.Count>0 ? result.ToArray() : throw new InvalidOperationException("The persisted remote listener configuration names no address.");
	}
	/// <summary>Reads the persisted set, tolerating a registry written before the listener held more than
	/// one address.</summary>
	public static string[] Read(JsonObject? listener) {
		if(listener is null) return Array.Empty<string>();
		var addresses=listener["addresses"]?.AsArray();
		return Normalize(addresses is null ? new[]{(string?)listener["address"] ?? ""} : addresses.Select(node=>(string?)node ?? ""));
	}
}

sealed class RemoteListenerConfiguration {
	public IReadOnlyList<IPAddress> Addresses { get; } public int? PlaintextPort { get; } public int? TlsPort { get; } readonly string? certificateFile,passwordFile;
	RemoteListenerConfiguration(IReadOnlyList<IPAddress> addresses,int? plaintextPort,int? tlsPort,string? certificateFile,string? passwordFile) { Addresses=addresses; PlaintextPort=plaintextPort; TlsPort=tlsPort; this.certificateFile=certificateFile; this.passwordFile=passwordFile; }
	public static RemoteListenerConfiguration? Load() {
		var addressText=Environment.GetEnvironmentVariable("DGSPY_REMOTE_ADDRESS");
		if(!string.IsNullOrWhiteSpace(addressText)) return FromEnvironment(addressText);
		var registry=Environment.GetEnvironmentVariable("DGSPY_HOSTS_FILE"); return string.IsNullOrWhiteSpace(registry) || !File.Exists(registry) ? null : FromRegistry(registry);
	}
	static RemoteListenerConfiguration FromEnvironment(string addressText) {
		var addresses=ListenerAddresses.Bindable(ListenerAddresses.Split(addressText)); int? plaintext=string.Equals(Environment.GetEnvironmentVariable("DGSPY_REMOTE_DISABLE_PLAINTEXT"),"true",StringComparison.OrdinalIgnoreCase) ? null : int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_PORT"),out var port) ? port : 7352;
		var tls=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_TLS_PORT"),out var tlsPort) ? tlsPort : (int?)null;
		return new RemoteListenerConfiguration(addresses,plaintext,tls,Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE"),Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE"));
	}
	public static RemoteListenerConfiguration FromRegistry(string path) {
		var root=JsonNode.Parse(File.ReadAllText(path))!.AsObject(); var listener=root["listener"]?.AsObject() ?? throw new InvalidOperationException("The host registry has outbound hosts but no persisted listener configuration.");
		var addresses=ListenerAddresses.Bindable(ListenerAddresses.Read(listener)); var hosts=root["hosts"]?.AsArray() ?? new JsonArray(); int? plaintext=hosts.Any(node=>(string?)node?["transport"]=="outbound") ? (int?)listener["plaintext_port"] ?? 7352 : null; var hasTls=hosts.Any(node=>(string?)node?["transport"]=="outbound_tls"); var tlsNode=root["tls"]?.AsObject(); int? tls=hasTls ? (int?)tlsNode?["port"] ?? 7353 : null; var baseDirectory=Path.GetDirectoryName(Path.GetFullPath(path))!;
		return new RemoteListenerConfiguration(addresses,plaintext,tls,Resolve(baseDirectory,(string?)tlsNode?["server_certificate_file"]),Resolve(baseDirectory,(string?)tlsNode?["server_certificate_password_file"]));
	}
	public X509Certificate2 LoadCertificate() { if(TlsPort is null) throw new InvalidOperationException("TLS is not configured."); if(string.IsNullOrWhiteSpace(certificateFile)||string.IsNullOrWhiteSpace(passwordFile)||!File.Exists(certificateFile)||!File.Exists(passwordFile)) throw new InvalidOperationException("The Gateway TLS certificate and password files are required for the TLS listener."); return new X509Certificate2(certificateFile,File.ReadAllText(passwordFile).Trim(),X509KeyStorageFlags.DefaultKeySet); }
	static string? Resolve(string root,string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.IsPathRooted(value) ? value : Path.Combine(root,value);
}

static class RpcCredential {
	public static bool FixedTimeEquals(string presented,string expected) {
		var different=presented.Length^expected.Length; var count=Math.Max(presented.Length,expected.Length);
		for(var index=0;index<count;index++) different|=(index<presented.Length?presented[index]:0)^(index<expected.Length?expected[index]:0);
		return different==0;
	}
	public static bool FixedTimeEquals(byte[] presented,byte[] expected) { var different=presented.Length^expected.Length; var count=Math.Max(presented.Length,expected.Length); for(var index=0;index<count;index++) different|=(index<presented.Length?presented[index]:0)^(index<expected.Length?expected[index]:0); return different==0; }
}

public sealed class HostRoutingException : Exception {
	public string Code { get; }
	public HostRoutingException(string code,string message) : base(message) { Code=code; }
}

public static class RpcClientSettings {
	public static string LoadToken() {
		var configured=Environment.GetEnvironmentVariable("DGSPY_RPC_TOKEN");
		if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
		var root=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
		var path=Path.Combine(root,"rpc.token");
		if (!File.Exists(path)) throw new InvalidOperationException($"No extension RPC credential exists at '{path}'. Start dnSpy with dgSpy loaded or set DGSPY_RPC_TOKEN.");
		var token=File.ReadAllText(path).Trim();
		if (string.IsNullOrEmpty(token)) throw new InvalidOperationException($"The extension RPC credential at '{path}' is empty.");
		return token;
	}

	public static void EnsureExpectedHost(string? expected,string actual) {
		if (string.IsNullOrEmpty(actual)) throw new IOException("The dgSpy extension did not report host_id.");
		if (!string.IsNullOrEmpty(expected) && !string.Equals(expected,actual,StringComparison.Ordinal))
			throw new IOException($"Configured host '{expected}' does not match extension host '{actual}'.");
	}
}
