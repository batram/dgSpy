using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Injector;

public sealed class ResidentCoordinator {
	const string HostId="apply-once";
	readonly string? payloadDirectory; readonly ProbeDiscoveryStore store; readonly ResidentPayloadStore payloads;
	readonly ConcurrentDictionary<(int ProcessId,long CreationTicks),object> gates=new();
	readonly object storeGate=new();
	public ResidentCoordinator(string? payloadDirectory,string? stateRoot=null) { this.payloadDirectory=payloadDirectory; store=new ProbeDiscoveryStore(stateRoot); payloads=new ResidentPayloadStore(stateRoot); payloads.CleanupExited(); }

	public InjectorResult Apply(InjectorRequest request) {
		lock(gates.GetOrAdd((request.ProcessId,request.ProcessCreationUtcTicks),_=>new object())) return ApplySerialized(request);
	}
	InjectorResult ApplySerialized(InjectorRequest request) {
		var digest=request.DefinitionSha256;
		var expected=LiveIdentity.Read(request.ProcessId);
		if(expected.ProcessCreationTimeUtc.ToUniversalTime().Ticks!=request.ProcessCreationUtcTicks) throw new InvalidOperationException("Target process identity changed before HookLab operation.");
		if(request.PermittedImagePath is not null&&!String.Equals(Path.GetFullPath(request.PermittedImagePath),Path.GetFullPath(expected.ImagePath),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Target image is outside the permitted executable path: "+expected.ImagePath);
		ProbeDiscoveryRecord[] records; lock(storeGate) records=store.DiscoverStrict(new SystemLiveTargets(),DateTime.UtcNow).Where(record=>SameTarget(record.Target,expected)).ToArray();
		if(records.Length>1) throw new InvalidOperationException("Multiple authenticated-resident records name the same live target; refusing ambiguous adoption.");
		if(records.Length==1) {
			ProbeHealthResult health; lock(storeGate) health=store.VerifyHealthAndRefresh(records[0],2000);
			return Reconcile(records[0],health.Status.PayloadJson,request,digest,"adopted");
		}

		var secret=ProbeAuthentication.CreateSecret();
		try {
			ResidentInjection injected;
			var staging=payloads.Create(request.ProcessId,request.ProcessCreationUtcTicks);
			try { injected=OneShotInjector.ApplyResident(request.ProcessId,request.Definition,request.DefinitionPath,payloadDirectory,secret,staging,request.ClrReadinessTimeoutMs,request.InitializationTimeoutMs); }
			catch(ClrReadinessTimeoutException) { payloads.Delete(staging); throw; }
			catch(TargetExitedException) { payloads.Delete(staging); return new("target_exited",digest,null,null,null,request.ProcessId,request.ProcessCreationUtcTicks,expected.ImagePath); }
			var target=new TargetIdentity(HostId,injected.ImagePath,request.ProcessId,new DateTime(injected.CreationTicks,DateTimeKind.Utc),"x64","v4.0.30319","1");
			var record=new ProbeDiscoveryRecord(target,injected.ProbeInstanceId,injected.PipeName,injected.EndpointNonce,secret,1,DateTime.UtcNow.Add(ProbeDiscoveryStore.DiscoveryRecordLifetime));
			var health=ProbeTransportClient.VerifyHealth(record,false,2000);
			lock(storeGate) store.Write(record);
			return Reconcile(record,health.Status.PayloadJson,request,digest,"installed");
		}
		finally { Array.Clear(secret,0,secret.Length); }
	}

	InjectorResult Reconcile(ProbeDiscoveryRecord record,string payload,InjectorRequest request,string digest,string unchangedStatus) {
		var status=ParseStatus(payload,record.ProbeInstanceId); var definition=request.Definition; var patchId=record.ProbeInstanceId+":"+definition.Id; var observed=status.Hooks.SingleOrDefault(value=>value.PatchId==patchId);
		string disposition;
		if(observed is null) { Mutate(record,"install_compiled_prefix",Parameters(request),status.HooksVersion); disposition="created"; }
		else {
			EnsureSameTarget(observed,definition);
			var sourceDigest=SourceDigest(definition.Hook!.Source!);
			if(observed.Revision==definition.Hook.Revision&&!String.Equals(observed.SourceSha256,sourceDigest,StringComparison.Ordinal)) throw new InvalidOperationException("Resident hook has the requested revision with different source; refusing conflict.");
			if(observed.Revision>definition.Hook.Revision) throw new InvalidOperationException("Resident hook revision is newer than desired state; refusing downgrade.");
			if(observed.Revision<definition.Hook.Revision) { Mutate(record,"install_compiled_prefix",Parameters(request),status.HooksVersion); disposition="updated"; }
			else disposition=unchangedStatus;
		}
		status=ReadStatus(record); observed=status.Hooks.Single(value=>value.PatchId==patchId);
		if(observed.Enabled!=definition.Hook!.Enabled) { Mutate(record,definition.Hook.Enabled?"enable":"disable",patchId,status.HooksVersion); disposition=definition.Hook.Enabled?"enabled":"disabled"; status=ReadStatus(record); observed=status.Hooks.Single(value=>value.PatchId==patchId); }
		EnsureSameTarget(observed,definition); if(observed.Revision!=definition.Hook.Revision||!String.Equals(observed.SourceSha256,SourceDigest(definition.Hook.Source!),StringComparison.Ordinal)||observed.Enabled!=definition.Hook.Enabled) throw new InvalidOperationException("Resident readback does not match desired hook state; refusing retry.");
		return new(disposition,digest,record.ProbeInstanceId,patchId,status.HooksVersion,request.ProcessId,request.ProcessCreationUtcTicks,record.Target.ImagePath);
	}
	public IReadOnlyList<ResidentStatusResult> Status(int? processId=null) {
		ProbeDiscoveryRecord[] records; lock(storeGate) records=store.DiscoverStrict(new SystemLiveTargets(),DateTime.UtcNow).Where(record=>processId is null||record.Target.ProcessId==processId.Value).ToArray();
		if(processId is not null&&records.Length==0) throw new InvalidOperationException("No authenticated HookLab resident was found for PID "+processId.Value+".");
		if(processId is not null&&records.Length>1) throw new InvalidOperationException("Multiple authenticated HookLab residents were found for PID "+processId.Value+".");
		var result=new List<ResidentStatusResult>(); foreach(var record in records) { ProbeHealthResult health; lock(storeGate) health=store.VerifyHealthAndRefresh(record,2000); var status=ParseStatus(health.Status.PayloadJson,record.ProbeInstanceId); result.Add(new(record.Target.ProcessId,record.Target.ProcessCreationTimeUtc.ToUniversalTime().Ticks,record.Target.ImagePath,record.ProbeInstanceId,status.HooksVersion,status.Hooks)); }
		return result;
	}
	static void Mutate(ProbeDiscoveryRecord record,string operation,string payload,long version) { try { ProbeTransportClient.Send(record,operation,payload,version,5000); } catch(TimeoutException) { _=ReadStatus(record); } }
	static ResidentStatusResult ReadStatus(ProbeDiscoveryRecord record)=>ParseStatus(ProbeTransportClient.VerifyHealth(record,false,2000).Status.PayloadJson,record.ProbeInstanceId);
	static string Parameters(InjectorRequest request) { var target=LiveIdentity.Read(request.ProcessId); return OneShotInjector.Parameters(target.ImagePath,target.ProcessId,target.ProcessCreationTimeUtc.Ticks,Path.Combine(Path.GetTempPath(),"hooklab-resident-unused"),request.Definition); }
	static string SourceDigest(string source) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(); }
	static void EnsureSameTarget(ResidentHookStatus observed,HookDefinition desired) { var target=desired.Target!; if(observed.Kind!=desired.Hook!.Kind||observed.ModuleMvid!=target.ModuleMvid||observed.MetadataToken!=target.MetadataToken||observed.DeclaringType!=target.DeclaringType||observed.Signature!=target.Signature||observed.IlSha256!=target.IlSha256) throw new InvalidOperationException("Resident hook id names a different exact target; refusing conflict."); }
	static ResidentStatusResult ParseStatus(string payload,string probeInstanceId) { using var document=JsonDocument.Parse(payload); var root=document.RootElement; if(root.GetProperty("probe_instance_id").GetString()!=probeInstanceId) throw new InvalidOperationException("Authenticated resident status reported a different probe instance."); var hooks=root.GetProperty("compiled_hooks").EnumerateArray().Select(value=>new ResidentHookStatus(value.GetProperty("patch_id").GetString()!,value.GetProperty("kind").GetString()!,value.GetProperty("module_mvid").GetString()!,value.GetProperty("metadata_token").GetInt32(),value.GetProperty("declaring_type").GetString()!,value.GetProperty("signature").GetString()!,value.GetProperty("il_sha256").GetString()!,value.GetProperty("source_sha256").GetString()!,value.GetProperty("revision").GetInt32(),value.GetProperty("enabled").GetBoolean())).ToArray(); return new(0,0,"",probeInstanceId,root.GetProperty("hooks_version").GetInt64(),hooks); }

	static bool SameTarget(TargetIdentity left,TargetIdentity right)=>left.HostId==HostId&&left.ProcessId==right.ProcessId&&left.ProcessCreationTimeUtc.ToUniversalTime().Ticks==right.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&String.Equals(Path.GetFullPath(left.ImagePath),Path.GetFullPath(right.ImagePath),StringComparison.OrdinalIgnoreCase)&&left.Architecture==right.Architecture&&left.RuntimeId==right.RuntimeId&&left.AppDomainId==right.AppDomainId;

	sealed class SystemLiveTargets : ILiveTargetIdentity,ILiveTargetLiveness {
		public bool IsAlive(TargetIdentity identity) {
			try { using var process=Process.GetProcessById(identity.ProcessId); return process.StartTime.ToUniversalTime().Ticks==identity.ProcessCreationTimeUtc.ToUniversalTime().Ticks; }
			catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } catch(Win32Exception) { return false; }
		}
		public bool IsCurrent(TargetIdentity identity) {
			try {
				var actual=LiveIdentity.Read(identity.ProcessId);
				return identity.ProcessCreationTimeUtc.ToUniversalTime().Ticks==actual.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&String.Equals(Path.GetFullPath(identity.ImagePath),Path.GetFullPath(actual.ImagePath),StringComparison.OrdinalIgnoreCase)&&identity.Architecture==actual.Architecture&&identity.RuntimeId==actual.RuntimeId&&identity.AppDomainId==actual.AppDomainId;
			}
			catch(ArgumentException) { return false; }
			catch(InvalidOperationException ex) { throw new LiveTargetInspectionUnavailableException("Live target identity could not be inspected; preserving its discovery record.",ex); }
			catch(Win32Exception ex) { throw new LiveTargetInspectionUnavailableException("Live target identity could not be inspected; preserving its discovery record.",ex); }
		}
	}

	static class LiveIdentity {
		public static TargetIdentity Read(int processId) { using var process=Process.GetProcessById(processId); return new TargetIdentity(HostId,process.MainModule?.FileName??throw new InvalidOperationException("Target image is unavailable."),process.Id,process.StartTime.ToUniversalTime(),"x64","v4.0.30319","1"); }
	}
}
