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
	const string HostId=DgSpyStateRoot.ResidentHostId;
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
		var status=ParseStatus(payload,record.ProbeInstanceId); var definition=request.Definition; var residentHookId=HookOwnership.Qualify(HookOwnership.WatcherController,definition.Id!); var patchId=record.ProbeInstanceId+":"+residentHookId; var legacyPatchId=record.ProbeInstanceId+":"+definition.Id; var observed=status.Hooks.SingleOrDefault(value=>value.PatchId==patchId)??status.Hooks.SingleOrDefault(value=>value.PatchId==legacyPatchId);
		string disposition;
		if(observed is null) { Mutate(record,"install_compiled_prefix",Parameters(request,residentHookId),status.HooksVersion); disposition="created"; }
		else {
			EnsureSameTarget(observed,definition,observed.PatchId==legacyPatchId);
			var sourceDigest=SourceDigest(definition.Hook!.Source!);
			if(observed.Revision==definition.Hook.Revision&&!String.Equals(observed.SourceSha256,sourceDigest,StringComparison.Ordinal)) throw new InvalidOperationException("Resident hook has the requested revision with different source; refusing conflict.");
			if(observed.Revision>definition.Hook.Revision) throw new InvalidOperationException("Resident hook revision is newer than desired state; refusing downgrade.");
			if(observed.Revision<definition.Hook.Revision) { if(observed.PatchId==legacyPatchId) { Mutate(record,"uninstall",legacyPatchId,status.HooksVersion); status=ReadStatus(record); } Mutate(record,"install_compiled_prefix",Parameters(request,residentHookId),status.HooksVersion); disposition="updated"; }
			else disposition=observed.PatchId==legacyPatchId?"adopted_legacy":unchangedStatus;
		}
		status=ReadStatus(record); observed=status.Hooks.SingleOrDefault(value=>value.PatchId==patchId)??status.Hooks.Single(value=>value.PatchId==legacyPatchId);
		if(observed.Enabled!=definition.Hook!.Enabled) { var activePatchId=observed.PatchId; Mutate(record,definition.Hook.Enabled?"enable":"disable",activePatchId,status.HooksVersion); disposition=definition.Hook.Enabled?"enabled":"disabled"; status=ReadStatus(record); observed=status.Hooks.Single(value=>value.PatchId==activePatchId); }
		EnsureSameTarget(observed,definition,observed.PatchId==legacyPatchId); if(observed.Revision!=definition.Hook.Revision||!String.Equals(observed.SourceSha256,SourceDigest(definition.Hook.Source!),StringComparison.Ordinal)||observed.Enabled!=definition.Hook.Enabled) throw new InvalidOperationException("Resident readback does not match desired hook state; refusing retry.");
		return new(disposition,digest,record.ProbeInstanceId,observed.PatchId,status.HooksVersion,request.ProcessId,request.ProcessCreationUtcTicks,record.Target.ImagePath);
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
	static string Parameters(InjectorRequest request,string residentHookId) { var target=LiveIdentity.Read(request.ProcessId); return OneShotInjector.Parameters(target.ImagePath,target.ProcessId,target.ProcessCreationTimeUtc.Ticks,Path.Combine(Path.GetTempPath(),"hooklab-resident-unused"),request.Definition,residentHookId:residentHookId); }
	// See OneShotInjector.Hex: Convert.ToHexString does not exist on net48, and this digest is compared
	// against resident state, so the replacement had to be proved identical rather than assumed.
	static string SourceDigest(string source) { using var sha=SHA256.Create(); return OneShotInjector.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(source))); }
	static void EnsureSameTarget(ResidentHookStatus observed,HookDefinition desired,bool legacy=false) { var target=desired.Target!; if((!legacy&&(observed.Controller!=HookOwnership.WatcherController||observed.HookId!=desired.Id))||(!String.IsNullOrEmpty(observed.AssemblySimpleName)&&!String.Equals(observed.AssemblySimpleName,target.Assembly,StringComparison.Ordinal))||observed.Kind!=desired.Hook!.Kind||observed.ModuleMvid!=target.ModuleMvid||observed.MetadataToken!=target.MetadataToken||observed.DeclaringType!=target.DeclaringType||observed.Signature!=target.Signature||observed.IlSha256!=target.IlSha256) throw new InvalidOperationException("Resident hook id names a different exact target or owner; refusing conflict."); }
	static ResidentStatusResult ParseStatus(string payload,string probeInstanceId) { using var document=JsonDocument.Parse(payload); var root=document.RootElement; if(root.GetProperty("probe_instance_id").GetString()!=probeInstanceId) throw new InvalidOperationException("Authenticated resident status reported a different probe instance."); var hooks=root.GetProperty("compiled_hooks").EnumerateArray().Select(value=>ParseHook(probeInstanceId,value)).ToArray(); return new(0,0,"",probeInstanceId,root.GetProperty("hooks_version").GetInt64(),hooks); }
	static ResidentHookStatus ParseHook(string probeInstanceId,JsonElement value) { var patchId=value.GetProperty("patch_id").GetString()!; HookOwnership.TryParse(probeInstanceId,patchId,out var controller,out var hookId); return new(patchId,controller,hookId,value.TryGetProperty("assembly_simple_name",out var assembly)?assembly.GetString()!:String.Empty,value.GetProperty("kind").GetString()!,value.GetProperty("module_mvid").GetString()!,value.GetProperty("metadata_token").GetInt32(),value.GetProperty("declaring_type").GetString()!,value.GetProperty("signature").GetString()!,value.GetProperty("il_sha256").GetString()!,value.GetProperty("source_sha256").GetString()!,value.GetProperty("revision").GetInt32(),value.GetProperty("enabled").GetBoolean()); }

	static bool SameTarget(TargetIdentity left,TargetIdentity right)=>(left.HostId==HostId||left.HostId=="apply-once")&&left.ProcessId==right.ProcessId&&left.ProcessCreationTimeUtc.ToUniversalTime().Ticks==right.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&String.Equals(Path.GetFullPath(left.ImagePath),Path.GetFullPath(right.ImagePath),StringComparison.OrdinalIgnoreCase)&&left.Architecture==right.Architecture&&left.RuntimeId==right.RuntimeId&&left.AppDomainId==right.AppDomainId;

	sealed class SystemLiveTargets : ILiveTargetIdentity,ILiveTargetLiveness {
		public bool IsAlive(TargetIdentity identity) {
			try { using var process=Process.GetProcessById(identity.ProcessId); return process.StartTime.ToUniversalTime().Ticks==identity.ProcessCreationTimeUtc.ToUniversalTime().Ticks; }
			catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } catch(Win32Exception) { return false; }
		}
		public bool IsCurrent(TargetIdentity identity) {
			try {
				var actual=LiveIdentity.Read(identity.ProcessId,identity.HostId);
				return identity.ProcessCreationTimeUtc.ToUniversalTime().Ticks==actual.ProcessCreationTimeUtc.ToUniversalTime().Ticks&&String.Equals(Path.GetFullPath(identity.ImagePath),Path.GetFullPath(actual.ImagePath),StringComparison.OrdinalIgnoreCase)&&identity.Architecture==actual.Architecture&&identity.RuntimeId==actual.RuntimeId&&identity.AppDomainId==actual.AppDomainId;
			}
			catch(ArgumentException) { return false; }
			catch(InvalidOperationException ex) { throw new LiveTargetInspectionUnavailableException("Live target identity could not be inspected; preserving its discovery record.",ex); }
			catch(Win32Exception ex) { throw new LiveTargetInspectionUnavailableException("Live target identity could not be inspected; preserving its discovery record.",ex); }
		}
	}

	static class LiveIdentity {
		public static TargetIdentity Read(int processId,string hostId=HostId) { using var process=Process.GetProcessById(processId); return new TargetIdentity(hostId,process.MainModule?.FileName??throw new InvalidOperationException("Target image is unavailable."),process.Id,process.StartTime.ToUniversalTime(),"x64","v4.0.30319","1"); }
	}
}
