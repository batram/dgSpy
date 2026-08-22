using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HookLab.Contracts;

namespace HookLab.Injector {
	/// <summary>The resident state every controller consumes after an authenticated status response.
	/// Controller-specific session and UI state deliberately do not belong here.</summary>
	public sealed class ResidentInventory {
		public ResidentInventory(string probeInstanceId,long hooksVersion,IReadOnlyList<ResidentInventoryHook> hooks,IReadOnlyList<ShadowedHookState> shadowedHooks) {
			ProbeInstanceId=probeInstanceId; HooksVersion=hooksVersion; Hooks=hooks; ShadowedHooks=shadowedHooks;
		}
		public string ProbeInstanceId { get; }
		public long HooksVersion { get; }
		public IReadOnlyList<ResidentInventoryHook> Hooks { get; }
		public IReadOnlyList<ShadowedHookState> ShadowedHooks { get; }
	}

	public sealed class ResidentInventoryHook {
		public ResidentInventoryHook(string patchId,string controller,string hookId,string assemblySimpleName,HookKind kind,MethodGuard target,string sourceSha256,int revision,bool enabled) {
			PatchId=patchId; Controller=controller; HookId=hookId; AssemblySimpleName=assemblySimpleName; Kind=kind;
			Target=target; SourceSha256=sourceSha256; Revision=revision; Enabled=enabled;
		}
		public string PatchId { get; }
		public string Controller { get; }
		public string HookId { get; }
		public string AssemblySimpleName { get; }
		public HookKind Kind { get; }
		public MethodGuard Target { get; }
		public string SourceSha256 { get; }
		public int Revision { get; }
		public bool Enabled { get; }
	}

	public sealed class ResidentIdentityMismatchException : IOException {
		public ResidentIdentityMismatchException(string expected,string actual)
			:base("Authenticated resident reported probe instance '"+actual+"', expected '"+expected+"'.") { }
	}

	/// <summary>One strict interpretation of the resident status wire shape, shared by every host-side
	/// controller. The authenticated transport proves who answered; this parser proves that answer names
	/// the expected resident and turns its ownership-qualified patch ids into one inventory model.</summary>
	public static class ResidentInventoryParser {
		public static ResidentInventory Parse(string payloadJson,string expectedProbeInstanceId) {
			if(String.IsNullOrWhiteSpace(payloadJson)) throw new ArgumentException("Resident status payload is required.",nameof(payloadJson));
			if(String.IsNullOrWhiteSpace(expectedProbeInstanceId)) throw new ArgumentException("Expected probe instance id is required.",nameof(expectedProbeInstanceId));
			using(var document=JsonDocument.Parse(payloadJson)) {
				var root=document.RootElement;
				var actual=RequiredString(root,"probe_instance_id");
				if(!String.Equals(actual,expectedProbeInstanceId,StringComparison.Ordinal)) throw new ResidentIdentityMismatchException(expectedProbeInstanceId,actual);
				var hooks=RequiredArray(root,"compiled_hooks").EnumerateArray().Select(value=>ParseHook(actual,value)).ToArray();
				var shadowed=root.TryGetProperty("shadowed_hooks",out var shadowedValue)
					?RequiredArray(shadowedValue,"shadowed_hooks",alreadySelected:true).EnumerateArray().Select(ParseShadowed).ToArray()
					:Array.Empty<ShadowedHookState>();
				return new ResidentInventory(actual,RequiredInt64(root,"hooks_version"),hooks,shadowed);
			}
		}

		static ResidentInventoryHook ParseHook(string probeInstanceId,JsonElement value) {
			var patchId=RequiredString(value,"patch_id");
			HookOwnership.TryParse(probeInstanceId,patchId,out var controller,out var hookId);
			if(!Enum.TryParse(RequiredString(value,"kind"),false,out HookKind kind)) throw new InvalidDataException("Resident hook kind is unsupported.");
			if(!Guid.TryParse(RequiredString(value,"module_mvid"),out var mvid)) throw new InvalidDataException("Resident hook module_mvid is invalid.");
			var target=new MethodGuard(mvid,checked((uint)RequiredInt32(value,"metadata_token")),RequiredString(value,"declaring_type"),RequiredString(value,"signature"),RequiredString(value,"il_sha256"));
			return new ResidentInventoryHook(patchId,controller,hookId,OptionalString(value,"assembly_simple_name"),kind,target,
				RequiredString(value,"source_sha256"),RequiredInt32(value,"revision"),RequiredBoolean(value,"enabled"));
		}

		static ShadowedHookState ParseShadowed(JsonElement value) => new ShadowedHookState(
			RequiredString(value,"patch_id"),RequiredString(value,"declaring_type"),RequiredString(value,"shadowing_assembly"));

		static JsonElement RequiredArray(JsonElement owner,string name,bool alreadySelected=false) {
			var value=alreadySelected?owner:owner.TryGetProperty(name,out var selected)?selected:throw new InvalidDataException("Resident status omitted "+name+".");
			if(value.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("Resident status "+name+" is not an array.");
			return value;
		}
		static string RequiredString(JsonElement owner,string name) => owner.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&!String.IsNullOrWhiteSpace(value.GetString())
			?value.GetString()!:throw new InvalidDataException("Resident status omitted "+name+".");
		static string OptionalString(JsonElement owner,string name) => owner.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??String.Empty:String.Empty;
		static int RequiredInt32(JsonElement owner,string name) => owner.TryGetProperty(name,out var value)&&value.TryGetInt32(out var result)?result:throw new InvalidDataException("Resident status omitted or invalid "+name+".");
		static long RequiredInt64(JsonElement owner,string name) => owner.TryGetProperty(name,out var value)&&value.TryGetInt64(out var result)?result:throw new InvalidDataException("Resident status omitted or invalid "+name+".");
		static bool RequiredBoolean(JsonElement owner,string name) => owner.TryGetProperty(name,out var value)&&(value.ValueKind==JsonValueKind.True||value.ValueKind==JsonValueKind.False)?value.GetBoolean():throw new InvalidDataException("Resident status omitted or invalid "+name+".");
	}
}
