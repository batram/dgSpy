using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using dgSpy.Gateway;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class DevelopmentTranscriptTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-transcript-"+Guid.NewGuid().ToString("N"));
	public DevelopmentTranscriptTests()=>Directory.CreateDirectory(root);
	[Fact] public void Disabled_transcript_writes_nothing() { var transcript=new GatewayDevelopmentTranscript(null,1024,256); transcript.Begin(1,"list_modules",new JsonObject(),"client-a").Complete(new { modules=Array.Empty<object>() },"succeeded"); Assert.False(transcript.Enabled); Assert.Empty(Directory.GetFiles(root)); }
	[Fact] public void Correlates_and_recursively_redacts_request_and_response_tokens() {
		var path=Path.Combine(root,"redacted.jsonl");var transcript=new GatewayDevelopmentTranscript(path,64*1024,4096);
		var args=JsonNode.Parse("""{"host_id":"host-a","session_id":"session-a","token":"request-secret","nested":{"rpc_token":"nested-secret"},"method_token":123}""")!.AsObject();
		transcript.Begin("rpc-7","list_modules",args,"client-a").Complete(new { token="response-secret",access_token="access-secret",method_token=456,modules=Array.Empty<object>() },"succeeded");
		var text=File.ReadAllText(path);Assert.DoesNotContain("request-secret",text);Assert.DoesNotContain("nested-secret",text);Assert.DoesNotContain("response-secret",text);Assert.DoesNotContain("access-secret",text);
		var record=JsonNode.Parse(text)!.AsObject();Assert.Equal("[REDACTED]",(string?)record["request_payload"]?["token"]);Assert.Equal(123,(int?)record["request_payload"]?["method_token"]);Assert.Equal(456,(int?)record["response_payload"]?["method_token"]);Assert.Equal("rpc-7",(string?)record["json_rpc_id"]);Assert.Equal("client-a",(string?)record["client_id"]);Assert.Equal("host-a",(string?)record["host_id"]);Assert.Equal("session-a",(string?)record["session_id"]);Assert.NotNull(record["gateway_build_label"]);Assert.NotNull(record["correlation_id"]);
	}
	[Fact] public void Bounds_payloads_and_rotates_rejected_calls() {
		var path=Path.Combine(root,"rotate.jsonl");var transcript=new GatewayDevelopmentTranscript(path,750,80);
		for(var index=0;index<8;index++){var call=transcript.Begin(index,"get_csharp",new JsonObject{{"session_id","session-a"},{"expression",new string('x',500)}},"client-a");call.Complete(new { code=new string('y',500) },index==7?"rejected":"succeeded",index==7?"invalid_arguments":null);}
		Assert.True(File.Exists(path));Assert.True(File.Exists(path+".1"));var record=JsonNode.Parse(File.ReadLines(path).Last())!.AsObject();Assert.True((bool?)record["request_truncated"]);Assert.True((bool?)record["response_truncated"]);Assert.True((int?)record["request_bytes"]>80);Assert.Equal("rejected",(string?)record["outcome"]);Assert.Equal("invalid_arguments",(string?)record["error_code"]);
	}
	[Fact] public void Logging_failure_does_not_break_the_tool_call() { var blocker=Path.Combine(root,"not-a-directory");File.WriteAllText(blocker,"block");var transcript=new GatewayDevelopmentTranscript(Path.Combine(blocker,"transcript.jsonl"),1024,256);var error=Record.Exception(()=>transcript.Begin(1,"list_modules",new JsonObject(),"client-a").Complete(new { modules=Array.Empty<object>() },"succeeded"));Assert.Null(error); }
	public void Dispose(){try{Directory.Delete(root,true);}catch{ }}
}
