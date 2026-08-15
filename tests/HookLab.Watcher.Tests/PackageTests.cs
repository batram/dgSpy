using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookLab.Injector;
using Xunit;

namespace HookLab.Watcher.Tests;

public sealed class PackageTests {
	[Fact]
	public void Checked_in_vmconnect_deployment_is_closed_and_profile_bound() {
		var root=Path.Combine(RepoRoot(),"tools","HookLab.Watcher","deployments","vmconnect-fullscreen"); var definition=Assert.Single(ProfileCatalog.Load(root));
		Assert.Equal("vmconnect-fullscreen-user",definition.ProfileId); Assert.Equal("vmconnect-fullscreen-sync",definition.PackageId); Assert.Equal("vmconnect-fullscreen-sync-v1",definition.Value.Id);
	}

	[Fact]
	public void Package_loads_closed_inventory_and_returns_stable_digest() {
		using var directory=new TemporaryDirectory(); var package=WritePackage(directory.Path);
		var first=PackageLoader.Load(package); var second=PackageLoader.Load(package);
		Assert.Equal("sample-package",first.PackageId); Assert.Equal(first.Digest,second.Digest); Assert.Equal("sample-hook",first.Definition.Id); Assert.Contains("class H",first.Definition.Hook!.Source); Assert.False(first.Definition.Hook.Source!.EndsWith('\n'));
	}

	[Theory]
	[InlineData("undeclared")]
	[InlineData("missing")]
	[InlineData("digest")]
	[InlineData("traversal")]
	[InlineData("duplicate")]
	[InlineData("external-source")]
	public void Package_rejects_open_or_ambiguous_inventory(string mutation) {
		using var directory=new TemporaryDirectory(); var package=WritePackage(directory.Path); var manifestPath=Path.Combine(package,PackageLoader.ManifestName); var manifest=JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath),JsonOptions())!;
		switch(mutation) {
			case "undeclared": File.WriteAllText(Path.Combine(package,"extra.txt"),"extra"); break;
			case "missing": File.Delete(Path.Combine(package,"hook.cs")); break;
			case "digest": File.WriteAllText(Path.Combine(package,"hook.cs"),"changed"); break;
			case "traversal": manifest.Entries![0].Path="../hook.cs"; WriteManifest(manifestPath,manifest); break;
			case "duplicate": manifest.Entries!.Add(new PackageEntry { Path="HOOK.CS",Sha256=manifest.Entries[0].Sha256 }); WriteManifest(manifestPath,manifest); break;
			case "external-source": manifest.Hook!.SourcePath="../hook.cs"; WriteManifest(manifestPath,manifest); break;
		}
		Assert.Throws<InvalidDataException>(()=>PackageLoader.Load(package));
	}

	[Fact]
	public void Profile_binds_exact_package_digest_and_narrows_process_path() {
		using var directory=new TemporaryDirectory(); var package=WritePackage(directory.Path); var loaded=PackageLoader.Load(package); var allowed=Path.Combine(directory.Path,"allowed","Target.exe");
		WriteProfile(directory.Path,Profile(package,directory.Path,loaded,allowed));
		var definition=Assert.Single(ProfileCatalog.Load(directory.Path)); Assert.Equal("sample-profile",definition.ProfileId); Assert.Equal(loaded.Digest,definition.DefinitionSha256);
		var tracker=new CandidateTracker(new[]{definition},7);
		Assert.Empty(tracker.Select(new[]{new ProcessIdentity(1,1,"Target.exe",7,Path.Combine(directory.Path,"wrong","Target.exe"))}));
		Assert.Empty(tracker.Select(new[]{new ProcessIdentity(2,2,"Target.exe",7,null)}));
		Assert.Single(tracker.Select(new[]{new ProcessIdentity(3,3,"Target.exe",7,allowed)}));
	}

	[Fact]
	public void Profile_cannot_substitute_changed_package_content_or_enable_without_paths() {
		using var directory=new TemporaryDirectory(); var package=WritePackage(directory.Path); var loaded=PackageLoader.Load(package);
		var profile=Profile(package,directory.Path,loaded,Path.Combine(directory.Path,"Target.exe")); profile.PackageDigest=new string('0',64);
		WriteProfile(directory.Path,profile); Assert.Throws<InvalidDataException>(()=>ProfileCatalog.Load(directory.Path));
		profile.PackageDigest=loaded.Digest; profile.PermittedExecutablePaths!.Clear(); WriteProfile(directory.Path,profile); Assert.Throws<InvalidDataException>(()=>ProfileCatalog.Load(directory.Path));
	}

	[Fact]
	public void Disabled_profile_does_not_require_or_load_its_package() {
		using var directory=new TemporaryDirectory(); WriteProfile(directory.Path,new WatchProfile { SchemaVersion=1,Id="off",Enabled=false,Scope="user",PackagePath="missing",PackageId="missing",PackageDigest=new string('0',64) });
		Assert.Empty(ProfileCatalog.Load(directory.Path));
	}

	[Fact]
	public void Reloading_catalog_swaps_only_complete_valid_generations() {
		using var directory=new TemporaryDirectory(); var source=Path.Combine(RepoRoot(),"tools","HookLab.Watcher","deployments","vmconnect-fullscreen"); CopyTree(source,directory.Path);
		var catalog=new ReloadingProfileCatalog(directory.Path); var original=catalog.Current(); Assert.Single(original.Definitions);
		var sourceProfile=Assert.Single(Directory.GetFiles(source,"*.json",SearchOption.TopDirectoryOnly)); var profilePath=Path.Combine(directory.Path,Path.GetFileName(sourceProfile)); File.WriteAllText(profilePath,"{",new UTF8Encoding(false)); Thread.Sleep(550);
		var invalid=catalog.Current(); Assert.Equal(original.Generation,invalid.Generation); Assert.Single(invalid.Definitions); Assert.NotNull(invalid.Error);
		File.Copy(sourceProfile,profilePath,true); Thread.Sleep(550);
		var recovered=catalog.Current(); Assert.Single(recovered.Definitions); Assert.Null(recovered.Error);
	}

	[Fact]
	public void Profile_rejects_package_traversal_and_relative_executable_policy() {
		using var directory=new TemporaryDirectory();
		WriteProfile(directory.Path,new WatchProfile { SchemaVersion=1,Id="escape",Enabled=true,Scope="user",PackagePath="../package",PackageId="sample",PackageDigest=new string('0',64),PermittedExecutablePaths=new(){Path.Combine(directory.Path,"Target.exe")},NotificationPolicy="errors",ClrReadinessTimeoutMs=5000,InitializationTimeoutMs=10000 });
		Assert.Throws<InvalidDataException>(()=>ProfileCatalog.Load(directory.Path));
		var package=WritePackage(directory.Path); var loaded=PackageLoader.Load(package);
		var relative=Profile(package,directory.Path,loaded,"Target.exe"); WriteProfile(directory.Path,relative);
		Assert.Throws<InvalidDataException>(()=>ProfileCatalog.Load(directory.Path));
	}

	static string WritePackage(string parent) {
		var package=Path.Combine(parent,"package"); Directory.CreateDirectory(package); var sourcePath=Path.Combine(package,"hook.cs"); File.WriteAllText(sourcePath,"public static class H{public static bool Prefix(){return true;}}",new UTF8Encoding(false));
		var manifest=new PackageManifest { SchemaVersion=1,PackageId="sample-package",Revision=1,DisplayName="Sample",MinimumProtocolVersion=1,Architecture="x64",RuntimeFamily="clr-v4",Process=new ProcessDefinition { FileName="Target.exe" },Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Void Run()",IlSha256=new string('a',64) },Hook=new PackageHook { Id="sample-hook",Kind="Prefix",Revision=1,Enabled=true,SourcePath="hook.cs",MaximumEventsPerSecond=10,MaximumStringLength=100 },Entries=new(){new PackageEntry { Path="hook.cs",Sha256=Sha256(sourcePath) }} };
		WriteManifest(Path.Combine(package,PackageLoader.ManifestName),manifest); return package;
	}
	static void WriteProfile(string directory,WatchProfile profile)=>File.WriteAllText(Path.Combine(directory,"profile.json"),JsonSerializer.Serialize(profile,JsonOptions()),new UTF8Encoding(false));
	static WatchProfile Profile(string package,string root,LoadedPackage loaded,string allowed)=>new() { SchemaVersion=1,Id="sample-profile",Enabled=true,Scope="user",PackagePath=Path.GetRelativePath(root,package),PackageId=loaded.PackageId,PackageDigest=loaded.Digest,PermittedExecutablePaths=new(){allowed},NotificationPolicy="errors",ClrReadinessTimeoutMs=5000,InitializationTimeoutMs=10000 };
	static void WriteManifest(string path,PackageManifest manifest)=>File.WriteAllText(path,JsonSerializer.Serialize(manifest,JsonOptions()),new UTF8Encoding(false));
	static JsonSerializerOptions JsonOptions()=>new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
	static string Sha256(string path) { using var stream=File.OpenRead(path); using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
	static void CopyTree(string source,string destination) { foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,directory))); foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)) { var target=Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target); } }
	static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null&&!File.Exists(Path.Combine(current.FullName,"dnSpy.sln"))) current=current.Parent; return current?.FullName??throw new DirectoryNotFoundException(); }
	sealed class TemporaryDirectory : IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-package-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
