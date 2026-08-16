namespace HookLab.Injector;

public sealed class LiveTargetInspectionUnavailableException:Exception {
	public LiveTargetInspectionUnavailableException(string message,Exception innerException):base(message,innerException) { }
}

public sealed record InjectorRequest(
	int ProcessId,
	long ProcessCreationUtcTicks,
	string DefinitionPath,
	string DefinitionSha256,
	HookDefinition Definition,
	string? PermittedImagePath=null,
	int ClrReadinessTimeoutMs=5000,
	int InitializationTimeoutMs=5000);

public sealed record InjectorResult(
	string Status,
	string DefinitionSha256,
	string? ProbeInstanceId,
	string? PatchId,
	long? HooksVersion,
	int ProcessId,
	long ProcessCreationUtcTicks,
	string ImagePath);

public sealed record ResidentStatusResult(
	int ProcessId,
	long ProcessCreationUtcTicks,
	string ImagePath,
	string ProbeInstanceId,
	long HooksVersion,
	IReadOnlyList<ResidentHookStatus> Hooks);

public sealed record ResidentHookStatus(
	string PatchId,
	string Kind,
	string ModuleMvid,
	int MetadataToken,
	string DeclaringType,
	string Signature,
	string IlSha256,
	string SourceSha256,
	int Revision,
	bool Enabled);
