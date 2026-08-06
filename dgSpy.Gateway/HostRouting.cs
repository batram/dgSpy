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
		var configured=FromJson(File.ReadAllText(path),Path.GetDirectoryName(Path.GetFullPath(path))!);
		if(!string.Equals(Environment.GetEnvironmentVariable("DGSPY_INCLUDE_LOCAL_HOST"),"true",StringComparison.OrdinalIgnoreCase)) return configured;
		var combined=configured.Endpoints.ToList(); var local=Local().Endpoints.Single(); if(combined.Any(endpoint=>endpoint.HostId==local.HostId)) throw new InvalidOperationException($"Configured host_id '{local.HostId}' conflicts with the managed local host."); combined.Add(local); return new HostRegistry(combined);
	}

	public static HostRegistry FromJson(string json,string baseDirectory) {
		var document=ProtocolJson.Deserialize<HostRegistryDocument>(json) ?? throw new InvalidOperationException("The host registry is invalid JSON.");
		if (document.Hosts.Length==0) throw new InvalidOperationException("The host registry contains no hosts.");
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
	readonly HostRegistry registry;
	readonly Dictionary<string,IHostRpcClient> clients;
	public HostRouter() : this(HostRegistry.Load()) { }
	internal HostRouter(HostRegistry registry) {
		this.registry=registry;
		clients=registry.Endpoints.ToDictionary(endpoint=>endpoint.HostId,endpoint=>endpoint.IsOutbound ? (IHostRpcClient)new RegisteredRpcClient(endpoint) : new EndpointRpcClient(endpoint),StringComparer.Ordinal);
	}
	internal bool TryAuthenticate(string hostId,string token,bool isTls,byte[]? clientCertificateHash,out string error) {
		if (!registry.TryGet(hostId,out var endpoint) || !endpoint.IsOutbound) { error="Unknown outbound host."; return false; }
		if (endpoint.RequiresTls!=isTls) { error=$"Host '{hostId}' requires {(endpoint.RequiresTls ? "TLS" : "plaintext")}."; return false; }
		if (endpoint.RequiresTls && (clientCertificateHash is null || endpoint.ClientCertificateHash is null || !RpcCredential.FixedTimeEquals(clientCertificateHash,endpoint.ClientCertificateHash))) { error="Client certificate does not match host_id."; return false; }
		if (!RpcCredential.FixedTimeEquals(token,endpoint.Token)) { error="Invalid host credential."; return false; }
		error=""; return true;
	}
	internal bool IsKnownClientCertificate(byte[] hash) => registry.Endpoints.Any(endpoint=>endpoint.RequiresTls && endpoint.ClientCertificateHash is not null && RpcCredential.FixedTimeEquals(hash,endpoint.ClientCertificateHash));
	internal bool TryRegister(string hostId,TcpClient client,StreamReader reader,StreamWriter writer,out string error) {
		if (!clients.TryGetValue(hostId,out var value) || value is not RegisteredRpcClient registered) { error="Unknown outbound host."; return false; }
		return registered.TryRegister(client,reader,writer,out error);
	}

	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		HostEndpoint? endpoint=null;
		try {
			var requestedHostId=(string?)request.Arguments["host_id"];
			endpoint=registry.Select(requestedHostId);
			request.Arguments.Remove("host_id");
			return await clients[endpoint.HostId].CallAsync(request,cancellationToken);
		}
		catch (HostRoutingException ex) { return RpcResponse.Failure(request.RequestId,ex.Code,ex.Message); }
		catch (OperationCanceledException) {
			return RpcResponse.Failure(request.RequestId,"deadline_exceeded","The debugger operation exceeded its deadline. Narrow the query or filters, then retry.");
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
		foreach (var endpoint in registry.Endpoints.OrderBy(endpoint=>endpoint.HostId,StringComparer.Ordinal)) {
			try {
				var response=await clients[endpoint.HostId].CallAsync(new RpcRequest { Operation="get_host_info",DeadlineUtc=DateTime.UtcNow.AddSeconds(5) },cancellationToken);
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
		await Task.WhenAll(clients.Values.Select(client=>SendHeartbeatAsync(client,cancellationToken)));
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

interface IHostRpcClient { Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken); }

sealed class EndpointRpcClient : IHostRpcClient {
	readonly HostEndpoint endpoint;
	public EndpointRpcClient(HostEndpoint endpoint) { this.endpoint=endpoint; }

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
	public bool TryRegister(TcpClient client,StreamReader reader,StreamWriter writer,out string error) {
		lock(sync) {
			if (connection is { IsAlive:true }) { error=$"Host '{endpoint.HostId}' already has a live connection."; return false; }
			connection?.Dispose(); connection=new ReverseConnection(client,reader,writer); error=""; return true;
		}
	}
	public Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		ReverseConnection? active; lock(sync) active=connection;
		if (active is null || !active.IsAlive) throw new IOException($"Host '{endpoint.HostId}' is not connected.");
		request.HostId=endpoint.HostId; request.AuthenticationToken=endpoint.Token;
		return active.CallAsync(request,cancellationToken);
	}
}

sealed class ReverseConnection : IDisposable {
	readonly TcpClient client; readonly StreamReader reader; readonly StreamWriter writer; readonly SemaphoreSlim calls=new(1,1);
	public bool IsAlive { get { try { return client.Connected && !(client.Client.Poll(0,SelectMode.SelectRead) && client.Available==0); } catch { return false; } } }
	public ReverseConnection(TcpClient client,StreamReader reader,StreamWriter writer) { this.client=client; this.reader=reader; this.writer=writer; }
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
	readonly HostRouter router; TcpListener? listener;
	public RemoteHostListener(HostRouter router) { this.router=router; }
	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		var addressText=Environment.GetEnvironmentVariable("DGSPY_REMOTE_ADDRESS");
		if (string.IsNullOrWhiteSpace(addressText)) return;
		if (!IPAddress.TryParse(addressText,out var address)) throw new InvalidOperationException("DGSPY_REMOTE_ADDRESS must be an IP address.");
		var tasks=new List<Task>();
		if (!string.Equals(Environment.GetEnvironmentVariable("DGSPY_REMOTE_DISABLE_PLAINTEXT"),"true",StringComparison.OrdinalIgnoreCase)) { var port=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_PORT"),out var configured) ? configured : 7352; tasks.Add(ListenAsync(address,port,false,null,stoppingToken)); }
		if (int.TryParse(Environment.GetEnvironmentVariable("DGSPY_REMOTE_TLS_PORT"),out var tlsPort)) { var certificateFile=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_FILE"); var passwordFile=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_SERVER_CERTIFICATE_PASSWORD_FILE"); if(string.IsNullOrWhiteSpace(certificateFile) || string.IsNullOrWhiteSpace(passwordFile)) throw new InvalidOperationException("The Gateway TLS certificate and password files are required for the TLS listener."); if(!File.Exists(certificateFile) || !File.Exists(passwordFile)) throw new InvalidOperationException("A configured Gateway TLS certificate or password file does not exist."); var password=File.ReadAllText(passwordFile).Trim(); tasks.Add(ListenAsync(address,tlsPort,true,new X509Certificate2(certificateFile,password,X509KeyStorageFlags.DefaultKeySet),stoppingToken)); }
		await Task.WhenAll(tasks);
	}
	async Task ListenAsync(IPAddress address,int port,bool useTls,X509Certificate2? serverCertificate,CancellationToken token) { var localListener=new TcpListener(address,port); if(!useTls) listener=localListener; localListener.Start(16); try { while(!token.IsCancellationRequested) { var client=await localListener.AcceptTcpClientAsync(token); _=RegisterAsync(client,useTls,serverCertificate,token); } } finally { localListener.Stop(); } }
	async Task RegisterAsync(TcpClient client,bool useTls,X509Certificate2? serverCertificate,CancellationToken token) {
		try {
			Stream stream=client.GetStream(); byte[]? clientCertificateHash=null;
			if(useTls) { var tls=new SslStream(stream,false,(_,certificate,__,___)=>certificate is not null && router.IsKnownClientCertificate(certificate.GetCertHash())); await tls.AuthenticateAsServerAsync(serverCertificate!,true,SslProtocols.Tls12,false); stream=tls; clientCertificateHash=tls.RemoteCertificate?.GetCertHash(); }
			var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true); var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true){AutoFlush=true};
			var line=await reader.ReadLineAsync(token); var request=line is null ? null : ProtocolJson.Deserialize<RpcRequest>(line); string error="Invalid registration.";
			if (request is null || request.Operation!="register_host" || request.Version!=ProtocolVersion.Current || string.IsNullOrWhiteSpace(request.HostId) || !router.TryAuthenticate(request.HostId,request.AuthenticationToken ?? "",useTls,clientCertificateHash,out error) || !router.TryRegister(request.HostId,client,reader,writer,out error)) {
				await writer.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Failure(request?.RequestId ?? "","registration_rejected",error))); client.Dispose(); return;
			}
			await writer.WriteLineAsync(ProtocolJson.Serialize(RpcResponse.Success(request.RequestId,new HostRegistration { HostId=request.HostId })));
		} catch { client.Dispose(); }
	}
	public override void Dispose() { listener?.Stop(); base.Dispose(); }
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
