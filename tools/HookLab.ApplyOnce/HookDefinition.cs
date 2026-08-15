using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HookLab.ApplyOnce;

internal sealed class HookDefinition {
	static readonly JsonSerializerOptions JsonOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };

	public int SchemaVersion { get; set; }
	public string? Id { get; set; }
	public ProcessDefinition? Process { get; set; }
	public TargetDefinition? Target { get; set; }
	public PatchDefinition? Hook { get; set; }

	public static HookDefinition Load(string path) {
		var fullPath=Path.GetFullPath(path);
		var bytes=File.ReadAllBytes(fullPath);
		if(bytes.Length is 0 or > 64*1024) throw new InvalidDataException("Hook definition length is invalid.");
		HookDefinition value;
		try { value=JsonSerializer.Deserialize<HookDefinition>(bytes,JsonOptions) ?? throw new InvalidDataException("Hook definition is empty."); }
		catch(JsonException ex) { throw new InvalidDataException("Hook definition JSON is invalid: "+ex.Message,ex); }
		value.Validate();
		return value;
	}

	internal void Validate() {
		if(SchemaVersion!=1) throw new InvalidDataException("schemaVersion must be 1.");
		Id=Required(Id,"id",128);
		if(Process is null) throw new InvalidDataException("process is required.");
		if(Target is null) throw new InvalidDataException("target is required.");
		if(Hook is null) throw new InvalidDataException("hook is required.");
		Process.FileName=Required(Process.FileName,"process.fileName",260);
		if(!String.Equals(Path.GetFileName(Process.FileName),Process.FileName,StringComparison.Ordinal)) throw new InvalidDataException("process.fileName must be a file basename, not a path.");
		Target.Assembly=Required(Target.Assembly,"target.assembly",256);
		Target.DeclaringType=Required(Target.DeclaringType,"target.declaringType",1024);
		Target.Method=Required(Target.Method,"target.method",256);
		Target.Signature=Required(Target.Signature,"target.signature",1024);
		Target.ModuleMvid=Required(Target.ModuleMvid,"target.moduleMvid",64);
		if(!Guid.TryParse(Target.ModuleMvid,out var mvid) || mvid==Guid.Empty) throw new InvalidDataException("target.moduleMvid must be a non-empty GUID.");
		Target.ModuleMvid=mvid.ToString("D");
		if(Target.MetadataToken<=0) throw new InvalidDataException("target.metadataToken must be positive.");
		Target.IlSha256=Required(Target.IlSha256,"target.ilSha256",64).ToLowerInvariant();
		if(Target.IlSha256.Length!=64 || Target.IlSha256.Any(value=>!Uri.IsHexDigit(value))) throw new InvalidDataException("target.ilSha256 must be 64 hexadecimal characters.");
		Hook.Kind=Required(Hook.Kind,"hook.kind",32);
		if(Hook.Kind is not ("Prefix" or "Postfix" or "Finalizer" or "Transpiler")) throw new InvalidDataException("hook.kind is unsupported.");
		if(Hook.Revision<=0) throw new InvalidDataException("hook.revision must be positive.");
		if(String.IsNullOrWhiteSpace(Hook.Source)||Hook.Source.Length>8192) throw new InvalidDataException("hook.source is invalid.");
		if(Convert.ToBase64String(Encoding.UTF8.GetBytes(Hook.Source)).Length>2048) throw new InvalidDataException("hook.source exceeds the bootstrap value limit after base64 encoding.");
		if(Hook.MaximumEventsPerSecond<=0) throw new InvalidDataException("hook.maximumEventsPerSecond must be positive.");
		if(Hook.MaximumStringLength<=0) throw new InvalidDataException("hook.maximumStringLength must be positive.");
	}

	static string Required(string? value,string name,int maximum) {
		if(String.IsNullOrWhiteSpace(value)) throw new InvalidDataException(name+" is required.");
		if(value.Length>maximum) throw new InvalidDataException(name+" is too long.");
		if(value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidDataException(name+" contains a newline.");
		return value;
	}
}

internal sealed class ProcessDefinition { public string? FileName { get; set; } }
internal sealed class TargetDefinition {
	public string? Assembly { get; set; }
	public string? ModuleMvid { get; set; }
	public string? DeclaringType { get; set; }
	public string? Method { get; set; }
	public int MetadataToken { get; set; }
	public string? Signature { get; set; }
	public string? IlSha256 { get; set; }
}
internal sealed class PatchDefinition {
	public string? Kind { get; set; }
	public int Revision { get; set; }
	public string? Source { get; set; }
	public int MaximumEventsPerSecond { get; set; }=100;
	public int MaximumStringLength { get; set; }=1024;
	public bool Enabled { get; set; }=true;
}
