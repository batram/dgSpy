using System.Collections.ObjectModel;
using System.Text.Json;

namespace dgSpy.Gateway;

public enum CapabilityPermission { RuntimeHooks, CustomHookCode, HookExport }

public sealed class GatewayCapabilityDecision {
	public bool Granted { get; }
	public string Reason { get; }
	internal GatewayCapabilityDecision(bool granted,string reason) { Granted=granted; Reason=reason; }
	public void Demand(string provider,string operation,CapabilityPermission permission) { if(!Granted) throw new GatewayControlException("permission_denied",$"Permission '{CapabilityPolicySnapshot.Name(permission)}' is denied for '{provider}/{operation}' ({Reason})."); }
}

/// <summary>Gateway mirror of the authoritative extension policy. Provider dispatch must capture the
/// extension decision too; this outer check is defense in depth, not authorization for direct RPC.</summary>
public sealed class CapabilityPolicySnapshot {
	readonly IReadOnlyDictionary<string,bool> grants;
	public string HostId { get; }
	public string AccessMode { get; }
	CapabilityPolicySnapshot(string hostId,string accessMode,Dictionary<string,bool> grants) { HostId=hostId; AccessMode=accessMode; this.grants=new ReadOnlyDictionary<string,bool>(new Dictionary<string,bool>(grants,StringComparer.Ordinal)); }
	public static CapabilityPolicySnapshot Load(string hostId,string? path=null,string? accessMode=null) {
		var mode=string.IsNullOrWhiteSpace(accessMode)?"full-control":accessMode.Trim().ToLowerInvariant(); if(mode is not ("full-control" or "inspect-only")) throw new InvalidDataException("DGSPY_ACCESS_MODE must be full-control or inspect-only.");
		path ??= Environment.GetEnvironmentVariable("DGSPY_CAPABILITY_POLICY_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy","capability-policy.json"); var grants=new Dictionary<string,bool>(StringComparer.Ordinal);
		if(!string.IsNullOrWhiteSpace(path)&&File.Exists(path)) using(var document=JsonDocument.Parse(File.ReadAllText(path))) {
			var root=document.RootElement; if(!root.TryGetProperty("format_version",out var format)||format.GetInt32()!=1) throw new InvalidDataException("Capability policy format_version must be 1.");
			if(root.TryGetProperty("entries",out var entries)) foreach(var entry in entries.EnumerateArray()) {
				if(Canonical(entry.GetProperty("host_id").GetString())!=Canonical(hostId)) continue; var provider=Canonical(entry.GetProperty("provider").GetString()); var operation=Canonical(entry.GetProperty("operation").GetString()); var permissions=entry.GetProperty("permissions");
				foreach(CapabilityPermission permission in Enum.GetValues<CapabilityPermission>()) { var name=Name(permission); if(permissions.TryGetProperty(name,out var enabled)) grants[Key(provider,operation,permission)]=enabled.GetBoolean(); }
			}
		}
		return new CapabilityPolicySnapshot(hostId,mode,grants);
	}
	public GatewayCapabilityDecision CaptureDecision(string provider,string operation,CapabilityPermission permission) { provider=Canonical(provider); operation=Canonical(operation); if(AccessMode=="inspect-only") return new(false,"inspect_only_ceiling"); return grants.TryGetValue(Key(provider,operation,permission),out var enabled)&&enabled?new(true,"explicit_grant"):new(false,"missing_or_disabled"); }
	static string Canonical(string? value) { if(string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Canonical policy value is required."); var result=value.Trim().ToLowerInvariant(); if(result.Any(c=>!(c is >= 'a' and <= 'z')&&!(c is >= '0' and <= '9')&&c is not ('_' or '-' or '.'))) throw new InvalidDataException("Policy value is not canonical."); return result; }
	static string Key(string provider,string operation,CapabilityPermission permission)=>provider+'\n'+operation+'\n'+Name(permission);
	public static string Name(CapabilityPermission permission)=>permission switch { CapabilityPermission.RuntimeHooks=>"runtime_hooks",CapabilityPermission.CustomHookCode=>"custom_hook_code",CapabilityPermission.HookExport=>"hook_export",_=>throw new ArgumentOutOfRangeException(nameof(permission)) };
}
