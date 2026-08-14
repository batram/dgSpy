using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using dgSpy.Protocol;

namespace dgSpy.Gateway;

/// <summary>Opt-in development traffic transcript, deliberately separate from the sparse security audit.</summary>
public sealed class GatewayDevelopmentTranscript {
	readonly object sync=new(); readonly string? path; readonly long maxBytes; readonly int maxPayloadBytes;
	public bool Enabled=>!string.IsNullOrWhiteSpace(path);
	public string? TranscriptPath=>path;
	public long MaxBytes=>maxBytes;
	public int MaxPayloadBytes=>maxPayloadBytes;
	public object Describe()=>new { enabled=Enabled,path=TranscriptPath,max_payload_bytes=MaxPayloadBytes,max_file_bytes=MaxBytes,rotated_path=Enabled?TranscriptPath+".1":null,query_tool="repository checkout: tools\\query-gateway-transcript.ps1" };
	public GatewayDevelopmentTranscript() : this(Environment.GetEnvironmentVariable("DGSPY_TRANSCRIPT_FILE"),ReadPositive("DGSPY_TRANSCRIPT_MAX_FILE_BYTES",5*1024*1024),ReadPositive("DGSPY_TRANSCRIPT_MAX_PAYLOAD_BYTES",32*1024)) { }
	internal GatewayDevelopmentTranscript(string? path,long maxBytes,int maxPayloadBytes) { this.path=string.IsNullOrWhiteSpace(path)?null:path; this.maxBytes=maxBytes; this.maxPayloadBytes=maxPayloadBytes; }
	public TranscriptCall Begin(object? jsonRpcId,string operation,JsonObject arguments,string? clientId)=>new(this,jsonRpcId,operation,arguments,clientId);
	internal void Write(TranscriptCall call,object? response,string outcome,string? errorCode) {
		if(!Enabled)return; var request=Bound(Redact(call.Arguments)); var result=Bound(Redact(ProtocolJson.ToNode(response)));
		var record=new { timestamp_utc=DateTime.UtcNow.ToString("O"),correlation_id=call.CorrelationId,json_rpc_id=call.JsonRpcId,gateway_build_label=GatewayBuild.BuildLabel,gateway_build_commit=GatewayBuild.BuildCommit,client_id=call.ClientId,host_id=(string?)call.Arguments["host_id"],session_id=(string?)call.Arguments["session_id"],operation=call.Operation,duration_ms=call.ElapsedMilliseconds,outcome,error_code=errorCode,request_payload=request.Payload,request_bytes=request.Bytes,request_truncated=request.Truncated,response_payload=result.Payload,response_bytes=result.Bytes,response_truncated=result.Truncated };
		var line=ProtocolJson.Serialize(record)+Environment.NewLine;
		try{lock(sync){var full=Path.GetFullPath(path!);Directory.CreateDirectory(Path.GetDirectoryName(full)!);if(File.Exists(full)&&new FileInfo(full).Length+Encoding.UTF8.GetByteCount(line)>maxBytes){var previous=full+".1";if(File.Exists(previous))File.Delete(previous);File.Move(full,previous);}File.AppendAllText(full,line,new UTF8Encoding(false));}}catch{ }
	}
	(object? Payload,int Bytes,bool Truncated) Bound(JsonNode? node){var json=node?.ToJsonString()??"null";var bytes=Encoding.UTF8.GetByteCount(json);if(bytes<=maxPayloadBytes)return(node,bytes,false);return(new JsonObject{{"truncated",true},{"preview",json.Substring(0,Math.Min(json.Length,Math.Max(0,maxPayloadBytes/2)))}},bytes,true);}
	internal static JsonNode? Redact(JsonNode? source){if(source is null)return null;var node=source.DeepClone();RedactInPlace(node);return node;}
	static void RedactInPlace(JsonNode node){if(node is JsonObject obj)foreach(var key in obj.Select(pair=>pair.Key).ToArray()){if(IsSecret(key))obj[key]="[REDACTED]";else if(obj[key] is JsonNode child)RedactInPlace(child);}else if(node is JsonArray array)foreach(var child in array)if(child is not null)RedactInPlace(child);}
	static bool IsSecret(string key){var value=key.Replace("-","_").ToLowerInvariant();return value is "token" or "rpc_token" or "access_token" or "auth_token" or "authorization" or "password" or "secret" or "api_key" or "x_dgspy_token" or "cookie";}
	static int ReadPositive(string name,int fallback)=>int.TryParse(Environment.GetEnvironmentVariable(name),out var value)&&value>0?value:fallback;
}

public sealed class TranscriptCall {
	readonly GatewayDevelopmentTranscript owner;readonly Stopwatch timer=Stopwatch.StartNew();int completed;
	internal TranscriptCall(GatewayDevelopmentTranscript owner,object? jsonRpcId,string operation,JsonObject arguments,string? clientId){this.owner=owner;JsonRpcId=jsonRpcId;Operation=operation;Arguments=(JsonObject)arguments.DeepClone();ClientId=clientId;CorrelationId=Guid.NewGuid().ToString("N");}
	public string CorrelationId{get;}public object? JsonRpcId{get;}public string Operation{get;}public JsonObject Arguments{get;}public string? ClientId{get;}public long ElapsedMilliseconds=>timer.ElapsedMilliseconds;
	public void Complete(object? response,string outcome,string? errorCode=null){if(Interlocked.Exchange(ref completed,1)==0)owner.Write(this,response,outcome,errorCode);}
}
