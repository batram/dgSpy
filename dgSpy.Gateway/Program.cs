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
		var response=await rpc.CallAsync(new RpcRequest { Operation=name, Arguments=args, DeadlineUtc=DateTime.UtcNow.AddSeconds(ToolCatalog.DeadlineSeconds(name)) }, cancellationToken);
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
public static class ToolCatalog {
	static object Tool(string name, string description, object properties, string[]? required=null) => new { name, description, inputSchema=new { type="object", properties, required=required ?? Array.Empty<string>() } };
	/// <summary>Margin between the extension's own bound for an operation and the gateway's deadline for
	/// it. The gateway deadline must outlast the inner bound, or the gateway abandons work that was about
	/// to succeed — a flat 8 s once cut off a successful 10 s attach.</summary>
	public const int MarginSeconds = 5;
	/// <summary>Derived from the extension's advertised bound rather than guessed. CapabilityCatalog is
	/// the single source of truth for both sides; get_capabilities serves the same table to callers.</summary>
	public static int DeadlineSeconds(string tool) {
		var bound=CapabilityCatalog.BoundMs(tool);
		return bound<=0 ? 8 : (int)Math.Ceiling(bound/1000.0)+MarginSeconds;
	}
	public static readonly object[] All = {
		Tool("get_host_info", "Identify the dnSpy host this gateway talks to: versions, machine, architecture, supported engines, and whether a session is live.", new {}),
		Tool("get_capabilities", "Report what this host supports before relying on it: per-operation time bounds, per-engine behavior (notably that Mono/Unity binds breakpoints only at sequence points), limits, and the complete event_kinds and stop_reasons vocabularies. Engine differences are advertised here rather than assumed.", new {}),
		Tool("list_programs", "List dnSpy-attachable managed runtimes. Unfiltered enumeration probes every process on the machine and takes seconds; pass process_ids or process_names when the target is known. Each call replaces the set of valid program_id values.", new { process_ids=new { type="array", items=new { type="integer" }, description="Only these PIDs." }, process_names=new { type="array", items=new { type="string" }, description="Process names, wildcards * and ? allowed, eg. UltimateChickenHorse*." }, provider_names=new { type="array", items=new { type="string" }, description="dnSpy attach providers to consult: DotNetFramework, DotNet, UnityEditor, UnityPlayer. Naming providers skips the rest. UnityPlayer runs a multicast scan and is skipped entirely unless named." } }),
		Tool("attach", "Attach using a program_id returned by list_programs.", new { program_id=new { type="string" } }, new[]{"program_id"}),
		Tool("attach_endpoint", "Attach to a Mono/Unity soft-debugger endpoint by address and port. Use this when the target was launched with --debugger-agent=transport=dt_socket,server=y,address=HOST:PORT: such a target emits no discovery beacon, so list_programs can never see it and attach cannot reach it. Returns state \"faulted\" with fault_message when the connection fails.", new { address=new { type="string", description="Default 127.0.0.1." }, port=new { type="integer", minimum=1, maximum=65535 }, engine=new { type="string", @enum=new[]{"unity","mono"}, description="Default unity. Must match the target's runtime." }, process_is_suspended=new { type="boolean", description="True when the agent was given suspend=y, ie. the target is parked waiting for a debugger." }, connection_timeout_ms=new { type="integer", description="Socket retry window. dnSpy's default is 10000; capped at 300000." } }, new[]{"port"}),
		Tool("launch", "Launch a target through dnSpy's debugger engine. Unlike attach, dnSpy owns the process, so terminate and restart have deterministic semantics.", new { filename=new { type="string" }, engine=new { type="string", @enum=new[]{"cordebug","unity"}, description="Default cordebug." }, command_line=new { type="string" }, working_directory=new { type="string" }, break_at=new { type="string", @enum=new[]{"none","create_process","entry_point"} }, environment=new { type="object", additionalProperties=new { type="string" } }, connection_timeout_ms=new { type="integer", minimum=1, maximum=300000 } }, new[]{"filename"}),
		Tool("get_session_state", "Get current debugger session state.", new { session_id=new { type="string" } }, new[]{"session_id"}),
		Tool("list_sessions", "List active debugger sessions. Use this to recover a session_id.", new {}),
		Tool("detach", "Detach from the target, leaving it running. This is the only safe way to end a session: closing dnSpy with a session attached terminates the target. Fails with detach_would_terminate if dnSpy cannot detach cleanly.", new { session_id=new { type="string" }, allow_terminate=new { type="boolean", description="Stop debugging even if that terminates the target." } }, new[]{"session_id"}),
		Tool("terminate", "Terminate the active target explicitly. This is intentionally separate from detach, which preserves an attached target.", new { session_id=new { type="string" } }, new[]{"session_id"}),
		Tool("restart", "Restart a target previously launched through dgSpy. Attached targets cannot be restarted because dgSpy does not own their launch options.", new { session_id=new { type="string" } }, new[]{"session_id"}),
		Tool("pause", "Pause the debugger session.", new { session_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("continue", "Continue the debugger session.", new { session_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("set_il_breakpoint", "Set an exact module/token/IL-offset breakpoint and report whether the engine actually bound it. Check `bound`: false with severity \"error\" will never be hit, false with no error is pending a module load. On Mono/Unity a breakpoint can only sit on a sequence point; many offsets qualify but a frame's own il_offset often does not, and a refused one is retried at method entry (offset 0) unless snap_to_sequence_point is false — when that happens `snapped` is true and it stops earlier than requested. Pass the returned `cursor_event_id` to wait_for_stop as after_event_id: a cursor read after this call has already missed the first hit on a hot method.", new { session_id=new { type="string" }, module=new { type="string" }, method_token=new { type="integer" }, il_offset=new { type="integer" }, snap_to_sequence_point=new { type="boolean", description="Default true. Retry at method entry when the engine refuses the requested offset." } }, new[]{"session_id","module","method_token","il_offset"}),
		Tool("list_breakpoints", "List all dnSpy breakpoints with their binding state. Breakpoints are global and outlive a session.", new {}),
		Tool("remove_breakpoint", "Remove one breakpoint by the exact id returned by set_il_breakpoint or list_breakpoints. Unlike clear_breakpoints, this leaves every other dnSpy and UI breakpoint untouched.", new { breakpoint_id=new { type="integer" } }, new[]{"breakpoint_id"}),
		Tool("clear_breakpoints", "Remove every dnSpy breakpoint, including any set by hand in the dnSpy UI. Breakpoints outlive a detach and rebind on the next attach, so clear them before reattaching if a fresh session should not stop on old ones.", new {}),
		Tool("wait_for_stop", "Wait without polling for the next normalized stop after an event cursor. Concurrent waiters do not consume events. A truncated result supplies oldest_available_cursor so the caller can recover.", new { session_id=new { type="string" }, after_event_id=new { type="integer" }, timeout_ms=new { type="integer", minimum=1, maximum=10000, description="Default 5000. The extension clamps to 10000; poll again with the returned cursor for longer waits." } }, new[]{"session_id"}),
		Tool("wait_for_event", "Wait without polling for any requested event kinds after a cursor. Returns immediately when a matching buffered event already exists; concurrent waiters are non-destructive.", new { session_id=new { type="string" }, after_event_id=new { type="integer" }, kinds=new { type="array", items=new { type="string", @enum=EventKinds.All }, description="Optional normalized event-kind filter. An unrecognized kind is rejected with invalid_argument rather than silently matching nothing." }, timeout_ms=new { type="integer", minimum=1, maximum=10000, description="Default 5000. The extension clamps to 10000." } }, new[]{"session_id"}),
		Tool("get_events", "Read normalized lifecycle and debugger events without consuming them. Optional kinds filter the bounded per-session stream; truncated reports when the requested cursor is older than retained history.", new { session_id=new { type="string" }, after_event_id=new { type="integer" }, kinds=new { type="array", items=new { type="string", @enum=EventKinds.All }, description="Optional normalized event-kind filter. An unrecognized kind is rejected with invalid_argument rather than silently matching nothing." } }, new[]{"session_id"}),
		Tool("get_stop_reason", "Read the latest normalized stop, or an exact retained stop event by event_id, including process, thread, breakpoint, exception, and location details when supplied by the debugger. stop_reason is one of: breakpoint, exception, step, entry_point, program_break, pause, unknown.", new { session_id=new { type="string" }, event_id=new { type="integer" } }, new[]{"session_id"}),
		Tool("list_threads", "List paused target threads with stable thread_id values. It does not eagerly fetch every stack because Unity threads can exit during frame retrieval; select a returned thread_id with get_callstack or get_frame.", new { session_id=new { type="string" } }, new[]{"session_id"}),
		Tool("get_callstack", "Get a paused call stack and primitive locals. Pass thread_id for deterministic caller-selected inspection; omitting it preserves the managed-frame probe fallback.", new { session_id=new { type="string" }, thread_id=new { type="string" }, max_frames=new { type="integer", minimum=1, maximum=100 } }, new[]{"session_id"}),
		Tool("get_frame", "Inspect one paused frame and its primitive locals by caller-selected thread_id and zero-based frame_index.", new { session_id=new { type="string" }, thread_id=new { type="string" }, frame_index=new { type="integer", minimum=0, maximum=99 } }, new[]{"session_id","thread_id","frame_index"}),
		Tool("update_breakpoint", "Change an existing breakpoint's enabled state, condition, hit count, or trace message. Conditions and hit counts are evaluated by the engine inside the target, so a condition that cannot be evaluated fails at hit time rather than here. Pass an empty string for condition or trace_message to clear it; omit a field to leave it unchanged.", new { breakpoint_id=new { type="integer" }, enabled=new { type="boolean" }, condition=new { type="string", description="Expression in the target's language. Empty string clears the condition." }, condition_kind=new { type="string", @enum=BreakpointConditionKinds.All, description="Default is_true. when_changed stops when the value differs from the previous hit." }, hit_count=new { type="integer", minimum=1 }, hit_count_kind=new { type="string", @enum=HitCountKinds.All, description="Default equals (stop on exactly the nth hit)." }, trace_message=new { type="string", description="Print instead of (or as well as) stopping. Empty string clears." }, trace_continue=new { type="boolean", description="Default true: print and keep running, which produces NO stopped event. Pass false to print and stop." } }, new[]{"breakpoint_id"}),
		Tool("set_exception_breakpoint", "Stop when an exception is thrown. Pass a fully qualified type name, or omit name to set the category's default that governs every exception it does not name. Like code breakpoints these are dnSpy-global and outlive a session.", new { category=new { type="string", description="Default DotNet." }, name=new { type="string", description="Fully qualified type, eg. System.InvalidOperationException. Omit for the category default." }, stop_first_chance=new { type="boolean", description="Stop when thrown, before the target gets to handle it." }, stop_second_chance=new { type="boolean", description="Stop when it goes unhandled." } }),
		Tool("list_exception_breakpoints", "List exceptions configured to break on throw. By default this reports first-chance entries only, which is the set someone deliberately configured: dnSpy stops on SECOND chance for essentially every .NET exception it knows, so including those returns thousands of identical stock entries that say nothing about this session. Bounded, with total and truncated.", new { include_second_chance=new { type="boolean", description="Default false. True adds dnSpy's stock second-chance defaults — expect thousands of entries." }, max_results=new { type="integer", minimum=1, maximum=1000, description="Default 200." } }),
		Tool("step_into", "Step into the call at the current instruction. The stop arrives on the event stream like any other, with stop_reason \"step\": pass the returned cursor_event_id to wait_for_stop. completed=false means the step is still running, not that it failed.", new { session_id=new { type="string" }, thread_id=new { type="string", description="Defaults to the thread that carried the stop. Stepping the wrong thread resumes the target and stops somewhere unrelated, so pass this explicitly when several threads are interesting." }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("step_over", "Step over the call at the current instruction. See step_into for how completion is reported.", new { session_id=new { type="string" }, thread_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"}),
		Tool("step_out", "Run to the caller of the current frame. See step_into for how completion is reported.", new { session_id=new { type="string" }, thread_id=new { type="string" }, expected_state_version=new { type="integer" } }, new[]{"session_id"})
	};
}
