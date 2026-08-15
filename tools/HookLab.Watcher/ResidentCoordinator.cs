using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using HookLab.ApplyOnce;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Watcher;

internal sealed class ResidentCoordinator {
	const string HostId="apply-once";
	readonly string? payloadDirectory; readonly ProbeDiscoveryStore store;
	public ResidentCoordinator(string? payloadDirectory,string? stateRoot=null) { this.payloadDirectory=payloadDirectory; store=new ProbeDiscoveryStore(stateRoot); }

	public string Apply(WatchWork work) {
		var expected=LiveIdentity.Read(work.Process.ProcessId);
		var records=store.DiscoverStrict(new SystemLiveTargets(),DateTime.UtcNow).Where(record=>SameTarget(record.Target,expected)).ToArray();
		if(records.Length>1) throw new InvalidOperationException("Multiple authenticated-resident records name the same live target; refusing ambiguous adoption.");
		if(records.Length==1) {
			var health=store.VerifyHealthAndRefresh(records[0],2000);
			EnsureExpectedPatch(health.Status.PayloadJson,records[0].ProbeInstanceId,work.Definition.Value.Id!);
			return "adopted";
		}

		var secret=ProbeAuthentication.CreateSecret();
		try {
			ResidentInjection injected;
			try { injected=OneShotInjector.ApplyResident(work.Process.ProcessId,work.Definition.Value,work.Definition.Path,payloadDirectory,secret); }
			catch(TargetExitedException) { return "target_exited"; }
			var target=new TargetIdentity(HostId,injected.ImagePath,work.Process.ProcessId,new DateTime(injected.CreationTicks,DateTimeKind.Utc),"x64","v4.0.30319","1");
			var record=new ProbeDiscoveryRecord(target,injected.ProbeInstanceId,injected.PipeName,injected.EndpointNonce,secret,1,DateTime.UtcNow.Add(ProbeDiscoveryStore.DiscoveryRecordLifetime));
			var health=ProbeTransportClient.VerifyHealth(record,false,2000);
			EnsureExpectedPatch(health.Status.PayloadJson,injected.ProbeInstanceId,work.Definition.Value.Id!);
			store.Write(record);
			return "installed";
		}
		finally { Array.Clear(secret,0,secret.Length); }
	}

	static void EnsureExpectedPatch(string payload,string probeInstanceId,string definitionId) {
		using var document=JsonDocument.Parse(payload); var root=document.RootElement;
		if(root.GetProperty("probe_instance_id").GetString()!=probeInstanceId) throw new InvalidOperationException("Authenticated resident status reported a different probe instance.");
		var expected=probeInstanceId+":"+definitionId;
		if(!root.GetProperty("patch_ids").EnumerateArray().Any(value=>value.GetString()==expected)) throw new InvalidOperationException("Authenticated resident does not contain expected patch "+expected+"; refusing reinjection.");
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
