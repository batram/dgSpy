using System.Net;
using System.Net.Sockets;
using System.Text;
using dgSpy.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dgSpy.Gateway;

/// <summary>Authenticated client for one extension endpoint. The ping discovers and verifies the stable
/// host identity before the caller's operation is sent on the same connection.</summary>
public sealed class LocalRpcClient {
	readonly int rpcPort;
	readonly string rpcToken;
	readonly string? configuredHostId;

	public LocalRpcClient() : this(
		int.TryParse(Environment.GetEnvironmentVariable("DGSPY_RPC_PORT"),out var port) ? port : 7351,
		RpcClientSettings.LoadToken(),
		Environment.GetEnvironmentVariable("DGSPY_HOST_ID")) { }

	internal LocalRpcClient(int rpcPort,string rpcToken,string? configuredHostId) {
		this.rpcPort=rpcPort;
		this.rpcToken=rpcToken;
		this.configuredHostId=string.IsNullOrWhiteSpace(configuredHostId) ? null : configuredHostId.Trim();
	}

	public async Task<RpcResponse> CallAsync(RpcRequest request,CancellationToken cancellationToken) {
		using var deadline=new CancellationTokenSource();
		if (request.DeadlineUtc is DateTime deadlineUtc) deadline.CancelAfter(deadlineUtc-DateTime.UtcNow > TimeSpan.Zero ? deadlineUtc-DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
		using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
		cancellationToken=linked.Token;
		using var client=new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback,rpcPort,cancellationToken);
		using var stream=client.GetStream();
		using var writer=new StreamWriter(stream,new UTF8Encoding(false),4096,true) { AutoFlush=true };
		using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true);

		var ping=new RpcRequest {
			Operation="ping", HostId=configuredHostId, AuthenticationToken=rpcToken,
			DeadlineUtc=DateTime.UtcNow.AddSeconds(3),
		};
		await writer.WriteLineAsync(JsonConvert.SerializeObject(ping));
		var handshakeLine=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe during handshake.");
		var response=JsonConvert.DeserializeObject<RpcResponse>(handshakeLine) ?? throw new IOException("Invalid dgSpy handshake response.");
		if (response.Version!=ProtocolVersion.Current || response.Error is not null)
			throw new IOException(response.Error?.Message ?? $"dgSpy protocol mismatch: expected {ProtocolVersion.Current}, received {response.Version}.");
		var handshake=(response.Result as JObject)?.ToObject<Handshake>() ?? throw new IOException("The dgSpy extension returned an invalid handshake.");
		RpcClientSettings.EnsureExpectedHost(configuredHostId,handshake.HostId);

		request.HostId=handshake.HostId;
		request.AuthenticationToken=rpcToken;
		await writer.WriteLineAsync(JsonConvert.SerializeObject(request));
		var line=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe.");
		return JsonConvert.DeserializeObject<RpcResponse>(line) ?? throw new IOException("Invalid RPC response.");
	}
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
