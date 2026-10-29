using System.Globalization;
using System.Text;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Injector;

/// <summary>Recovers the authenticated endpoint record written by watcher generations that retained
/// their protected resident payload directory but predated the shared discovery store. The directory
/// is evidence that prevents a blind second injection; only a successful authenticated health check
/// promotes that evidence into an adoptable discovery record.</summary>
public sealed class LegacyResidentRecovery {
	readonly ProbeDiscoveryStore discovery;
	readonly ResidentPayloadStore payloads;
	public LegacyResidentRecovery(string? stateRoot=null) { discovery=new ProbeDiscoveryStore(stateRoot); payloads=new ResidentPayloadStore(stateRoot); }

	public IReadOnlyList<ProbeDiscoveryRecord> FindOrRecover(TargetIdentity expected,ILiveTargetIdentity liveTargets) {
		var existing=discovery.DiscoverStrict(liveTargets,DateTime.UtcNow).Where(value=>SameTarget(value.Target,expected)).ToArray();
		if(existing.Length!=0) return existing;
		var candidates=payloads.Find(expected.ProcessId,expected.ProcessCreationTimeUtc.ToUniversalTime().Ticks);
		if(candidates.Count==0) return Array.Empty<ProbeDiscoveryRecord>();
		var recovered=new List<ProbeDiscoveryRecord>(); var failures=new List<string>(); var arrivals=new List<string>();
		foreach(var directory in candidates) {
			try { recovered.Add(Recover(directory,expected)); }
			catch(FailedArrivalException ex) { arrivals.Add(Path.GetFileName(directory)+": "+ex.Message); }
			catch(Exception ex) { failures.Add(Path.GetFileName(directory)+": "+ex.Message); }
		}
		// A resident that reported its own failure is not an endpoint we failed to authenticate: it never
		// had one. Its payloads are loaded and cannot be rolled back, so the refusal stands either way,
		// but the useful answer is who injected it and why it failed - typically a watcher rule.
		if(recovered.Count==0&&arrivals.Count!=0) throw new UnregisteredResidentException("A HookLab resident was already injected into this exact process and its initialization failed, leaving its payloads loaded with no endpoint to adopt. Refusing a second injection; restart the target, and fix or remove the rule that injected it. "+String.Join("; ",arrivals.Concat(failures)));
		if(recovered.Count==0) throw new UnregisteredResidentException("Live HookLab resident staging exists for this exact process, but no endpoint could be authenticated. Refusing a second injection. "+String.Join("; ",failures));
		if(recovered.Count>1) throw new UnregisteredResidentException("Multiple legacy HookLab residents authenticated for this exact process; refusing ambiguous adoption.");
		discovery.Write(recovered[0]);
		return recovered.AsReadOnly();
	}

	ProbeDiscoveryRecord Recover(string directory,TargetIdentity expected) {
		var parameters=Parse(Path.Combine(directory,"initialize.params")); var completion=Parse(Path.Combine(directory,"completion.txt"));
		if(completion.TryGetValue("status",out var status)&&status!="ok") throw new FailedArrivalException(Describe(parameters,completion,status));
		Required(parameters,"endpoint","pipe"); Required(completion,"status","ok");
		var host=Value(parameters,"host_id"); if(host!=DgSpyStateRoot.ResidentHostId&&host!="apply-once") throw new InvalidDataException("Legacy host_id is not recognized.");
		var target=new TargetIdentity(host,Value(parameters,"image_path"),Int32.Parse(Value(parameters,"process_id"),CultureInfo.InvariantCulture),new DateTime(Int64.Parse(Value(parameters,"process_creation_utc_ticks"),CultureInfo.InvariantCulture),DateTimeKind.Utc),Value(parameters,"architecture"),Value(parameters,"runtime_id"),Value(parameters,"appdomain_id"));
		if(!SameTarget(target,expected)) throw new InvalidDataException("Legacy staging target identity does not match the live target.");
		var namedCompletion=Path.GetFullPath(Value(parameters,"completion_path")); if(!String.Equals(namedCompletion,Path.GetFullPath(Path.Combine(directory,"completion.txt")),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Legacy completion path escapes its resident staging directory.");
		var secret=Convert.FromBase64String(Value(parameters,"endpoint_secret_base64")); if(secret.Length!=32) throw new InvalidDataException("Legacy endpoint secret length is invalid.");
		var nonce=Convert.FromBase64String(Value(completion,"pipe_nonce_base64")); if(nonce.Length!=32) throw new InvalidDataException("Legacy endpoint nonce length is invalid.");
		var record=new ProbeDiscoveryRecord(new TargetIdentity(DgSpyStateRoot.ResidentHostId,target.ImagePath,target.ProcessId,target.ProcessCreationTimeUtc,target.Architecture,target.RuntimeId,target.AppDomainId),Value(completion,"probe_instance_id"),Value(completion,"pipe_name"),nonce,secret,ProbeWireProtocol.ProtocolVersion,DateTime.UtcNow.Add(ProbeDiscoveryStore.DiscoveryRecordLifetime));
		var health=ProbeTransportClient.VerifyHealth(record,false,2000); _=ResidentInventoryParser.Parse(health.Status.PayloadJson,record.ProbeInstanceId); return record;
	}

	static Dictionary<string,string> Parse(string path) {
		if(!File.Exists(path)) throw new InvalidDataException("Required legacy resident file is missing: "+Path.GetFileName(path));
		var result=new Dictionary<string,string>(StringComparer.Ordinal); foreach(var line in File.ReadAllLines(path,Encoding.UTF8)) { var split=line.IndexOf('='); if(split<=0) continue; var key=line.Substring(0,split); if(result.ContainsKey(key)) throw new InvalidDataException("Duplicate legacy resident field: "+key); result.Add(key,line.Substring(split+1)); } return result;
	}
	static string Describe(Dictionary<string,string> parameters,Dictionary<string,string> completion,string status) {
		string Field(Dictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!String.IsNullOrWhiteSpace(value)?value:"unknown";
		return "injected by "+Field(parameters,"host_id")+(parameters.ContainsKey("hook_id")?" for hook "+Field(parameters,"hook_id"):"")+
			", status="+status+" at stage "+Field(completion,"stage")+": "+Field(completion,"error_type")+": "+Field(completion,"error_message")+
			" (payloads_resident="+Field(completion,"payloads_resident")+", endpoint_live="+Field(completion,"endpoint_live")+")";
	}
	sealed class FailedArrivalException : Exception { public FailedArrivalException(string message):base(message) { } }
	static string Value(Dictionary<string,string> values,string key)=>values.TryGetValue(key,out var value)&&!String.IsNullOrWhiteSpace(value)?value:throw new InvalidDataException("Legacy resident data omitted "+key+".");
	static void Required(Dictionary<string,string> values,string key,string expected) { if(Value(values,key)!=expected) throw new InvalidDataException("Legacy resident "+key+" is not "+expected+"."); }
	static bool SameTarget(TargetIdentity left,TargetIdentity right)=>left.ProcessId==right.ProcessId&&left.ProcessCreationTimeUtc.ToUniversalTime().Ticks==right.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&String.Equals(Path.GetFullPath(left.ImagePath),Path.GetFullPath(right.ImagePath),StringComparison.OrdinalIgnoreCase)&&left.Architecture==right.Architecture&&left.RuntimeId==right.RuntimeId&&left.AppDomainId==right.AppDomainId;
}

public sealed class UnregisteredResidentException : InvalidOperationException { public UnregisteredResidentException(string message):base(message) { } }
