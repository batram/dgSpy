using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class Program {
	static readonly string StateRoot=Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy");
	static readonly Uri Endpoint=new(Environment.GetEnvironmentVariable("DGSPY_URL") ?? "http://127.0.0.1:7350/mcp");
	static readonly Uri Health=new(Endpoint,"/health");
	public static async Task<int> Main(string[] args) {
		try {
			var command=args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
			return command switch {
				"mcp" => await RunMcpAsync(), "start" => await StartAsync(), "stop" => Stop(), "status" => await StatusAsync(), "doctor" => await CallAndPrintAsync("doctor",new()),
				"configure" => Configure(args.Skip(1).ToArray()), "launch-local" or "deploy-local" => await CallAndPrintAsync("launch_local_host",new()), "uninstall-local" => await CallAndPrintAsync("uninstall_local_deployment",new JsonObject{{"confirm",true},{"remove_settings",args.Contains("--remove-settings")}}),
				"pack-host" => await PackHostAsync(args.Skip(1).ToArray()), "revoke-host" => await RevokeHostAsync(args.Skip(1).ToArray()), _ => Help()
			};
		} catch(Exception ex) { Console.Error.WriteLine($"dgspy: {ex.Message}"); return 1; }
	}
	static int Help() { Console.WriteLine("dgspy mcp|start|stop|status|doctor|configure <codex|claude|generic> [--apply]|launch-local|uninstall-local [--remove-settings]|pack-host --host-id ID --gateway-address IP [--plaintext] [--output PATH]|revoke-host --host-id ID"); return 0; }
	static async Task<int> StartAsync() { if(await HealthyAsync()) { Console.WriteLine("dgSpy Gateway is already healthy."); return 0; } StartGateway(); if(!await WaitHealthyAsync()) throw new InvalidOperationException($"Gateway did not become healthy. See {Path.Combine(StateRoot,"gateway-cli.log")}"); Console.WriteLine($"dgSpy Gateway started at {Endpoint}."); Console.WriteLine("URL-based MCP clients may need an MCP reconnect or a new session before tools appear."); return 0; }
	static void StartGateway() { Directory.CreateDirectory(StateRoot); var (file,arguments)=GatewayCommand(); var start=new ProcessStartInfo(file,arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true }; start.Environment["DGSPY_STATE_ROOT"]=StateRoot; start.Environment["DGSPY_URL"]=Endpoint.GetLeftPart(UriPartial.Authority); start.Environment["Logging__LogLevel__Default"]="Warning"; var packageRoot=Environment.GetEnvironmentVariable("DGSPY_PACKAGE_ROOT") ?? Path.Combine(StateRoot,"packages"); var registry=Path.Combine(packageRoot,"gateway-hosts.json"); if(File.Exists(registry)) { start.Environment["DGSPY_HOSTS_FILE"]=registry; start.Environment["DGSPY_INCLUDE_LOCAL_HOST"]="true"; } var process=Process.Start(start) ?? throw new InvalidOperationException("Gateway process did not start."); File.WriteAllText(Path.Combine(StateRoot,"gateway.pid"),process.Id.ToString()); var log=Path.Combine(StateRoot,"gateway-cli.log"); process.OutputDataReceived+=(_,e)=>{ if(e.Data is not null) TryAppend(log,e.Data); }; process.ErrorDataReceived+=(_,e)=>{ if(e.Data is not null) TryAppend(log,e.Data); }; process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
	static (string,string) GatewayCommand() { var configured=Environment.GetEnvironmentVariable("DGSPY_GATEWAY_PATH"); if(!string.IsNullOrWhiteSpace(configured)) return configured.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?("dotnet",Quote(configured)):(configured,""); var packaged=Path.Combine(AppContext.BaseDirectory,"dgSpy.Gateway.exe"); if(File.Exists(packaged)) return (packaged,""); var legacyPackaged=Path.Combine(AppContext.BaseDirectory,"gateway","dgSpy.Gateway.exe"); if(File.Exists(legacyPackaged)) return (legacyPackaged,""); var sibling=Path.Combine(AppContext.BaseDirectory,"dgSpy.Gateway.dll"); if(File.Exists(sibling)) return ("dotnet",Quote(sibling)); for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent) { var candidate=Path.Combine(directory.FullName,"dgSpy.Gateway","bin","Release","net10.0","dgSpy.Gateway.dll"); if(File.Exists(candidate)) return ("dotnet",Quote(candidate)); } throw new InvalidOperationException("Gateway executable not found. Set DGSPY_GATEWAY_PATH or publish the CLI with the Gateway."); }
	static int Stop() { var path=Path.Combine(StateRoot,"gateway.pid"); if(!File.Exists(path)||!int.TryParse(File.ReadAllText(path),out var id)) { Console.WriteLine("No managed Gateway PID is recorded."); return 0; } try { var process=Process.GetProcessById(id); process.Kill(true); process.WaitForExit(5000); } catch(ArgumentException) { } File.Delete(path); Console.WriteLine("dgSpy Gateway stopped. Debug targets were not resumed, detached, or terminated."); return 0; }
	static async Task<int> StatusAsync() { Console.WriteLine(JsonSerializer.Serialize(new { healthy=await HealthyAsync(),endpoint=Endpoint.ToString(),state_root=StateRoot,pid=ReadPid() },JsonOptions)); return await HealthyAsync()?0:1; }
	static int? ReadPid() { var path=Path.Combine(StateRoot,"gateway.pid"); return File.Exists(path)&&int.TryParse(File.ReadAllText(path),out var id)?id:null; }

	static async Task<int> RunMcpAsync() { if(!await HealthyAsync()) { StartGateway(); if(!await WaitHealthyAsync()) throw new InvalidOperationException("Gateway did not become healthy."); } using var client=Client(); string? session=null; while(await Console.In.ReadLineAsync() is { } line) { if(string.IsNullOrWhiteSpace(line)) continue; using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint) { Content=new StringContent(line,Encoding.UTF8,"application/json") }; if(session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id",session); request.Headers.TryAddWithoutValidation("MCP-Protocol-Version",ProtocolVersion(line)); var response=await client.SendAsync(request); if(response.Headers.TryGetValues("Mcp-Session-Id",out var values)) session=values.FirstOrDefault(); var body=await response.Content.ReadAsStringAsync(); if(!string.IsNullOrWhiteSpace(body)) { await Console.Out.WriteLineAsync(body); await Console.Out.FlushAsync(); } } return 0; }
	static string ProtocolVersion(string json) { try { return JsonNode.Parse(json)?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-11-25"; } catch { return "2025-11-25"; } }
	static HttpClient Client() { var client=new HttpClient { Timeout=TimeSpan.FromSeconds(330) }; var tokenPath=Path.Combine(StateRoot,"gateway.token"); if(File.Exists(tokenPath)) client.DefaultRequestHeaders.TryAddWithoutValidation("X-dgSpy-Token",File.ReadAllText(tokenPath).Trim()); return client; }
	static async Task<bool> HealthyAsync() { try { using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(1) }; var response=await client.GetAsync(Health); if(!response.IsSuccessStatusCode) return false; var json=JsonNode.Parse(await response.Content.ReadAsStringAsync()); return (string?)json?["status"]=="ok" && ((int?)json?["protocol_version"] ?? 0)>0; } catch { return false; } }
	static async Task<bool> WaitHealthyAsync() { for(var i=0;i<40;i++) { if(await HealthyAsync()) return true; await Task.Delay(250); } return false; }

	static int Configure(string[] args) { var client=args.FirstOrDefault()?.ToLowerInvariant() ?? throw new InvalidOperationException("configure requires codex, claude, or generic."); var process=Environment.ProcessPath ?? "dgspy.exe"; var frameworkDependent=Path.GetFileNameWithoutExtension(process).Equals("dotnet",StringComparison.OrdinalIgnoreCase); var executable=process; var commandArgs=frameworkDependent?new[]{System.Reflection.Assembly.GetExecutingAssembly().Location,"mcp"}:new[]{"mcp"}; var tomlArgs=string.Join(", ",commandArgs.Select(value=>JsonSerializer.Serialize(value))); var snippet=client switch { "codex"=>$"[mcp_servers.dgspy]\ncommand = '{executable.Replace("'","''")}'\nargs = [{tomlArgs}]\n", "claude"=>$"{{\"mcpServers\":{{\"dgspy\":{{\"command\":{JsonSerializer.Serialize(executable)},\"args\":{JsonSerializer.Serialize(commandArgs)}}}}}}}", "generic"=>$"command: {executable} {string.Join(" ",commandArgs.Select(Quote))}", _=>throw new InvalidOperationException("configure supports codex, claude, or generic.") };
		if(client=="codex"&&args.Contains("--apply")) { var config=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex","config.toml"); Directory.CreateDirectory(Path.GetDirectoryName(config)!); var content=File.Exists(config)?File.ReadAllText(config):""; content=Regex.Replace(content,@"(?ms)^\[mcp_servers\.dgspy\]\s*.*?(?=^\[|\z)","").TrimEnd()+Environment.NewLine+Environment.NewLine+snippet; File.WriteAllText(config,content,new UTF8Encoding(false)); Console.WriteLine($"Updated {config}. Restart Codex or reconnect MCP."); } else Console.WriteLine(snippet); return 0; }
	static async Task<int> PackHostAsync(string[] args) { var request=new JsonObject{{"host_id",Required(args,"--host-id")},{"gateway_address",Required(args,"--gateway-address")},{"use_tls",!args.Contains("--plaintext")}}; AddOption(request,"output_root",args,"--output"); return await CallAndPrintAsync("create_remote_host_package",request); }
	static Task<int> RevokeHostAsync(string[] args) => CallAndPrintAsync("revoke_remote_host",new JsonObject{{"host_id",Required(args,"--host-id")},{"confirm",true}});
	static void AddOption(JsonObject target,string name,string[] args,string option) { var value=Optional(args,option); if(value is not null) target[name]=value; }
	static string Required(string[] args,string option) => Optional(args,option) ?? throw new InvalidOperationException($"{option} is required.");
	static string? Optional(string[] args,string option) { var index=Array.IndexOf(args,option); return index>=0&&index+1<args.Length?args[index+1]:null; }
	static async Task<int> CallAndPrintAsync(string tool,JsonObject arguments) { var result=await CallAsync(tool,arguments); Console.WriteLine(result?.ToJsonString(JsonOptions)); return 0; }
	static async Task<JsonNode?> CallAsync(string tool,JsonObject arguments) {
		if(!await HealthyAsync()) await StartAsync();
		using var client=Client();
		var initializeBody=new JsonObject {
			["jsonrpc"]="2.0",["id"]=1,["method"]="initialize",
			["params"]=new JsonObject { ["protocolVersion"]="2025-11-25",["capabilities"]=new JsonObject(),["clientInfo"]=new JsonObject { ["name"]="dgspy-cli",["version"]="0.2.0" } }
		};
		var initialize=await PostAsync(client,initializeBody);
		var callBody=new JsonObject { ["jsonrpc"]="2.0",["id"]=2,["method"]="tools/call",["params"]=new JsonObject { ["name"]=tool,["arguments"]=arguments } };
		var called=await PostAsync(client,callBody,initialize.Session);
		var result=JsonNode.Parse(called.Body)?["result"];
		if((bool?)result?["isError"]==true) throw new InvalidOperationException((string?)result?["content"]?[0]?["text"] ?? "Tool failed.");
		return result?["structuredContent"];
	}
	static async Task<(string Body,string? Session)> PostAsync(HttpClient client,JsonObject body,string? session=null) { using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint) { Content=JsonContent.Create(body) }; request.Headers.TryAddWithoutValidation("MCP-Protocol-Version","2025-11-25"); if(session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id",session); var response=await client.SendAsync(request); var text=await response.Content.ReadAsStringAsync(); if(!response.IsSuccessStatusCode) throw new InvalidOperationException($"Gateway returned {(int)response.StatusCode}: {text}"); return (text,response.Headers.TryGetValues("Mcp-Session-Id",out var values)?values.FirstOrDefault():session); }
	static void TryAppend(string path,string line) { try { File.AppendAllText(path,$"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}"); } catch { } }
	static string Quote(string value) => "\""+value.Replace("\"","\\\"")+"\"";
	static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
}
