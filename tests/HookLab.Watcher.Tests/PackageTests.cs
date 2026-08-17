using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookLab.Injector;
using HookLab.Packaging;
using Xunit;

namespace HookLab.Watcher.Tests;

public sealed class PackageTests {
	[Fact]
	public void Aggregate_deployments_root_loads_immediate_closed_deployment_directories() {
		var root=Path.Combine(RepoRoot(),"tools","HookLab.Watcher","deployments"); var definitions=ProfileCatalog.Load(root);
		Assert.Equal(new[]{"vmconnect-fullscreen-user","vmconnect-status-codex-was-here-user"},definitions.Select(definition=>definition.ProfileId).OrderBy(id=>id,StringComparer.Ordinal));
	}

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

	[Fact]
	public void DgSpy_export_is_a_canonical_package_with_an_explicitly_disabled_profile() {
		using var directory=new TemporaryDirectory(); var deployment=Path.Combine(directory.Path,"exported"); var mvid=Guid.NewGuid().ToString("D");
		var request=new HookPackageExportRequest { PackageId="spy-snack",ProfileId="spy-snack-user",DisplayName="Spy snack",PackageRevision=2,ProcessFileName="Target.exe",PermittedExecutablePath=Path.Combine(directory.Path,"Target.exe"),Assembly="Target",ModuleMvid=mvid,DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Void Run()",IlSha256=new string('a',64),HookId="spy-hook",HookKind="Prefix",HookRevision=2,HookEnabled=true,Source="public static class H{public static void Prefix(){}}",MaximumEventsPerSecond=10,MaximumStringLength=128 };
		var exported=HookPackageExporter.Export(deployment,request);
		var package=PackageLoader.Load(exported.PackagePath); Assert.Equal("spy-snack",package.PackageId); Assert.Equal(exported.PackageDigest,package.Digest); Assert.Equal(mvid,package.Definition.Target!.ModuleMvid); Assert.Equal("spy-hook",package.Definition.Id);
		using var profile=JsonDocument.Parse(File.ReadAllBytes(exported.ProfilePath)); Assert.False(profile.RootElement.GetProperty("enabled").GetBoolean()); Assert.Equal(exported.PackageDigest,profile.RootElement.GetProperty("packageDigest").GetString()); Assert.Empty(ProfileCatalog.Load(deployment));
		Assert.Throws<IOException>(()=>HookPackageExporter.Export(deployment,request,overwrite:false));
		request.DisplayName="Updated spy snack"; var replaced=HookPackageExporter.Export(deployment,request,overwrite:true); Assert.NotEqual(exported.PackageDigest,replaced.PackageDigest); Assert.Equal(replaced.PackageDigest,PackageLoader.Load(replaced.PackagePath).Digest); Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path),path=>path.Contains(".previous-",StringComparison.Ordinal)||path.Contains(".staging-",StringComparison.Ordinal));
	}

	[Fact]
	public void Export_protection_is_applied_before_publication_and_failure_preserves_previous() {
		using var directory=new TemporaryDirectory(); var deployment=Path.Combine(directory.Path,"exported"); var first=Export(deployment,"protected-profile","protected-package","protected-hook",1); var called=false;
		var request=new HookPackageExportRequest { PackageId="protected-package",ProfileId="protected-profile",DisplayName="replacement",PackageRevision=2,ProcessFileName="Target.exe",PermittedExecutablePath=Path.Combine(directory.Path,"Target.exe"),Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Void Run()",IlSha256=new string('a',64),HookId="protected-hook",HookKind="Prefix",HookRevision=2,HookEnabled=true,Source="public static class H{public static void Prefix(){}}",MaximumEventsPerSecond=10,MaximumStringLength=128 };
		Assert.Throws<UnauthorizedAccessException>(()=>HookPackageExporter.Export(deployment,request,overwrite:true,protectStaging:path=>{ called=Directory.Exists(path); throw new UnauthorizedAccessException("injected ACL failure"); }));
		Assert.True(called); Assert.Equal(first.PackageDigest,PackageLoader.Load(first.PackagePath).Digest); Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path),path=>path.Contains(".staging-",StringComparison.Ordinal)||path.Contains(".previous-",StringComparison.Ordinal));
	}

	[Fact]
	public void Enrollment_publishes_verified_export_but_keeps_it_operator_disabled() {
		using var directory=new TemporaryDirectory(); var exported=Export(Path.Combine(directory.Path,"source"),"enrolled-profile","enrolled-package","enrolled-hook",1); var enrollment=Path.Combine(directory.Path,"enrolled"); var builtIn=Path.Combine(directory.Path,"built-in"); var controlPath=Path.Combine(directory.Path,"state","control.json"); Directory.CreateDirectory(builtIn);
		var result=JsonSerializer.SerializeToElement(new WatcherEnrollmentService().Enroll(EnrollmentOptions.Defaults(exported.DeploymentPath,enrollment,builtIn,controlPath)));
		Assert.Equal("enrolled",result.GetProperty("status").GetString()); Assert.Equal(exported.PackageDigest,result.GetProperty("packageDigest").GetString()); Assert.False(result.GetProperty("profileEnabled").GetBoolean()); Assert.Equal(64,result.GetProperty("catalogGeneration").GetString()!.Length);
		var definition=Assert.Single(ProfileCatalog.Load(enrollment)); Assert.Equal("enrolled-profile",definition.ProfileId); Assert.Equal(exported.PackageDigest,definition.DefinitionSha256);
		var control=new WatchControlStore(controlPath); Assert.Contains("enrolled-profile",control.Read().DisabledProfiles); control.Update(enableProfile:"enrolled-profile"); Assert.DoesNotContain("enrolled-profile",control.Read().DisabledProfiles);
	}

	[Fact]
	public void Enrollment_refuses_same_profile_id_and_open_inventory() {
		using var directory=new TemporaryDirectory(); var exported=Export(Path.Combine(directory.Path,"source"),"same-profile","same-package","same-hook",1); var enrollment=Path.Combine(directory.Path,"enrolled"); var builtIn=Path.Combine(directory.Path,"built-in"); var control=Path.Combine(directory.Path,"control.json"); Directory.CreateDirectory(builtIn); var service=new WatcherEnrollmentService(); var options=EnrollmentOptions.Defaults(exported.DeploymentPath,enrollment,builtIn,control);
		service.Enroll(options); Assert.Throws<InvalidOperationException>(()=>service.Enroll(options));
		var open=Export(Path.Combine(directory.Path,"open"),"open-profile","open-package","open-hook",1); File.WriteAllText(Path.Combine(open.DeploymentPath,"undeclared.txt"),"no"); Assert.Throws<InvalidDataException>(()=>service.Enroll(EnrollmentOptions.Defaults(open.DeploymentPath,enrollment,builtIn,control)));
	}

	[Fact]
	public void Failed_replacement_restores_last_good_deployment_and_operator_state() {
		using var directory=new TemporaryDirectory(); var first=Export(Path.Combine(directory.Path,"first"),"replace-profile","replace-package","replace-hook",1); var second=Export(Path.Combine(directory.Path,"second"),"replace-profile","replace-package","replace-hook",2); var enrollment=Path.Combine(directory.Path,"enrolled"); var builtIn=Path.Combine(directory.Path,"built-in"); var controlPath=Path.Combine(directory.Path,"control.json"); Directory.CreateDirectory(builtIn); var options=EnrollmentOptions.Defaults(first.DeploymentPath,enrollment,builtIn,controlPath);
		new WatcherEnrollmentService().Enroll(options); var control=new WatchControlStore(controlPath); control.Update(enableProfile:"replace-profile"); var original=Assert.Single(ProfileCatalog.Load(enrollment)).DefinitionSha256;
		var failure=new WatcherEnrollmentService((source,destination)=>throw new IOException("injected publication failure")); Assert.Throws<IOException>(()=>failure.Enroll(EnrollmentOptions.Defaults(second.DeploymentPath,enrollment,builtIn,controlPath,replace:true)));
		Assert.Equal(original,Assert.Single(ProfileCatalog.Load(enrollment)).DefinitionSha256); Assert.DoesNotContain("replace-profile",control.Read().DisabledProfiles); Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path,"*",SearchOption.AllDirectories),path=>path.Contains(".previous-",StringComparison.Ordinal)||path.Contains(".staging-",StringComparison.Ordinal));
	}

	[Fact]
	public void Explicit_replacement_atomically_publishes_the_new_digest_disabled() {
		using var directory=new TemporaryDirectory(); var first=Export(Path.Combine(directory.Path,"first"),"replace-profile","replace-package","replace-hook",1); var second=Export(Path.Combine(directory.Path,"second"),"replace-profile","replace-package","replace-hook",2); var enrollment=Path.Combine(directory.Path,"enrolled"); var builtIn=Path.Combine(directory.Path,"built-in"); var controlPath=Path.Combine(directory.Path,"control.json"); Directory.CreateDirectory(builtIn);
		new WatcherEnrollmentService().Enroll(EnrollmentOptions.Defaults(first.DeploymentPath,enrollment,builtIn,controlPath)); new WatchControlStore(controlPath).Update(enableProfile:"replace-profile");
		var result=JsonSerializer.SerializeToElement(new WatcherEnrollmentService().Enroll(EnrollmentOptions.Defaults(second.DeploymentPath,enrollment,builtIn,controlPath,replace:true)));
		Assert.Equal(second.PackageDigest,result.GetProperty("packageDigest").GetString()); Assert.Equal(second.PackageDigest,Assert.Single(ProfileCatalog.Load(enrollment)).DefinitionSha256); Assert.Contains("replace-profile",new WatchControlStore(controlPath).Read().DisabledProfiles); Assert.DoesNotContain(Directory.EnumerateDirectories(directory.Path,"*",SearchOption.AllDirectories),path=>path.Contains(".previous-",StringComparison.Ordinal)||path.Contains(".staging-",StringComparison.Ordinal));
	}

	[Fact]
	public void Enrollment_rejects_hook_identity_conflict_with_built_in_catalog() {
		using var directory=new TemporaryDirectory(); var builtInExport=Export(Path.Combine(directory.Path,"built-in","existing"),"built-in-profile","built-in-package","shared-hook",1); EnableExportedProfile(builtInExport.ProfilePath); var incoming=Export(Path.Combine(directory.Path,"incoming"),"incoming-profile","incoming-package","shared-hook",1); var control=Path.Combine(directory.Path,"control.json");
		Assert.Throws<InvalidOperationException>(()=>new WatcherEnrollmentService().Enroll(EnrollmentOptions.Defaults(incoming.DeploymentPath,Path.Combine(directory.Path,"enrolled"),Path.Combine(directory.Path,"built-in"),control)));
		Assert.False(File.Exists(control));
	}

	[Fact]
	public void Enrollment_command_parses_only_explicit_bounded_options() {
		Assert.True(EnrollmentCommand.TryParse(new[]{"enroll","--deployment","export","--replace","--state-root","state"},out var command)); Assert.True(command.Options.Replace); Assert.EndsWith(Path.Combine("state","watcher-control.json"),command.Options.ControlPath,StringComparison.OrdinalIgnoreCase);
		Assert.False(EnrollmentCommand.TryParse(new[]{"enroll","--deployment","one","--deployment","two"},out _)); Assert.False(EnrollmentCommand.TryParse(new[]{"enroll","--replace"},out _));
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
		var definition=Assert.Single(ProfileCatalog.Load(directory.Path)); Assert.Equal("sample-profile",definition.ProfileId); Assert.Equal(loaded.Digest,definition.DefinitionSha256); Assert.Equal("errors",definition.NotificationPolicy); Assert.Equal(5000,definition.ClrReadinessTimeoutMs); Assert.Equal(10000,definition.InitializationTimeoutMs);
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
	static HookPackageExportResult Export(string deployment,string profileId,string packageId,string hookId,int revision) {
		var request=new HookPackageExportRequest { PackageId=packageId,ProfileId=profileId,DisplayName=profileId,PackageRevision=revision,ProcessFileName="Target.exe",PermittedExecutablePath=Path.Combine(Path.GetDirectoryName(deployment)!,"Target.exe"),Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Void Run()",IlSha256=new string('a',64),HookId=hookId,HookKind="Prefix",HookRevision=revision,HookEnabled=true,Source="public static class H{public static void Prefix(){}}",MaximumEventsPerSecond=10,MaximumStringLength=128 };
		return HookPackageExporter.Export(deployment,request);
	}
	static void EnableExportedProfile(string path) { var profile=JsonSerializer.Deserialize<WatchProfile>(File.ReadAllBytes(path),JsonOptions())!; profile.Enabled=true; ProfileCatalog.Write(path,profile); }
	static void WriteProfile(string directory,WatchProfile profile)=>File.WriteAllText(Path.Combine(directory,"profile.json"),JsonSerializer.Serialize(profile,JsonOptions()),new UTF8Encoding(false));
	static WatchProfile Profile(string package,string root,LoadedPackage loaded,string allowed)=>new() { SchemaVersion=1,Id="sample-profile",Enabled=true,Scope="user",PackagePath=Path.GetRelativePath(root,package),PackageId=loaded.PackageId,PackageDigest=loaded.Digest,PermittedExecutablePaths=new(){allowed},NotificationPolicy="errors",ClrReadinessTimeoutMs=5000,InitializationTimeoutMs=10000 };
	static void WriteManifest(string path,PackageManifest manifest)=>File.WriteAllText(path,JsonSerializer.Serialize(manifest,JsonOptions()),new UTF8Encoding(false));
	static JsonSerializerOptions JsonOptions()=>new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
	static string Sha256(string path) { using var stream=File.OpenRead(path); using var sha=SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
	static void CopyTree(string source,string destination) { foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,directory))); foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)) { var target=Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target); } }
	static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null&&!File.Exists(Path.Combine(current.FullName,"dnSpy.sln"))) current=current.Parent; return current?.FullName??throw new DirectoryNotFoundException(); }
	sealed class TemporaryDirectory : IDisposable { public string Path { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hooklab-package-tests-"+Guid.NewGuid().ToString("N")); public TemporaryDirectory()=>Directory.CreateDirectory(Path); public void Dispose()=>Directory.Delete(Path,true); }
}
