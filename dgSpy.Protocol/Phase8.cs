using System;
using Newtonsoft.Json;

namespace dgSpy.Protocol {
	public sealed class ObjectIdInfo {
		[JsonProperty("object_id")] public uint ObjectId { get; set; }
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("process_id")] public int ProcessId { get; set; }
		[JsonProperty("runtime_id")] public string RuntimeId { get; set; }="";
		[JsonProperty("value",NullValueHandling=NullValueHandling.Ignore)] public EvaluatedValue? Value { get; set; }
	}
	public sealed class OutputMessage {
		[JsonProperty("output_id")] public long OutputId { get; set; }
		[JsonProperty("timestamp_utc")] public DateTime TimestampUtc { get; set; }
		[JsonProperty("category")] public string Category { get; set; }="";
		[JsonProperty("message")] public string Message { get; set; }="";
		[JsonProperty("process_id",NullValueHandling=NullValueHandling.Ignore)] public int? ProcessId { get; set; }
		[JsonProperty("runtime_id",NullValueHandling=NullValueHandling.Ignore)] public string? RuntimeId { get; set; }
	}
	public class OutputResult {
		[JsonProperty("messages")] public OutputMessage[] Messages { get; set; }=Array.Empty<OutputMessage>();
		[JsonProperty("oldest_output_id")] public long OldestOutputId { get; set; }
		[JsonProperty("oldest_available_cursor")] public long OldestAvailableCursor { get; set; }
		[JsonProperty("last_output_id")] public long LastOutputId { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
	}
	public sealed class WaitOutputResult : OutputResult { [JsonProperty("timed_out")] public bool TimedOut { get; set; } }
	public sealed class ModuleBreakpointInfo {
		[JsonProperty("breakpoint_id")] public int BreakpointId { get; set; }
		[JsonProperty("enabled")] public bool Enabled { get; set; }
		[JsonProperty("module_name",NullValueHandling=NullValueHandling.Ignore)] public string? ModuleName { get; set; }
		[JsonProperty("is_dynamic",NullValueHandling=NullValueHandling.Ignore)] public bool? IsDynamic { get; set; }
		[JsonProperty("is_in_memory",NullValueHandling=NullValueHandling.Ignore)] public bool? IsInMemory { get; set; }
		[JsonProperty("is_loaded",NullValueHandling=NullValueHandling.Ignore)] public bool? IsLoaded { get; set; }
		[JsonProperty("order",NullValueHandling=NullValueHandling.Ignore)] public int? Order { get; set; }
		[JsonProperty("process_name",NullValueHandling=NullValueHandling.Ignore)] public string? ProcessName { get; set; }
		[JsonProperty("app_domain_name",NullValueHandling=NullValueHandling.Ignore)] public string? AppDomainName { get; set; }
	}
	public sealed class ExceptionCategoryInfo {
		[JsonProperty("category")] public string Category { get; set; }="";
		[JsonProperty("display_name")] public string DisplayName { get; set; }="";
	}
	public sealed class ExceptionConditionInfo {
		[JsonProperty("kind")] public string Kind { get; set; }="";
		[JsonProperty("module")] public string Module { get; set; }="";
	}
	public sealed class ExceptionPolicyInfo {
		[JsonProperty("category")] public string Category { get; set; }="";
		[JsonProperty("name",NullValueHandling=NullValueHandling.Ignore)] public string? Name { get; set; }
		[JsonProperty("stop_thrown")] public bool StopThrown { get; set; }
		[JsonProperty("stop_unhandled")] public bool StopUnhandled { get; set; }
		[JsonProperty("conditions")] public ExceptionConditionInfo[] Conditions { get; set; }=Array.Empty<ExceptionConditionInfo>();
	}
	public sealed class ValueExportChunk {
		[JsonProperty("expression")] public string Expression { get; set; }="";
		[JsonProperty("offset")] public int Offset { get; set; }
		[JsonProperty("count")] public int Count { get; set; }
		[JsonProperty("total_size")] public int TotalSize { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
		[JsonProperty("sha256")] public string Sha256 { get; set; }="";
		[JsonProperty("data_base64")] public string DataBase64 { get; set; }="";
	}
	public sealed class HostExportResult {
		[JsonProperty("path")] public string Path { get; set; }="";
		[JsonProperty("size")] public int Size { get; set; }
		[JsonProperty("sha256")] public string Sha256 { get; set; }="";
		[JsonProperty("audit_id")] public string AuditId { get; set; }="";
	}
	public sealed class AnalysisEdge {
		[JsonProperty("kind")] public string Kind { get; set; }="";
		[JsonProperty("source")] public SymbolInfo Source { get; set; }=new SymbolInfo();
		[JsonProperty("target")] public SymbolInfo Target { get; set; }=new SymbolInfo();
	}
	public sealed class AnalysisResult {
		[JsonProperty("edges")] public AnalysisEdge[] Edges { get; set; }=Array.Empty<AnalysisEdge>();
		[JsonProperty("total")] public int Total { get; set; }
		[JsonProperty("truncated")] public bool Truncated { get; set; }
		[JsonProperty("scanned_methods")] public int ScannedMethods { get; set; }
		[JsonProperty("scan_truncated")] public bool ScanTruncated { get; set; }
	}
	public sealed class BreakpointDocument {
		[JsonProperty("format")] public string Format { get; set; }="dgspy.breakpoints";
		[JsonProperty("version")] public int Version { get; set; }=1;
		[JsonProperty("code")] public BreakpointInfo[] Code { get; set; }=Array.Empty<BreakpointInfo>();
		[JsonProperty("modules")] public ModuleBreakpointInfo[] Modules { get; set; }=Array.Empty<ModuleBreakpointInfo>();
		[JsonProperty("exceptions")] public ExceptionPolicyInfo[] Exceptions { get; set; }=Array.Empty<ExceptionPolicyInfo>();
		[JsonProperty("exception_total")] public int ExceptionTotal { get; set; }
		[JsonProperty("exception_truncated")] public bool ExceptionTruncated { get; set; }
	}
	public sealed class BreakpointImportResult {
		[JsonProperty("dry_run")] public bool DryRun { get; set; }
		[JsonProperty("mode")] public string Mode { get; set; }="merge";
		[JsonProperty("added")] public int Added { get; set; }
		[JsonProperty("removed")] public int Removed { get; set; }
		[JsonProperty("unchanged")] public int Unchanged { get; set; }
	}
}
