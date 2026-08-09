using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace dgSpy.Extension.Policy {
	public enum CapabilityPermission { RuntimeHooks, CustomHookCode, HookExport }

	/// <summary>A decision captured before an operation starts. Keeping this value, rather than consulting
	/// the policy again, guarantees that a reload cannot widen an in-flight operation.</summary>
	public sealed class CapabilityDecision {
		public string HostId { get; }
		public string Provider { get; }
		public string Operation { get; }
		public CapabilityPermission Permission { get; }
		public bool Granted { get; }
		public string Reason { get; }
		internal CapabilityDecision(string hostId,string provider,string operation,CapabilityPermission permission,bool granted,string reason) {
			HostId=hostId; Provider=provider; Operation=operation; Permission=permission; Granted=granted; Reason=reason;
		}
		public void Demand() { if(!Granted) throw new dgSpy.Extension.RpcException("permission_denied",$"Permission '{CapabilityPolicySnapshot.Name(Permission)}' is denied for '{Provider}/{Operation}' on host '{HostId}' ({Reason})."); }
	}

	/// <summary>Immutable, default-deny host policy. T10 declares a provider operation's requirement by
	/// calling CaptureDecision(provider, operation, permission) once, before dispatch, then Demand().</summary>
	public sealed class CapabilityPolicySnapshot {
		readonly IReadOnlyDictionary<string,bool> grants;
		public string HostId { get; }
		public string AccessMode { get; }
		public string Source { get; }
		CapabilityPolicySnapshot(string hostId,string accessMode,string source,Dictionary<string,bool> grants) {
			HostId=hostId; AccessMode=accessMode; Source=source;
			this.grants=new ReadOnlyDictionary<string,bool>(new Dictionary<string,bool>(grants,StringComparer.Ordinal));
		}
		public static CapabilityPolicySnapshot Load(string hostId,string? path=null,string? accessMode=null) {
			if(string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("hostId is required.",nameof(hostId));
			var mode=NormalizeMode(accessMode ?? Environment.GetEnvironmentVariable("DGSPY_ACCESS_MODE"));
			path=path ?? Environment.GetEnvironmentVariable("DGSPY_CAPABILITY_POLICY_FILE");
			if(path is null) { var bundled=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capability-policy.json"); path=File.Exists(bundled)?bundled:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy","capability-policy.json"); }
			var grants=new Dictionary<string,bool>(StringComparer.Ordinal);
			if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new CapabilityPolicySnapshot(hostId,mode,path ?? "unconfigured",grants);
			using(var document=JsonDocument.Parse(File.ReadAllText(path))) {
				var root=document.RootElement;
				if(!root.TryGetProperty("format_version",out var format) || format.GetInt32()!=1) throw new InvalidDataException("Capability policy format_version must be 1.");
				if(root.TryGetProperty("entries",out var entries)) foreach(var entry in entries.EnumerateArray()) {
					var configuredHost=Canonical(entry.GetProperty("host_id").GetString(),"host_id");
					if(configuredHost!=Canonical(hostId,"host_id")) continue;
					var provider=Canonical(entry.GetProperty("provider").GetString(),"provider");
					var operation=Canonical(entry.GetProperty("operation").GetString(),"operation");
					var permissions=entry.GetProperty("permissions");
					foreach(CapabilityPermission permission in Enum.GetValues(typeof(CapabilityPermission))) {
						var name=Name(permission); if(permissions.TryGetProperty(name,out var enabled)) grants[Key(provider,operation,permission)]=enabled.GetBoolean();
					}
				}
			}
			return new CapabilityPolicySnapshot(hostId,mode,Path.GetFullPath(path),grants);
		}
		public CapabilityDecision CaptureDecision(string provider,string operation,CapabilityPermission permission) {
			provider=Canonical(provider,"provider"); operation=Canonical(operation,"operation");
			if(AccessMode=="inspect-only") return new CapabilityDecision(HostId,provider,operation,permission,false,"inspect_only_ceiling");
			bool granted; return grants.TryGetValue(Key(provider,operation,permission),out granted) && granted
				? new CapabilityDecision(HostId,provider,operation,permission,true,"explicit_grant")
				: new CapabilityDecision(HostId,provider,operation,permission,false,"missing_or_disabled");
		}
		public object Describe() => new { host_id=HostId,access_mode=AccessMode,source=Source,reload_boundary="restart_or_explicit_reload",permissions=new[]{"runtime_hooks","custom_hook_code","hook_export"} };
		static string NormalizeMode(string? value) { var mode=string.IsNullOrWhiteSpace(value)?"full-control":value!.Trim().ToLowerInvariant(); if(mode!="full-control"&&mode!="inspect-only") throw new InvalidDataException("DGSPY_ACCESS_MODE must be full-control or inspect-only."); return mode; }
		static string Canonical(string? value,string field) { if(string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(field+" is required."); var result=value!.Trim().ToLowerInvariant(); foreach(var c in result) if(!(c>='a'&&c<='z')&&!(c>='0'&&c<='9')&&c!='_'&&c!='-'&&c!='.') throw new InvalidDataException(field+" is not canonical."); return result; }
		static string Key(string provider,string operation,CapabilityPermission permission) => provider+"\n"+operation+"\n"+Name(permission);
		public static string Name(CapabilityPermission permission) { switch(permission) { case CapabilityPermission.RuntimeHooks:return "runtime_hooks"; case CapabilityPermission.CustomHookCode:return "custom_hook_code"; case CapabilityPermission.HookExport:return "hook_export"; default:throw new ArgumentOutOfRangeException(nameof(permission)); } }
	}
}
