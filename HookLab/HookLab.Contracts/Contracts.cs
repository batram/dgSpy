using System;
using System.Collections.Generic;

namespace HookLab.Contracts {
	public enum ActionOutcome { trigger_not_reached, reached_not_evaluable, nearby_slot_not_found, action_failed, verification_failed, completed }
	public enum InterruptionReason { none, timeout, cancelled, client_disconnected, target_exited, appdomain_unloaded, ui_shutdown, dispatcher_degraded, external_debugger_action }
	public enum CleanupOutcome { not_required, completed, failed, ambiguous }
	public enum HookKind { Prefix, Postfix, Finalizer, Transpiler }
	public enum ProbeMessageKind { Request, Response, Event }

	public sealed class AtomicActionStatus {
		public AtomicActionStatus(ActionOutcome actionOutcome, InterruptionReason interruptionReason, CleanupOutcome cleanupOutcome,
			bool actionMayHaveExecuted, string auditId, string? reconciliationOperation = null) {
			ActionOutcome = actionOutcome; InterruptionReason = interruptionReason; CleanupOutcome = cleanupOutcome;
			ActionMayHaveExecuted = actionMayHaveExecuted; AuditId = Required(auditId, nameof(auditId)); ReconciliationOperation = reconciliationOperation;
		}
		public ActionOutcome ActionOutcome { get; }
		public InterruptionReason InterruptionReason { get; }
		public CleanupOutcome CleanupOutcome { get; }
		public bool ActionMayHaveExecuted { get; }
		public string AuditId { get; }
		public string? ReconciliationOperation { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class TargetIdentity {
		public TargetIdentity(string hostId, string imagePath, int processId, DateTime processCreationTimeUtc, string architecture, string runtimeId, string appDomainId) {
			HostId = Required(hostId, nameof(hostId)); ImagePath = Required(imagePath, nameof(imagePath)); ProcessId = processId;
			ProcessCreationTimeUtc = processCreationTimeUtc; Architecture = Required(architecture, nameof(architecture));
			RuntimeId = Required(runtimeId, nameof(runtimeId)); AppDomainId = Required(appDomainId, nameof(appDomainId));
		}
		public string HostId { get; } public string ImagePath { get; } public int ProcessId { get; }
		public DateTime ProcessCreationTimeUtc { get; } public string Architecture { get; } public string RuntimeId { get; } public string AppDomainId { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class MethodGuard {
		public MethodGuard(Guid moduleMvid, uint metadataToken, string declaringType, string methodSignature, string ilSha256) {
			ModuleMvid = moduleMvid; MetadataToken = metadataToken; DeclaringType = Required(declaringType, nameof(declaringType));
			MethodSignature = Required(methodSignature, nameof(methodSignature)); IlSha256 = Required(ilSha256, nameof(ilSha256));
		}
		public Guid ModuleMvid { get; } public uint MetadataToken { get; } public string DeclaringType { get; } public string MethodSignature { get; } public string IlSha256 { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class HookLimits {
		public HookLimits(int maximumEventsPerSecond, int maximumEventBytes, int maximumSerializationDepth, int maximumCollectionCount, int maximumStringLength, int maximumConsecutiveFailures) {
			MaximumEventsPerSecond = Positive(maximumEventsPerSecond, nameof(maximumEventsPerSecond)); MaximumEventBytes = Positive(maximumEventBytes, nameof(maximumEventBytes));
			MaximumSerializationDepth = Positive(maximumSerializationDepth, nameof(maximumSerializationDepth)); MaximumCollectionCount = Positive(maximumCollectionCount, nameof(maximumCollectionCount));
			MaximumStringLength = Positive(maximumStringLength, nameof(maximumStringLength)); MaximumConsecutiveFailures = Positive(maximumConsecutiveFailures, nameof(maximumConsecutiveFailures));
		}
		public int MaximumEventsPerSecond { get; } public int MaximumEventBytes { get; } public int MaximumSerializationDepth { get; }
		public int MaximumCollectionCount { get; } public int MaximumStringLength { get; } public int MaximumConsecutiveFailures { get; }
		static int Positive(int value, string name) => value <= 0 ? throw new ArgumentOutOfRangeException(name) : value;
	}

	public sealed class HookDocument {
		public HookDocument(int documentVersion, string hookId, HookKind kind, MethodGuard target, string behaviorJson, HookLimits limits, bool enabled) {
			if (documentVersion <= 0) throw new ArgumentOutOfRangeException(nameof(documentVersion)); DocumentVersion = documentVersion;
			HookId = Required(hookId, nameof(hookId)); Kind = kind; Target = target ?? throw new ArgumentNullException(nameof(target));
			BehaviorJson = Required(behaviorJson, nameof(behaviorJson)); Limits = limits ?? throw new ArgumentNullException(nameof(limits)); Enabled = enabled;
		}
		public int DocumentVersion { get; } public string HookId { get; } public HookKind Kind { get; } public MethodGuard Target { get; }
		public string BehaviorJson { get; } public HookLimits Limits { get; } public bool Enabled { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class PackageArtifact {
		public PackageArtifact(string name, string sha256, long size) { Name = Required(name, nameof(name)); Sha256 = Required(sha256, nameof(sha256)); if (size < 0) throw new ArgumentOutOfRangeException(nameof(size)); Size = size; }
		public string Name { get; } public string Sha256 { get; } public long Size { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class HookPackageManifest {
		public HookPackageManifest(int manifestVersion, string packageId, string packageVersion, TargetIdentity target, string backendIdentity,
			IReadOnlyList<HookDocument>? hooks, IReadOnlyList<PackageArtifact>? artifacts, IReadOnlyList<string>? requiredPermissions) {
			if (manifestVersion <= 0) throw new ArgumentOutOfRangeException(nameof(manifestVersion)); ManifestVersion = manifestVersion;
			PackageId = Required(packageId, nameof(packageId)); PackageVersion = Required(packageVersion, nameof(packageVersion));
			Target = target ?? throw new ArgumentNullException(nameof(target)); BackendIdentity = Required(backendIdentity, nameof(backendIdentity));
			Hooks = new List<HookDocument>(hooks ?? Array.Empty<HookDocument>()).AsReadOnly();
			Artifacts = new List<PackageArtifact>(artifacts ?? Array.Empty<PackageArtifact>()).AsReadOnly();
			RequiredPermissions = new List<string>(requiredPermissions ?? Array.Empty<string>()).AsReadOnly();
		}
		public int ManifestVersion { get; } public string PackageId { get; } public string PackageVersion { get; } public TargetIdentity Target { get; }
		public string BackendIdentity { get; } public IReadOnlyList<HookDocument> Hooks { get; } public IReadOnlyList<PackageArtifact> Artifacts { get; }
		public IReadOnlyList<string> RequiredPermissions { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ProbeMessage {
		public ProbeMessage(int protocolVersion, ProbeMessageKind kind, string correlationId, string operation, string payloadJson, long? expectedHooksVersion = null) {
			if (protocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion)); ProtocolVersion = protocolVersion; Kind = kind;
			CorrelationId = Required(correlationId, nameof(correlationId)); Operation = Required(operation, nameof(operation)); PayloadJson = Required(payloadJson, nameof(payloadJson)); ExpectedHooksVersion = expectedHooksVersion;
		}
		public int ProtocolVersion { get; } public ProbeMessageKind Kind { get; } public string CorrelationId { get; } public string Operation { get; }
		public string PayloadJson { get; } public long? ExpectedHooksVersion { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ProbeState {
		public ProbeState(int protocolVersion, string probeInstanceId, TargetIdentity target, string backendIdentity, long hooksVersion, IReadOnlyList<string>? patchIds, IReadOnlyList<CompiledHookState>? compiledHooks = null) {
			if (protocolVersion <= 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion)); ProtocolVersion = protocolVersion;
			ProbeInstanceId = Required(probeInstanceId, nameof(probeInstanceId)); Target = target ?? throw new ArgumentNullException(nameof(target));
			BackendIdentity = Required(backendIdentity, nameof(backendIdentity)); HooksVersion = hooksVersion;
			PatchIds = new List<string>(patchIds ?? Array.Empty<string>()).AsReadOnly(); CompiledHooks = new List<CompiledHookState>(compiledHooks ?? Array.Empty<CompiledHookState>()).AsReadOnly();
		}
		public int ProtocolVersion { get; } public string ProbeInstanceId { get; } public TargetIdentity Target { get; } public string BackendIdentity { get; }
		public long HooksVersion { get; } public IReadOnlyList<string> PatchIds { get; } public IReadOnlyList<CompiledHookState> CompiledHooks { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class CompiledHookState {
		public CompiledHookState(string patchId, string assemblySimpleName, HookKind kind, MethodGuard target, string sourceSha256, int revision, bool enabled) { PatchId=Required(patchId,nameof(patchId)); AssemblySimpleName=Required(assemblySimpleName,nameof(assemblySimpleName)); Kind=kind; Target=target??throw new ArgumentNullException(nameof(target)); SourceSha256=Required(sourceSha256,nameof(sourceSha256)); Revision=revision; Enabled=enabled; }
		public string PatchId { get; } public string AssemblySimpleName { get; } public HookKind Kind { get; } public MethodGuard Target { get; } public string SourceSha256 { get; } public int Revision { get; } public bool Enabled { get; }
		static string Required(string value,string name)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("Value is required.",name):value;
	}

	public static class HookOwnership {
		public const string DgSpyController="dgspy",WatcherController="watcher";
		public static string Qualify(string controller,string hookId) { if(String.IsNullOrWhiteSpace(controller)||controller.IndexOf(':')>=0) throw new ArgumentException("Controller is invalid.",nameof(controller)); if(String.IsNullOrWhiteSpace(hookId)||hookId.IndexOf(':')>=0) throw new ArgumentException("Hook id is invalid.",nameof(hookId)); return controller+":"+hookId; }
		public static bool TryParse(string probeInstanceId,string patchId,out string controller,out string hookId) { controller=hookId=String.Empty; var prefix=probeInstanceId+":"; if(!patchId.StartsWith(prefix,StringComparison.Ordinal)) return false; var value=patchId.Substring(prefix.Length); var separator=value.IndexOf(':'); if(separator<=0||separator==value.Length-1) return false; controller=value.Substring(0,separator); hookId=value.Substring(separator+1); return true; }
	}

	public sealed class HookEvent {
		public HookEvent(string probeInstanceId, string patchId, long hooksVersion, long sequence, DateTime timestampUtc, int managedThreadId,
			string payloadJson, bool truncated, long droppedCount) {
			ProbeInstanceId = Required(probeInstanceId, nameof(probeInstanceId)); PatchId = Required(patchId, nameof(patchId)); HooksVersion = hooksVersion;
			Sequence = sequence; TimestampUtc = timestampUtc; ManagedThreadId = managedThreadId; PayloadJson = Required(payloadJson, nameof(payloadJson)); Truncated = truncated; DroppedCount = droppedCount;
		}
		public string ProbeInstanceId { get; } public string PatchId { get; } public long HooksVersion { get; } public long Sequence { get; }
		public DateTime TimestampUtc { get; } public int ManagedThreadId { get; } public string PayloadJson { get; } public bool Truncated { get; } public long DroppedCount { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}
}
