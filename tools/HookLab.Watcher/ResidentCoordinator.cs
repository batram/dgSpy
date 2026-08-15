using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using HookLab.ApplyOnce;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Watcher;

internal sealed class ResidentCoordinator {
	const string HostId="apply-once";
	readonly string? payloadDirectory; readonly ProbeDiscoveryStore store; readonly ResidentPayloadStore payloads;
	readonly ConcurrentDictionary<(int ProcessId,long CreationTicks),object> gates=new();
	readonly object storeGate=new();
	public ResidentCoordinator(string? payloadDirectory,string? stateRoot=null) { this.payloadDirectory=payloadDirectory; store=new ProbeDiscoveryStore(stateRoot); payloads=new ResidentPayloadStore(stateRoot); payloads.CleanupExited(); }

	public WatchApplyResult Apply(WatchWork work) {
		lock(gates.GetOrAdd((work.Process.ProcessId,work.Process.CreationUtcTicks),_=>new object())) return ApplySerialized(work);
	}
	WatchApplyResult ApplySerialized(WatchWork work) {
		var digest=work.Definition.DefinitionSha256;
		var expected=LiveIdentity.Read(work.Process.ProcessId);
		ProbeDiscoveryRecord[] records; lock(storeGate) records=store.DiscoverStrict(new SystemLiveTargets(),DateTime.UtcNow).Where(record=>SameTarget(record.Target,expected)).ToArray();
		if(records.Length>1) throw new InvalidOperationException("Multiple authenticated-resident records name the same live target; refusing ambiguous adoption.");
		if(records.Length==1) {
			ProbeHealthResult health; lock(storeGate) health=store.VerifyHealthAndRefresh(records[0],2000);
			return Reconcile(records[0],health.Status.PayloadJson,work,digest,"adopted");
		}

		var secret=ProbeAuthentication.CreateSecret();
		try {
			ResidentInjection injected;
			var staging=payloads.Create(work.Process);
			try { injected=OneShotInjector.ApplyResident(work.Process.ProcessId,work.Definition.Value,work.Definition.Path,payloadDirectory,secret,staging); }
			catch(TargetExitedException) { payloads.Delete(staging); return new("target_exited",digest,null,null,null); }
			var target=new TargetIdentity(HostId,injected.ImagePath,work.Process.ProcessId,new DateTime(injected.CreationTicks,DateTimeKind.Utc),"x64","v4.0.30319","1");
			var record=new ProbeDiscoveryRecord(target,injected.ProbeInstanceId,injected.PipeName,injected.EndpointNonce,secret,1,DateTime.UtcNow.Add(ProbeDiscoveryStore.DiscoveryRecordLifetime));
			var health=ProbeTransportClient.VerifyHealth(record,false,2000);
			lock(storeGate) store.Write(record);
			return Reconcile(record,health.Status.PayloadJson,work,digest,"installed");
		}
		finally { Array.Clear(secret,0,secret.Length); }
	}

	WatchApplyResult Reconcile(ProbeDiscoveryRecord record,string payload,WatchWork work,string digest,string unchangedStatus) {
		var status=ResidentStatus.Parse(payload,record.ProbeInstanceId); var definition=work.Definition.Value; var patchId=record.ProbeInstanceId+":"+definition.Id; var observed=status.Hooks.SingleOrDefault(value=>value.PatchId==patchId);
		string disposition;
		if(observed is null) { Mutate(record,"install_compiled_prefix",Parameters(work),status.HooksVersion); disposition="created"; }
		else {
			EnsureSameTarget(observed,definition);
			var sourceDigest=SourceDigest(definition.Hook!.Source!);
			if(observed.Revision==definition.Hook.Revision&&!String.Equals(observed.SourceSha256,sourceDigest,StringComparison.Ordinal)) throw new InvalidOperationException("Resident hook has the requested revision with different source; refusing conflict.");
			if(observed.Revision>definition.Hook.Revision) throw new InvalidOperationException("Resident hook revision is newer than desired state; refusing downgrade.");
			if(observed.Revision<definition.Hook.Revision) { Mutate(record,"install_compiled_prefix",Parameters(work),status.HooksVersion); disposition="updated"; }
			else disposition=unchangedStatus;
		}
		status=ReadStatus(record); observed=status.Hooks.Single(value=>value.PatchId==patchId);
		if(observed.Enabled!=definition.Hook!.Enabled) { Mutate(record,definition.Hook.Enabled?"enable":"disable",patchId,status.HooksVersion); disposition=definition.Hook.Enabled?"enabled":"disabled"; status=ReadStatus(record); observed=status.Hooks.Single(value=>value.PatchId==patchId); }
		EnsureSameTarget(observed,definition); if(observed.Revision!=definition.Hook.Revision||!String.Equals(observed.SourceSha256,SourceDigest(definition.Hook.Source!),StringComparison.Ordinal)||observed.Enabled!=definition.Hook.Enabled) throw new InvalidOperationException("Resident readback does not match desired hook state; refusing retry.");
		return new(disposition,digest,record.ProbeInstanceId,patchId,status.HooksVersion);
	}
	static void Mutate(ProbeDiscoveryRecord record,string operation,string payload,long version) { try { ProbeTransportClient.Send(record,operation,payload,version,5000); } catch(TimeoutException) { _=ReadStatus(record); } }
	static ResidentStatus ReadStatus(ProbeDiscoveryRecord record)=>ResidentStatus.Parse(ProbeTransportClient.VerifyHealth(record,false,2000).Status.PayloadJson,record.ProbeInstanceId);
	static string Parameters(WatchWork work) { var target=LiveIdentity.Read(work.Process.ProcessId); return OneShotInjector.Parameters(target.ImagePath,target.ProcessId,target.ProcessCreationTimeUtc.Ticks,Path.Combine(Path.GetTempPath(),"hooklab-resident-unused"),work.Definition.Value); }
	static string SourceDigest(string source) { using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(); }
	static void EnsureSameTarget(ResidentHook observed,HookDefinition desired) { var target=desired.Target!; if(observed.Kind!=desired.Hook!.Kind||observed.ModuleMvid!=target.ModuleMvid||observed.MetadataToken!=target.MetadataToken||observed.DeclaringType!=target.DeclaringType||observed.Signature!=target.Signature||observed.IlSha256!=target.IlSha256) throw new InvalidOperationException("Resident hook id names a different exact target; refusing conflict."); }
	sealed record ResidentHook(string PatchId,string Kind,string ModuleMvid,int MetadataToken,string DeclaringType,string Signature,string IlSha256,string SourceSha256,int Revision,bool Enabled);
	sealed record ResidentStatus(long HooksVersion,IReadOnlyList<ResidentHook> Hooks) {
		public static ResidentStatus Parse(string payload,string probeInstanceId) { using var document=JsonDocument.Parse(payload); var root=document.RootElement; if(root.GetProperty("probe_instance_id").GetString()!=probeInstanceId) throw new InvalidOperationException("Authenticated resident status reported a different probe instance."); var hooks=root.GetProperty("compiled_hooks").EnumerateArray().Select(value=>new ResidentHook(value.GetProperty("patch_id").GetString()!,value.GetProperty("kind").GetString()!,value.GetProperty("module_mvid").GetString()!,value.GetProperty("metadata_token").GetInt32(),value.GetProperty("declaring_type").GetString()!,value.GetProperty("signature").GetString()!,value.GetProperty("il_sha256").GetString()!,value.GetProperty("source_sha256").GetString()!,value.GetProperty("revision").GetInt32(),value.GetProperty("enabled").GetBoolean())).ToArray(); return new(root.GetProperty("hooks_version").GetInt64(),hooks); }
	}

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
			catch(InvalidOperationException) { return false; }
			catch(Win32Exception) { return false; }
		}
	}

	static class LiveIdentity {
		public static TargetIdentity Read(int processId) { using var process=Process.GetProcessById(processId); return new TargetIdentity(HostId,process.MainModule?.FileName??throw new InvalidOperationException("Target image is unavailable."),process.Id,process.StartTime.ToUniversalTime(),"x64","v4.0.30319","1"); }
	}
}

internal sealed record WatchApplyResult(string Status,string DefinitionSha256,string? ProbeInstanceId,string? PatchId,long? HooksVersion);
