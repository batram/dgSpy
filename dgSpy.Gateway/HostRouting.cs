using System.Net;
using System.Net.Sockets;
using System.Text;
using dgSpy.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dgSpy.Gateway;

public sealed class HostEndpoint {
	public string HostId { get; }
	public string DisplayName { get; }
	public IPAddress Address { get; }
	public int Port { get; }
	internal string Token { get; }

	internal HostEndpoint(string hostId,string displayName,IPAddress address,int port,string token) {
		HostId=hostId; DisplayName=displayName; Address=address; Port=port; Token=token;
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
		return string.IsNullOrWhiteSpace(path) ? Local() : FromJson(File.ReadAllText(path),Path.GetDirectoryName(Path.GetFullPath(path))!);
	}

	public static HostRegistry FromJson(string json,string baseDirectory) {
		var document=JsonConvert.DeserializeObject<HostRegistryDocument>(json) ?? throw new InvalidOperationException("The host registry is invalid JSON.");
		if (document.Hosts.Length==0) throw new InvalidOperationException("The host registry contains no hosts.");
		var endpoints=new List<HostEndpoint>();
		var ids=new HashSet<string>(StringComparer.Ordinal);
		foreach (var host in document.Hosts) {
			if (string.IsNullOrWhiteSpace(host.HostId)) throw new InvalidOperationException("Every registered host requires host_id.");
			if (!ids.Add(host.HostId)) throw new InvalidOperationException($"Duplicate host_id '{host.HostId}'.");
			if (!IPAddress.TryParse(host.Address,out var address) || !IPAddress.IsLoopback(address))
				throw new InvalidOperationException($"Host '{host.HostId}' must use a loopback tunnel endpoint, not '{host.Address}'.");
			if (host.Port is <1 or >65535) throw new InvalidOperationException($"Host '{host.HostId}' has an invalid port.");
			var token=ResolveToken(host,baseDirectory);
			endpoints.Add(new HostEndpoint(host.HostId,host.DisplayName ?? host.HostId,address,host.Port,token));
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
		var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
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

	sealed class HostRegistryDocument { [JsonProperty("hosts")] public HostRegistration[] Hosts { get; set; }=Array.Empty<HostRegistration>(); }
	sealed class HostRegistration {
		[JsonProperty("host_id")] public string HostId { get; set; }="";
		[JsonProperty("display_name")] public string? DisplayName { get; set; }
		[JsonProperty("address")] public string Address { get; set; }="127.0.0.1";
		[JsonProperty("port")] public int Port { get; set; }
		[JsonProperty("token_environment")] public string? TokenEnvironment { get; set; }
		[JsonProperty("token_file")] public string? TokenFile { get; set; }
	}
}

public sealed class HostRouter {
	readonly HostRegistry registry;
	readonly Dictionary<string,EndpointRpcClient> clients;
	public HostRouter() : this(HostRegistry.Load()) { }
	internal HostRouter(HostRegistry registry) {
		this.registry=registry;
		clients=registry.Endpoints.ToDictionary(endpoint=>endpoint.HostId,endpoint=>new EndpointRpcClient(endpoint),StringComparer.Ordinal);
	}

	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		try {
			var requestedHostId=(string?)request.Arguments["host_id"];
			var endpoint=registry.Select(requestedHostId);
			request.Arguments.Remove("host_id");
			return await clients[endpoint.HostId].CallAsync(request,cancellationToken);
		}
		catch (HostRoutingException ex) { return RpcResponse.Failure(request.RequestId,ex.Code,ex.Message); }
	}

	public async Task<object[]> ListHostsAsync(CancellationToken cancellationToken) {
		var hosts=new List<object>();
		foreach (var endpoint in registry.Endpoints.OrderBy(endpoint=>endpoint.HostId,StringComparer.Ordinal)) {
			try {
				var response=await clients[endpoint.HostId].CallAsync(new RpcRequest { Operation="get_host_info",DeadlineUtc=DateTime.UtcNow.AddSeconds(5) },cancellationToken);
				hosts.Add(new { host_id=endpoint.HostId,display_name=endpoint.DisplayName,state=response.Error is null ? "connected" : "error",host=response.Result,error=response.Error });
			}
			catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException) {
				hosts.Add(new { host_id=endpoint.HostId,display_name=endpoint.DisplayName,state="unavailable",host=(object?)null,error=new RpcError { Code="host_unavailable",Message=ex.Message } });
			}
		}
		return hosts.ToArray();
	}
}

sealed class EndpointRpcClient {
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
		await writer.WriteLineAsync(JsonConvert.SerializeObject(ping));
		var handshakeLine=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe during handshake.");
		var response=JsonConvert.DeserializeObject<RpcResponse>(handshakeLine) ?? throw new IOException("Invalid dgSpy handshake response.");
		if (response.Version!=ProtocolVersion.Current || response.Error is not null)
			throw new IOException(response.Error?.Message ?? $"dgSpy protocol mismatch: expected {ProtocolVersion.Current}, received {response.Version}.");
		var handshake=(response.Result as JObject)?.ToObject<Handshake>() ?? throw new IOException("The dgSpy extension returned an invalid handshake.");
		RpcClientSettings.EnsureExpectedHost(endpoint.HostId,handshake.HostId);
		request.HostId=handshake.HostId; request.AuthenticationToken=endpoint.Token;
		await writer.WriteLineAsync(JsonConvert.SerializeObject(request));
		var line=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe.");
		return JsonConvert.DeserializeObject<RpcResponse>(line) ?? throw new IOException("Invalid RPC response.");
	}
}

public sealed class HostRoutingException : Exception {
	public string Code { get; }
	public HostRoutingException(string code,string message) : base(message) { Code=code; }
}

public static class RpcClientSettings {
	public static string LoadToken() {
		var configured=Environment.GetEnvironmentVariable("DGSPY_RPC_TOKEN");
		if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
		var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy","rpc.token");
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
