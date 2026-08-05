using System;
using System.Text.Json.Serialization;

namespace dgSpy.Protocol {
	public sealed class ObjectIdInfo {
		[JsonPropertyName("object_id")] public uint ObjectId { get; set; }
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("process_id")] public int ProcessId { get; set; }
		[JsonPropertyName("runtime_id")] public string RuntimeId { get; set; }="";
		[JsonPropertyName("value"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public EvaluatedValue? Value { get; set; }
	}
	public sealed class OutputMessage {
		[JsonPropertyName("output_id")] public long OutputId { get; set; }
		[JsonPropertyName("timestamp_utc")] public DateTime TimestampUtc { get; set; }
		[JsonPropertyName("category")] public string Category { get; set; }="";
		[JsonPropertyName("message")] public string Message { get; set; }="";
		[JsonPropertyName("process_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ProcessId { get; set; }
		[JsonPropertyName("runtime_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? RuntimeId { get; set; }
	}
	public class OutputResult {
		[JsonPropertyName("messages")] public OutputMessage[] Messages { get; set; }=Array.Empty<OutputMessage>();
		[JsonPropertyName("oldest_output_id")] public long OldestOutputId { get; set; }
		[JsonPropertyName("oldest_available_cursor")] public long OldestAvailableCursor { get; set; }
		[JsonPropertyName("last_output_id")] public long LastOutputId { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
	}
	public sealed class WaitOutputResult : OutputResult { [JsonPropertyName("timed_out")] public bool TimedOut { get; set; } }
	public sealed class ModuleBreakpointInfo {
		[JsonPropertyName("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonPropertyName("enabled")] public bool Enabled { get; set; }
		[JsonPropertyName("module_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ModuleName { get; set; }
		[JsonPropertyName("is_dynamic"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? IsDynamic { get; set; }
		[JsonPropertyName("is_in_memory"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? IsInMemory { get; set; }
		[JsonPropertyName("is_loaded"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public bool? IsLoaded { get; set; }
		[JsonPropertyName("order"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? Order { get; set; }
		[JsonPropertyName("process_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? ProcessName { get; set; }
		[JsonPropertyName("app_domain_name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? AppDomainName { get; set; }
	}
	public sealed class ExceptionCategoryInfo {
		[JsonPropertyName("category")] public string Category { get; set; }="";
		[JsonPropertyName("display_name")] public string DisplayName { get; set; }="";
	}
	public sealed class ExceptionConditionInfo {
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("module")] public string Module { get; set; }="";
	}
	public sealed class ExceptionPolicyInfo {
		[JsonPropertyName("category")] public string Category { get; set; }="";
		[JsonPropertyName("name"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
		[JsonPropertyName("stop_thrown")] public bool StopThrown { get; set; }
		[JsonPropertyName("stop_unhandled")] public bool StopUnhandled { get; set; }
		[JsonPropertyName("conditions")] public ExceptionConditionInfo[] Conditions { get; set; }=Array.Empty<ExceptionConditionInfo>();
	}
	public sealed class ValueExportChunk {
		[JsonPropertyName("expression")] public string Expression { get; set; }="";
		[JsonPropertyName("offset")] public int Offset { get; set; }
		[JsonPropertyName("count")] public int Count { get; set; }
		[JsonPropertyName("total_size")] public int TotalSize { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[JsonPropertyName("sha256")] public string Sha256 { get; set; }="";
		[JsonPropertyName("data_base64")] public string DataBase64 { get; set; }="";
	}
	public sealed class HostExportResult {
		[JsonPropertyName("path")] public string Path { get; set; }="";
		[JsonPropertyName("size")] public int Size { get; set; }
		[JsonPropertyName("sha256")] public string Sha256 { get; set; }="";
		[JsonPropertyName("audit_id")] public string AuditId { get; set; }="";
	}
	public sealed class AnalysisEdge {
		[JsonPropertyName("kind")] public string Kind { get; set; }="";
		[JsonPropertyName("source")] public SymbolInfo Source { get; set; }=new SymbolInfo();
		[JsonPropertyName("target")] public SymbolInfo Target { get; set; }=new SymbolInfo();
	}
	public sealed class AnalysisResult {
		[JsonPropertyName("edges")] public AnalysisEdge[] Edges { get; set; }=Array.Empty<AnalysisEdge>();
		[JsonPropertyName("total")] public int Total { get; set; }
		[JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[JsonPropertyName("scanned_methods")] public int ScannedMethods { get; set; }
		[JsonPropertyName("scan_truncated")] public bool ScanTruncated { get; set; }
	}
	public sealed class BreakpointDocument {
		[JsonPropertyName("format")] public string Format { get; set; }="dgspy.breakpoints";
		[JsonPropertyName("version")] public int Version { get; set; }=1;
		[JsonPropertyName("code")] public BreakpointInfo[] Code { get; set; }=Array.Empty<BreakpointInfo>();
		[JsonPropertyName("modules")] public ModuleBreakpointInfo[] Modules { get; set; }=Array.Empty<ModuleBreakpointInfo>();
		[JsonPropertyName("exceptions")] public ExceptionPolicyInfo[] Exceptions { get; set; }=Array.Empty<ExceptionPolicyInfo>();
		[JsonPropertyName("exception_total")] public int ExceptionTotal { get; set; }
		[JsonPropertyName("exception_truncated")] public bool ExceptionTruncated { get; set; }
	}
	public sealed class BreakpointImportResult {
		[JsonPropertyName("dry_run")] public bool DryRun { get; set; }
		[JsonPropertyName("mode")] public string Mode { get; set; }="merge";
		[JsonPropertyName("added")] public int Added { get; set; }
		[JsonPropertyName("removed")] public int Removed { get; set; }
		[JsonPropertyName("unchanged")] public int Unchanged { get; set; }
	}
}
