using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace dgSpy.ExtensionContracts {
	public enum ExtensionOperationClassification { ReadOnly, Mutation, TargetCodeExecution }

	public sealed class ExtensionSchemaDescriptor {
		public ExtensionSchemaDescriptor(int version, string jsonSchema) {
			if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
			Version = version;
			JsonSchema = Required(jsonSchema, nameof(jsonSchema));
		}
		public int Version { get; }
		public string JsonSchema { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ExtensionOperationBounds {
		public ExtensionOperationBounds(int timeoutMilliseconds, int maximumInputBytes, int maximumResultBytes) {
			if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
			if (maximumInputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
			if (maximumResultBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumResultBytes));
			TimeoutMilliseconds = timeoutMilliseconds;
			MaximumInputBytes = maximumInputBytes;
			MaximumResultBytes = maximumResultBytes;
		}
		public int TimeoutMilliseconds { get; }
		public int MaximumInputBytes { get; }
		public int MaximumResultBytes { get; }
	}

	public sealed class RuntimeEngineCapability {
		public RuntimeEngineCapability(string runtime, string engine, IReadOnlyDictionary<string, long>? limits = null) {
			Runtime = Required(runtime, nameof(runtime));
			Engine = Required(engine, nameof(engine));
			Limits = new ReadOnlyDictionary<string, long>(CopyLimits(limits));
		}
		public string Runtime { get; }
		public string Engine { get; }
		public IReadOnlyDictionary<string, long> Limits { get; }
		static Dictionary<string, long> CopyLimits(IReadOnlyDictionary<string, long>? limits) {
			var copy = new Dictionary<string, long>();
			if (limits != null) foreach (var pair in limits) copy.Add(pair.Key, pair.Value);
			return copy;
		}
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ExtensionOperationDescriptor {
		public ExtensionOperationDescriptor(string name, ExtensionSchemaDescriptor inputSchema, ExtensionSchemaDescriptor resultSchema,
			ExtensionOperationBounds bounds, ExtensionOperationClassification classification, string requiredPermission,
			string targetScope, IReadOnlyList<RuntimeEngineCapability>? capabilities = null) {
			Name = Required(name, nameof(name));
			InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
			ResultSchema = resultSchema ?? throw new ArgumentNullException(nameof(resultSchema));
			Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
			Classification = classification;
			RequiredPermission = Required(requiredPermission, nameof(requiredPermission));
			TargetScope = Required(targetScope, nameof(targetScope));
			Capabilities = new List<RuntimeEngineCapability>(capabilities ?? Array.Empty<RuntimeEngineCapability>()).AsReadOnly();
		}
		public string Name { get; }
		public ExtensionSchemaDescriptor InputSchema { get; }
		public ExtensionSchemaDescriptor ResultSchema { get; }
		public ExtensionOperationBounds Bounds { get; }
		public ExtensionOperationClassification Classification { get; }
		public string RequiredPermission { get; }
		public string TargetScope { get; }
		public IReadOnlyList<RuntimeEngineCapability> Capabilities { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ExtensionProviderDescriptor {
		public ExtensionProviderDescriptor(string providerId, string semanticVersion, int contractVersion,
			IReadOnlyList<ExtensionOperationDescriptor>? operations = null) {
			ProviderId = Required(providerId, nameof(providerId));
			SemanticVersion = Required(semanticVersion, nameof(semanticVersion));
			if (contractVersion <= 0) throw new ArgumentOutOfRangeException(nameof(contractVersion));
			ContractVersion = contractVersion;
			Operations = new List<ExtensionOperationDescriptor>(operations ?? Array.Empty<ExtensionOperationDescriptor>()).AsReadOnly();
		}
		public string ProviderId { get; }
		public string SemanticVersion { get; }
		public int ContractVersion { get; }
		public IReadOnlyList<ExtensionOperationDescriptor> Operations { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ExtensionInvocationContext {
		public ExtensionInvocationContext(string requestId, string hostId, string targetIdentity, string permission, DateTime deadlineUtc) {
			RequestId = Required(requestId, nameof(requestId)); HostId = Required(hostId, nameof(hostId));
			TargetIdentity = Required(targetIdentity, nameof(targetIdentity)); Permission = Required(permission, nameof(permission)); DeadlineUtc = deadlineUtc;
		}
		public string RequestId { get; }
		public string HostId { get; }
		public string TargetIdentity { get; }
		public string Permission { get; }
		public DateTime DeadlineUtc { get; }
		static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
	}

	public sealed class ExtensionInvocationResult {
		public ExtensionInvocationResult(bool succeeded, string resultJson, string? errorCode = null, bool sideEffectsMayHaveOccurred = false) {
			Succeeded = succeeded; ResultJson = resultJson ?? throw new ArgumentNullException(nameof(resultJson));
			ErrorCode = errorCode; SideEffectsMayHaveOccurred = sideEffectsMayHaveOccurred;
		}
		public bool Succeeded { get; }
		public string ResultJson { get; }
		public string? ErrorCode { get; }
		public bool SideEffectsMayHaveOccurred { get; }
	}

	public interface IDgSpyExtensionProvider {
		ExtensionProviderDescriptor Descriptor { get; }
		ExtensionInvocationResult Invoke(string operationName, string inputJson, ExtensionInvocationContext context);
	}
}
