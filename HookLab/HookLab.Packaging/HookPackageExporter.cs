using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HookLab.Packaging;

public sealed class HookPackageExportRequest {
	public string PackageId { get; set; }="";
	public string ProfileId { get; set; }="";
	public string DisplayName { get; set; }="";
	public int PackageRevision { get; set; }
	public string ProcessFileName { get; set; }="";
	public string PermittedExecutablePath { get; set; }="";
	public string Assembly { get; set; }="";
	public string ModuleMvid { get; set; }="";
	public string DeclaringType { get; set; }="";
	public string Method { get; set; }="";
	public int MetadataToken { get; set; }
	public string Signature { get; set; }="";
	public string IlSha256 { get; set; }="";
	public string HookId { get; set; }="";
	public string HookKind { get; set; }="";
	public int HookRevision { get; set; }
	public bool HookEnabled { get; set; }
	public string Source { get; set; }="";
	public int MaximumEventsPerSecond { get; set; }
	public int MaximumStringLength { get; set; }
	public string NotificationPolicy { get; set; }="errors";
	public int ClrReadinessTimeoutMs { get; set; }=5000;
	public int InitializationTimeoutMs { get; set; }=10000;
}

public sealed class HookPackageExportResult {
	public string DeploymentPath { get; set; }="";
	public string PackagePath { get; set; }="";
	public string ProfilePath { get; set; }="";
	public string PackageDigest { get; set; }="";
}

public static class HookPackageExporter {
	static readonly JsonSerializerOptions JsonOptions=new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true };

	public static HookPackageExportResult Export(string deploymentPath,HookPackageExportRequest request,bool overwrite=false) {
		Validate(request);
		var target=Path.GetFullPath(deploymentPath); var parent=Directory.GetParent(target)?.FullName??throw new InvalidDataException("Deployment path has no parent.");
		Directory.CreateDirectory(parent);
		var staging=target+".staging-"+Guid.NewGuid().ToString("N"); var backup=target+".previous-"+Guid.NewGuid().ToString("N");
		if((File.Exists(target)||Directory.Exists(target))&&!overwrite) throw new IOException("HookLab export already exists: "+target);
		try {
			var package=Path.Combine(staging,"package"); Directory.CreateDirectory(package);
			var sourcePath=Path.Combine(package,"hook.cs"); WriteUtf8(sourcePath,request.Source.TrimEnd('\r','\n')+"\n"); var sourceDigest=DigestFile(sourcePath);
			var manifest=new {
				schemaVersion=1,packageId=request.PackageId,revision=request.PackageRevision,displayName=request.DisplayName,minimumProtocolVersion=1,architecture="x64",runtimeFamily="clr-v4",
				process=new { fileName=request.ProcessFileName },
				target=new { assembly=request.Assembly,moduleMvid=request.ModuleMvid,declaringType=request.DeclaringType,method=request.Method,metadataToken=request.MetadataToken,signature=request.Signature,ilSha256=request.IlSha256 },
				hook=new { id=request.HookId,kind=request.HookKind,revision=request.HookRevision,enabled=request.HookEnabled,sourcePath="hook.cs",maximumEventsPerSecond=request.MaximumEventsPerSecond,maximumStringLength=request.MaximumStringLength },
				entries=new[] { new { path="hook.cs",sha256=sourceDigest } }
			};
			var manifestPath=Path.Combine(package,"hooklab-package.json"); WriteJson(manifestPath,manifest); var packageDigest=PackageDigest(manifestPath,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["hook.cs"]=sourceDigest });
			var profile=new { schemaVersion=1,id=request.ProfileId,enabled=false,scope="user",packagePath="package",packageId=request.PackageId,packageDigest,permittedExecutablePaths=new[]{request.PermittedExecutablePath},notificationPolicy=request.NotificationPolicy,clrReadinessTimeoutMs=request.ClrReadinessTimeoutMs,initializationTimeoutMs=request.InitializationTimeoutMs };
			var profilePath=Path.Combine(staging,request.ProfileId+".json"); WriteJson(profilePath,profile);
			if(File.Exists(target)) throw new IOException("HookLab deployment path names a file: "+target);
			if(Directory.Exists(target)) Directory.Move(target,backup);
			try { Directory.Move(staging,target); }
			catch { if(Directory.Exists(backup)) Directory.Move(backup,target); throw; }
			TryDelete(backup);
			return new HookPackageExportResult { DeploymentPath=target,PackagePath=Path.Combine(target,"package"),ProfilePath=Path.Combine(target,request.ProfileId+".json"),PackageDigest=packageDigest };
		}
		finally { TryDelete(staging); }
	}

	static void Validate(HookPackageExportRequest value) {
		Required(value.PackageId,"packageId",128); Required(value.ProfileId,"profileId",128); Required(value.DisplayName,"displayName",256); Required(value.ProcessFileName,"processFileName",260); Required(value.PermittedExecutablePath,"permittedExecutablePath",1024);
		Required(value.Assembly,"assembly",256); Required(value.ModuleMvid,"moduleMvid",64); Required(value.DeclaringType,"declaringType",1024); Required(value.Method,"method",256); Required(value.Signature,"signature",1024); Required(value.IlSha256,"ilSha256",64); Required(value.HookId,"hookId",128); Required(value.HookKind,"hookKind",32);
		if(value.PackageRevision<=0||value.HookRevision<=0||value.MetadataToken<=0||value.MaximumEventsPerSecond<=0||value.MaximumStringLength<=0) throw new InvalidDataException("HookLab export revisions, token, and bounds must be positive.");
		if(Path.GetFileName(value.ProcessFileName)!=value.ProcessFileName||!Path.IsPathRooted(value.PermittedExecutablePath)) throw new InvalidDataException("HookLab export process identity is invalid.");
		if(!Guid.TryParse(value.ModuleMvid,out var mvid)||mvid==Guid.Empty) throw new InvalidDataException("HookLab export MVID is invalid."); value.ModuleMvid=mvid.ToString("D");
		value.IlSha256=Digest(value.IlSha256); if(value.HookKind!="Prefix"&&value.HookKind!="Postfix"&&value.HookKind!="Finalizer"&&value.HookKind!="Transpiler") throw new InvalidDataException("HookLab export kind is unsupported.");
		if(String.IsNullOrWhiteSpace(value.Source)||Encoding.UTF8.GetByteCount(value.Source)>8192) throw new InvalidDataException("HookLab export source is invalid.");
		if(value.NotificationPolicy!="errors"&&value.NotificationPolicy!="all"&&value.NotificationPolicy!="none") throw new InvalidDataException("HookLab export notification policy is unsupported.");
		if(value.ClrReadinessTimeoutMs<250||value.ClrReadinessTimeoutMs>30000||value.InitializationTimeoutMs<1000||value.InitializationTimeoutMs>30000) throw new InvalidDataException("HookLab export deadlines are outside the supported bounds.");
		if(value.ProfileId.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new InvalidDataException("HookLab export profile ID is not a valid file name.");
	}
	static void Required(string value,string name,int maximum) { if(String.IsNullOrWhiteSpace(value)||value.Length>maximum||value.IndexOfAny(new[]{'\r','\n'})>=0) throw new InvalidDataException("HookLab export "+name+" is invalid."); }
	static void WriteJson(string path,object value)=>WriteUtf8(path,JsonSerializer.Serialize(value,JsonOptions)+"\n");
	static void WriteUtf8(string path,string value)=>File.WriteAllText(path,value,new UTF8Encoding(false));
	static string PackageDigest(string manifestPath,IReadOnlyDictionary<string,string> entries) { var identity=Encoding.UTF8.GetBytes(DigestFile(manifestPath)+"\n"+String.Join("\n",entries.OrderBy(value=>value.Key,StringComparer.Ordinal).Select(value=>value.Key+":"+value.Value))); using var sha=SHA256.Create(); return Hex(sha.ComputeHash(identity)); }
	static string DigestFile(string path) { using var stream=File.OpenRead(path); using var sha=SHA256.Create(); return Hex(sha.ComputeHash(stream)); }
	static string Digest(string value) { value=value.ToLowerInvariant(); if(value.Length!=64||value.Any(character=>!Uri.IsHexDigit(character))) throw new InvalidDataException("HookLab export digest is invalid."); return value; }
	static string Hex(byte[] value)=>String.Concat(value.Select(item=>item.ToString("x2")));
	static void TryDelete(string path) { try { if(Directory.Exists(path)) Directory.Delete(path,true); } catch { } }
}
