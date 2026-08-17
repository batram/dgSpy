using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HookLab.Injector;

namespace HookLab.Watcher;

internal sealed class PackageManifest {
	public int SchemaVersion { get; set; }
	public string? PackageId { get; set; }
	public int Revision { get; set; }
	public string? DisplayName { get; set; }
	public int MinimumProtocolVersion { get; set; }
	public string? Architecture { get; set; }
	public string? RuntimeFamily { get; set; }
	public ProcessDefinition? Process { get; set; }
	public TargetDefinition? Target { get; set; }
	public PackageHook? Hook { get; set; }
	public List<PackageEntry>? Entries { get; set; }
}
internal sealed class PackageHook {
	public string? Id { get; set; }
	public string? Kind { get; set; }
	public int Revision { get; set; }
	public bool Enabled { get; set; }=true;
	public string? SourcePath { get; set; }
	public int MaximumEventsPerSecond { get; set; }=100;
	public int MaximumStringLength { get; set; }=1024;
}
internal sealed class PackageEntry { public string? Path { get; set; } public string? Sha256 { get; set; } }
internal sealed record LoadedPackage(string Root,string PackageId,string Digest,HookDefinition Definition,string ManifestPath);

internal static class PackageLoader {
	internal const string ManifestName="hooklab-package.json";
	static readonly JsonSerializerOptions JsonOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };

	public static LoadedPackage Load(string directory) {
		var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		if(!Directory.Exists(root)) throw new DirectoryNotFoundException("HookLab package directory does not exist: "+root);
		RejectReparse(root);
		foreach(var childDirectory in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories)) RejectReparse(childDirectory);
		var manifestPath=Path.Combine(root,ManifestName);
		if(!File.Exists(manifestPath)) throw new InvalidDataException("HookLab package manifest is missing: "+manifestPath);
		var bytes=ReadBounded(manifestPath,64*1024,"package manifest");
		PackageManifest manifest;
		try { manifest=JsonSerializer.Deserialize<PackageManifest>(bytes,JsonOptions) ?? throw new InvalidDataException("HookLab package manifest is empty."); }
		catch(JsonException ex) { throw new InvalidDataException("HookLab package manifest JSON is invalid: "+ex.Message,ex); }
		if(manifest.SchemaVersion!=1) throw new InvalidDataException("package schemaVersion must be 1.");
		var packageId=Required(manifest.PackageId,"packageId",128);
		if(manifest.Revision<=0) throw new InvalidDataException("package revision must be positive.");
		_ = Required(manifest.DisplayName,"displayName",256);
		if(manifest.MinimumProtocolVersion!=1) throw new InvalidDataException("minimumProtocolVersion is unsupported.");
		if(manifest.Architecture!="x64") throw new InvalidDataException("package architecture must be x64.");
		if(manifest.RuntimeFamily!="clr-v4") throw new InvalidDataException("package runtimeFamily must be clr-v4.");
		if(manifest.Process is null||manifest.Target is null||manifest.Hook is null) throw new InvalidDataException("package process, target, and hook are required.");
		var hookId=Required(manifest.Hook.Id,"hook.id",128);
		var sourcePath=NormalizeRelative(Required(manifest.Hook.SourcePath,"hook.sourcePath",260));
		var entries=manifest.Entries??throw new InvalidDataException("package entries are required.");
		if(entries.Count is 0 or > 32) throw new InvalidDataException("package entry count is invalid.");
		var declared=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
		foreach(var entry in entries) {
			var relative=NormalizeRelative(Required(entry.Path,"entries.path",260));
			var digest=Digest(Required(entry.Sha256,"entries.sha256",64));
			if(!declared.TryAdd(relative,digest)) throw new InvalidDataException("Duplicate normalized package entry path: "+relative);
		}
		if(!declared.ContainsKey(sourcePath)) throw new InvalidDataException("hook.sourcePath is not declared in package entries.");
		var actual=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>Path.GetRelativePath(root,path).Replace('\\','/')).Where(path=>!String.Equals(path,ManifestName,StringComparison.OrdinalIgnoreCase)).ToArray();
		foreach(var relative in actual) { var normalized=NormalizeRelative(relative); var full=ResolveChild(root,normalized); RejectReparse(full); if(!declared.ContainsKey(normalized)) throw new InvalidDataException("Undeclared package entry: "+normalized); }
		foreach(var entry in declared) {
			var full=ResolveChild(root,entry.Key); if(!File.Exists(full)) throw new InvalidDataException("Missing package entry: "+entry.Key); RejectReparse(full);
			if(!String.Equals(FileDigest(full),entry.Value,StringComparison.Ordinal)) throw new InvalidDataException("Package entry digest mismatch: "+entry.Key);
		}
		var source=Encoding.UTF8.GetString(ReadBounded(ResolveChild(root,sourcePath),8192,"hook source")).TrimEnd('\r','\n');
		var definition=new HookDefinition { SchemaVersion=1,Id=hookId,Process=manifest.Process,Target=manifest.Target,Hook=new PatchDefinition { Kind=manifest.Hook.Kind,Revision=manifest.Hook.Revision,Enabled=manifest.Hook.Enabled,Source=source,MaximumEventsPerSecond=manifest.Hook.MaximumEventsPerSecond,MaximumStringLength=manifest.Hook.MaximumStringLength } };
		definition.Validate();
		using var aggregate=SHA256.Create();
		var identity=Encoding.UTF8.GetBytes(FileDigest(manifestPath)+"\n"+String.Join("\n",declared.OrderBy(value=>value.Key,StringComparer.Ordinal).Select(value=>value.Key+":"+value.Value)));
		return new(root,packageId,Convert.ToHexString(aggregate.ComputeHash(identity)).ToLowerInvariant(),definition,manifestPath);
	}

	static string NormalizeRelative(string value) {
		var normalized=value.Replace('\\','/');
		if(Path.IsPathRooted(value)||normalized.StartsWith('/')||normalized.Split('/').Any(part=>part is "" or "." or "..")) throw new InvalidDataException("Package entry path must be normalized and relative: "+value);
		return normalized;
	}
	static string ResolveChild(string root,string relative) { var full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))); if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package entry escapes its root: "+relative); return full; }
	static void RejectReparse(string path) { if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Package paths must not contain reparse points: "+path); }
	static byte[] ReadBounded(string path,int maximum,string label) { using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); if(stream.Length is 0||stream.Length>maximum) throw new InvalidDataException(label+" length is invalid."); var bytes=new byte[stream.Length]; stream.ReadExactly(bytes); return bytes; }
	static string FileDigest(string path) { using var stream=File.OpenRead(path); using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
	static string Digest(string value) { value=value.ToLowerInvariant(); if(value.Length!=64||value.Any(character=>!Uri.IsHexDigit(character))) throw new InvalidDataException("Package SHA-256 must contain 64 hexadecimal characters."); return value; }
	static string Required(string? value,string name,int maximum) { if(String.IsNullOrWhiteSpace(value)||value.Length>maximum||value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidDataException(name+" is invalid."); return value; }
}

internal sealed class WatchProfile {
	public int SchemaVersion { get; set; }
	public string? Id { get; set; }
	public bool Enabled { get; set; }=true;
	public string? Scope { get; set; }
	public string? PackagePath { get; set; }
	public string? PackageId { get; set; }
	public string? PackageDigest { get; set; }
	public List<string>? PermittedExecutablePaths { get; set; }
	public string? NotificationPolicy { get; set; }
	public int ClrReadinessTimeoutMs { get; set; }
	public int InitializationTimeoutMs { get; set; }
}

internal static class ProfileCatalog {
	static readonly JsonSerializerOptions JsonOptions=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
	public static IReadOnlyList<WatchDefinition> Load(string directory,bool allowEmpty=false) {
		var root=Path.GetFullPath(directory); if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Profiles directory does not exist: "+root);
		var paths=ProfilePaths(root); if(paths.Length==0) { if(allowEmpty) return Array.Empty<WatchDefinition>(); throw new InvalidDataException("Profiles directory contains no JSON profiles at its root or immediate deployment directories: "+root); }
		var result=new List<WatchDefinition>(); var ids=new HashSet<string>(StringComparer.Ordinal);
		foreach(var path in paths) {
			var profile=Read(path);
			if(profile.SchemaVersion!=1||String.IsNullOrWhiteSpace(profile.Id)||!ids.Add(profile.Id)) throw new InvalidDataException("Profile schema or ID is invalid or duplicated.");
			if(profile.Scope is not ("explicit" or "launcher" or "user")) throw new InvalidDataException("Profile scope is unsupported: "+profile.Scope);
			if(!profile.Enabled) continue;
			result.Add(LoadEnabled(path,profile));
		}
		var duplicateHook=result.GroupBy(value=>value.Value.Id!,StringComparer.Ordinal).FirstOrDefault(group=>group.Count()>1); if(duplicateHook is not null) throw new InvalidDataException("Enabled profiles contain duplicate hook ID: "+duplicateHook.Key);
		return result;
	}
	internal static (string ProfilePath,WatchProfile Profile,LoadedPackage Package) LoadExport(string directory) {
		var root=Path.GetFullPath(directory); if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Exported deployment does not exist: "+root);
		var paths=Directory.GetFiles(root,"*.json",SearchOption.TopDirectoryOnly); if(paths.Length!=1) throw new InvalidDataException("Exported deployment must contain exactly one profile.");
		var profile=Read(paths[0]); if(profile.SchemaVersion!=1||String.IsNullOrWhiteSpace(profile.Id)||profile.Scope!="user"||profile.Enabled) throw new InvalidDataException("Exported profile must be a disabled schema-1 user profile.");
		var definition=LoadEnabled(paths[0],profile); var package=PackageLoader.Load(Path.GetDirectoryName(definition.Path)!);
		return (paths[0],profile,package);
	}
	internal static void Write(string path,WatchProfile profile)=>File.WriteAllText(path,JsonSerializer.Serialize(profile,JsonOptions),new UTF8Encoding(false));
	static string[] ProfilePaths(string root) { var rootPaths=Directory.GetFiles(root,"*.json",SearchOption.TopDirectoryOnly); return (rootPaths.Length>0?rootPaths:Directory.GetDirectories(root,"*",SearchOption.TopDirectoryOnly).SelectMany(child=>Directory.GetFiles(child,"*.json",SearchOption.TopDirectoryOnly))).OrderBy(value=>value,StringComparer.OrdinalIgnoreCase).ToArray(); }
	static WatchProfile Read(string path) { try { return JsonSerializer.Deserialize<WatchProfile>(File.ReadAllBytes(path),JsonOptions)??throw new InvalidDataException("Profile is empty."); } catch(JsonException ex) { throw new InvalidDataException("Profile JSON is invalid: "+ex.Message,ex); } }
	static WatchDefinition LoadEnabled(string path,WatchProfile profile) {
		if(profile.NotificationPolicy is not ("errors" or "all" or "none")) throw new InvalidDataException("Profile notificationPolicy is unsupported: "+profile.Id);
		if(profile.ClrReadinessTimeoutMs is < 250 or > 30000||profile.InitializationTimeoutMs is < 1000 or > 30000) throw new InvalidDataException("Profile readiness or initialization deadline is outside the supported bounds: "+profile.Id);
		if(String.IsNullOrWhiteSpace(profile.PackagePath)||Path.IsPathRooted(profile.PackagePath)||profile.PackagePath.Replace('\\','/').Split('/').Any(part=>part is "" or "." or "..")) throw new InvalidDataException("Profile packagePath must be normalized and relative to the profiles directory.");
		var profileRoot=Path.GetDirectoryName(path)!; var packageRoot=Path.GetFullPath(Path.Combine(profileRoot,profile.PackagePath)); if(!packageRoot.StartsWith(Path.TrimEndingDirectorySeparator(profileRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Profile packagePath escapes its deployment directory.");
		var package=PackageLoader.Load(packageRoot); if(package.PackageId!=profile.PackageId||package.Digest!=profile.PackageDigest?.ToLowerInvariant()) throw new InvalidDataException("Profile package identity or digest does not match verified package content: "+profile.Id);
		if((profile.PermittedExecutablePaths??new()).Any(value=>!Path.IsPathFullyQualified(value))) throw new InvalidDataException("Profile permitted executable paths must be fully qualified: "+profile.Id);
		var permitted=(profile.PermittedExecutablePaths??new()).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if(permitted.Length==0) throw new InvalidDataException("Enabled profile requires at least one permitted executable path: "+profile.Id);
		return new WatchDefinition(package.ManifestPath,package.Definition,package.Digest,profile.Id,package.PackageId,permitted,profile.NotificationPolicy!,profile.ClrReadinessTimeoutMs,profile.InitializationTimeoutMs);
	}
}
