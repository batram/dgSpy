using System.Net;
using System.Net.Sockets;
using System.Text;
using dgSpy.Gateway;
using dgSpy.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["DGSPY_URL"] ?? "http://127.0.0.1:7350");
builder.Services.AddSingleton<LocalRpcClient>();
var app = builder.Build();

// Clients authenticate with a local secret. Take it from the environment when set, otherwise mint
// one and write it where a local client can read it — never derive it from anything guessable.
var token = Environment.GetEnvironmentVariable("DGSPY_TOKEN");
var tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dgSpy", "gateway.token");
if (string.IsNullOrEmpty(token)) {
	token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
	Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
	File.WriteAllText(tokenFile, token);
	Console.WriteLine($"dgSpy gateway token written to {tokenFile}");
}
Console.WriteLine($"dgSpy gateway listening; send it as the {RequestGuard.TokenHeader} header.");

app.MapGet("/health", () => Results.Json(new { status="ok", protocol_version=ProtocolVersion.Current }));
app.MapPost("/mcp", async (HttpContext http, LocalRpcClient rpc, CancellationToken cancellationToken) => {
	var rejection = RequestGuard.Reject(http.Request.Headers.Origin, http.Request.Headers[RequestGuard.TokenHeader], token, http.Connection.RemoteIpAddress);
	if (rejection is not null) return Results.Json(new { jsonrpc="2.0", id=(JToken?)null, error=new { code=-32600, message=rejection } }, statusCode: StatusCodes.Status403Forbidden);
	using var reader = new StreamReader(http.Request.Body);
	var root = JObject.Parse(await reader.ReadToEndAsync(cancellationToken));
	var id = root["id"];
	try {
		var method = (string?)root["method"];
		if (method == "initialize") return Results.Json(new { jsonrpc="2.0", id, result=new { protocolVersion="2025-03-26", capabilities=new { tools=new {} }, serverInfo=new { name="dgSpy", version="0.1.0" } } });
		if (method == "notifications/initialized") return Results.NoContent();
		if (method == "tools/list") return Results.Json(new { jsonrpc="2.0", id, result=new { tools=ToolCatalog.All } });
		if (method != "tools/call") return McpError(id, -32601, "Method not found");
		var name=(string?)root["params"]?["name"] ?? ""; var args=(JObject?)root["params"]?["arguments"] ?? new JObject();
		var response=await rpc.CallAsync(new RpcRequest { Operation=name, Arguments=args, DeadlineUtc=DateTime.UtcNow.AddSeconds(name=="wait_for_stop" ? 12 : 8) }, cancellationToken);
		if (response.Error is not null) return Results.Json(new { jsonrpc="2.0", id, result=new { isError=true, structuredContent=new { error=response.Error }, content=new[] { new { type="text", text=response.Error.Message } } } });
		return Results.Json(new { jsonrpc="2.0", id, result=new { structuredContent=response.Result, content=new[] { new { type="text", text=JsonConvert.SerializeObject(response.Result) } } } });
	} catch (Exception ex) { return McpError(id, -32603, ex.Message); }
});
app.Run();
static IResult McpError(JToken? id, int code, string message) => Results.Json(new { jsonrpc="2.0", id, error=new { code, message } });

sealed class LocalRpcClient {
	readonly int rpcPort=int.TryParse(Environment.GetEnvironmentVariable("DGSPY_RPC_PORT"),out var value) ? value : 7351;
	public async Task<RpcResponse> CallAsync(RpcRequest request, CancellationToken cancellationToken) {
		using var deadline=new CancellationTokenSource();
		if (request.DeadlineUtc is DateTime deadlineUtc) deadline.CancelAfter(deadlineUtc-DateTime.UtcNow > TimeSpan.Zero ? deadlineUtc-DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
		using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,deadline.Token);
		cancellationToken=linked.Token;
		using var client=new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback,rpcPort,cancellationToken);
		using var stream=client.GetStream();
		using var writer=new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush=true };
		using var reader=new StreamReader(stream, Encoding.UTF8, false, 4096, true);
		var handshake=new RpcRequest { Operation="ping", DeadlineUtc=DateTime.UtcNow.AddSeconds(3) };
		await writer.WriteLineAsync(JsonConvert.SerializeObject(handshake));
		var handshakeLine=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe during handshake.");
		var handshakeResponse=JsonConvert.DeserializeObject<RpcResponse>(handshakeLine) ?? throw new IOException("Invalid dgSpy handshake response.");
		if (handshakeResponse.Version!=ProtocolVersion.Current || handshakeResponse.Error is not null) throw new IOException(handshakeResponse.Error?.Message ?? $"dgSpy protocol mismatch: expected {ProtocolVersion.Current}, received {handshakeResponse.Version}.");
		await writer.WriteLineAsync(JsonConvert.SerializeObject(request));
		var line=await reader.ReadLineAsync(cancellationToken) ?? throw new IOException("The dgSpy extension closed the pipe.");
		return JsonConvert.DeserializeObject<RpcResponse>(line) ?? throw new IOException("Invalid RPC response.");
	}
}
static class ToolCatalog {
	static object Tool(string name, string description, object properties, string[]? required=null) => new { name, description, inputSchema=new { type="object", properties, required=required ?? Array.Empty<string>() } };
	public static readonly object[] All = {
		Tool("list_programs", "List dnSpy-attachable managed runtimes. Unfiltered enumeration probes every process on the machine and takes seconds; pass process_ids or process_names when the target is known. Each call replaces the set of valid program_id values.", new { process_ids=new { type="array", items=new { type="integer" }, description="Only these PIDs." }, process_names=new { type="array", items=new { type="string" }, description="Process names, wildcards * and ? allowed, eg. UltimateChickenHorse*." } }),
		Tool("attach", "Attach using a program_id returned by list_programs.", new { program_id=new { type="string" } }, new[]{"program_id"}),
		Tool("get_session_state", "Get current debugger session state.", new { session_id=new { type="string" } }, new[]{"session_id"}),
		Tool("list_sessions", "List active debugger sessions. Use this to recover a session_id.", new {}),
		Tool("detach", "Detach from the target, leaving it running. This is the only safe way to end a session: closing dnSpy with a session attached terminates the target. Fails with detach_would_terminate if dnSpy cannot detach cleanly.", new { session_id=new { type="string" }, allow_terminate=new { type="boolean", description="Stop debugging even if that terminates the target." } }, new[]{"session_id"}),
		Tool("pause", "Pause the debugger session.", new { session_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("continue", "Continue the debugger session.", new { session_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("set_il_breakpoint", "Set an exact module/token/IL-offset breakpoint.", new { session_id=new { type="string" }, module=new { type="string" }, method_token=new { type="integer" }, il_offset=new { type="integer" } }, new[]{"session_id","module","method_token","il_offset"}),
		Tool("wait_for_stop", "Wait for a stop event after an event cursor.", new { session_id=new { type="string" }, after_event_id=new { type="integer" }, timeout_ms=new { type="integer", minimum=1, maximum=60000 } }, new[]{"session_id"}),
		Tool("get_callstack", "Get paused call stack and primitive locals.", new { session_id=new { type="string" }, max_frames=new { type="integer", minimum=1, maximum=100 } }, new[]{"session_id"})
	};
}
