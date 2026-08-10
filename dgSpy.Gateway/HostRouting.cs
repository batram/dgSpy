using System.Net;
using System.Net.Sockets;
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
	readonly TcpClient client; readonly StreamReader reader; readonly StreamWriter writer; readonly SemaphoreSlim calls=new(1,1); volatile bool active;
	public bool SocketAlive { get { try { return client.Connected && !(client.Client.Poll(0,SelectMode.SelectRead) && client.Available==0); } catch { return false; } } }
	public bool IsAlive => active && SocketAlive;
	public ReverseConnection(TcpClient client,StreamReader reader,StreamWriter writer) { this.client=client; this.reader=reader; this.writer=writer; }
	public void Activate() => active=true;
	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		await calls.WaitAsync(cancellationToken);
		try {
			await writer.WriteLineAsync(ProtocolJson.Serialize(request));
			var line=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The remote host disconnected.");
			return ProtocolJson.Deserialize<RpcResponse>(line) ?? throw new IOException("Invalid remote host response.");
		} catch { Dispose(); throw; } finally { calls.Release(); }
	}
	public void Dispose() { client.Dispose(); }
}

public sealed class RemoteHostListener : BackgroundService {
	readonly HostRouter router; readonly object sync=new(); readonly Dictionary<string,ListenerState> listeners=new(StringComparer.Ordinal); readonly CancellationTokenSource shutdown=new(); int disposed;
	public RemoteHostListener(HostRouter router) { this.router=router; }
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		using var registration=stoppingToken.Register(shutdown.Cancel);
		var configured=RemoteListenerConfiguration.Load(); if(configured is not null) EnsureListening(configured);
		try { await Task.Delay(Timeout.Infinite,stoppingToken); } catch(OperationCanceledException) { }
	}
	internal object EnsureConfigured(string registryPath) { var configured=RemoteListenerConfiguration.FromRegistry(registryPath); EnsureListening(configured); lock(sync) return new { address=configured.Address.ToString(),plaintext_port=configured.PlaintextPort,tls_port=configured.TlsPort,listening=listeners.Keys.OrderBy(value=>value,StringComparer.Ordinal).ToArray() }; }
	void EnsureListening(RemoteListenerConfiguration configured) {
		var required=new HashSet<string>(StringComparer.Ordinal);
		if(configured.PlaintextPort is int plaintext) Ensure(configured.Address,plaintext,false,null,required);
		if(configured.TlsPort is int tls) Ensure(configured.Address,tls,true,configured.LoadCertificate(),required);
		KeyValuePair<string,ListenerState>[] retired; lock(sync) { retired=listeners.Where(pair=>!required.Contains(pair.Key)).ToArray(); foreach(var item in retired) listeners.Remove(item.Key); }
		foreach(var item in retired) item.Value.Dispose();
	}
	void Ensure(IPAddress address,int port,bool useTls,X509Certificate2? certificate,HashSet<string> required) {
		var key=$"{address}:{port}:{useTls}"; required.Add(key); lock(sync) { if(listeners.ContainsKey(key)) { certificate?.Dispose(); return; } var listener=new TcpListener(address,port); listener.Start(16); var state=new ListenerState(listener,certificate); listeners.Add(key,state); state.Task=ListenAsync(state,useTls,shutdown.Token); }
	}
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
}

sealed class RemoteListenerConfiguration {
	public IPAddress Address { get; } public int? PlaintextPort { get; } public int? TlsPort { get; } readonly string? certificateFile,passwordFile;
	RemoteListenerConfiguration(IPAddress address,int? plaintextPort,int? tlsPort,string? certificateFile,string? passwordFile) { Address=address; PlaintextPort=plaintextPort; TlsPort=tlsPort; this.certificateFile=certificateFile; this.passwordFile=passwordFile; }
	public static RemoteListenerConfiguration? Load() {
		var addressText=Environment.GetEnvironmentVariable("DGSPY_REMOTE_ADDRESS");
		if(!string.IsNullOrWhiteSpace(addressText)) return FromEnvironment(addressText);
		var registry=Environment.GetEnvironmentVariable("DGSPY_HOSTS_FILE"); return string.IsNullOrWhiteSpace(registry) || !File.Exists(registry) ? null : FromRegistry(registry);
	}
	static RemoteListenerConfiguration FromEnvironment(string addressText) {
		var address=ParseAddress(addressText); int? plaintext=string.Equals(Environment.GetEnvironmentVariable("DGSPY_REMOTE_DISABLE_PLAINTEXT"),"true",StringComparison.OrdinalIgnoreCase) ? null : int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_PORT"),out var port) ? port : 7352;
		var tls=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_TLS_PORT"),out var tlsPort) ? tlsPort : (int?)null;
		return new RemoteListenerConfiguration(address,plaintext,tls,Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE"),Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE"));
	}
	public static RemoteListenerConfiguration FromRegistry(string path) {
		var root=JsonNode.Parse(File.ReadAllText(path))!.AsObject(); var listener=root["listener"]?.AsObject() ?? throw new InvalidOperationException("The host registry has outbound hosts but no persisted listener configuration.");
		var address=ParseAddress((string?)listener["address"] ?? ""); var hosts=root["hosts"]?.AsArray() ?? new JsonArray(); int? plaintext=hosts.Any(node=>(string?)node?["transport"]=="outbound") ? (int?)listener["plaintext_port"] ?? 7352 : null; var hasTls=hosts.Any(node=>(string?)node?["transport"]=="outbound_tls"); var tlsNode=root["tls"]?.AsObject(); int? tls=hasTls ? (int?)tlsNode?["port"] ?? 7353 : null; var baseDirectory=Path.GetDirectoryName(Path.GetFullPath(path))!;
		return new RemoteListenerConfiguration(address,plaintext,tls,Resolve(baseDirectory,(string?)tlsNode?["server_certificate_file"]),Resolve(baseDirectory,(string?)tlsNode?["server_certificate_password_file"]));
	}
	public X509Certificate2 LoadCertificate() { if(TlsPort is null) throw new InvalidOperationException("TLS is not configured."); if(string.IsNullOrWhiteSpace(certificateFile)||string.IsNullOrWhiteSpace(passwordFile)||!File.Exists(certificateFile)||!File.Exists(passwordFile)) throw new InvalidOperationException("The Gateway TLS certificate and password files are required for the TLS listener."); return new X509Certificate2(certificateFile,File.ReadAllText(passwordFile).Trim(),X509KeyStorageFlags.DefaultKeySet); }
	static IPAddress ParseAddress(string value) => IPAddress.TryParse(value,out var address) ? address : throw new InvalidOperationException("The persisted remote listener address must be an IP address assigned to this Gateway.");
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
