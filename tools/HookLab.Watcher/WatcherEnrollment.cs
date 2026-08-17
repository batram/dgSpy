namespace HookLab.Watcher;

internal sealed record EnrollmentOptions(string DeploymentRoot,string EnrollmentRoot,string BuiltInProfilesRoot,string ControlPath,bool Replace) {
	public static EnrollmentOptions Defaults(string deploymentRoot,string? enrollmentRoot=null,string? builtInProfilesRoot=null,string? controlPath=null,bool replace=false) {
		var programs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs");
		return new(Path.GetFullPath(deploymentRoot),Path.GetFullPath(enrollmentRoot??Path.Combine(programs,"HookLab.Watcher.Enrolled")),Path.GetFullPath(builtInProfilesRoot??Path.Combine(AppContext.BaseDirectory,"deployments")),Path.GetFullPath(controlPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-control.json")),replace);
	}
}

internal sealed class WatcherEnrollmentService {
	readonly Action<string,string> move;
	public WatcherEnrollmentService(Action<string,string>? move=null)=>this.move=move??Directory.Move;

	public object Enroll(EnrollmentOptions options) {
		var source=Path.TrimEndingDirectorySeparator(options.DeploymentRoot); VerifySourceTree(source);
		var exported=ProfileCatalog.LoadExport(source); var profile=exported.Profile; var profileId=profile.Id!;
		if(profileId.IndexOfAny(Path.GetInvalidFileNameChars())>=0||profileId is "." or "..") throw new InvalidDataException("Exported profile ID is not a safe enrollment directory name.");
		var root=Path.TrimEndingDirectorySeparator(options.EnrollmentRoot); Directory.CreateDirectory(root); WatcherInstaller.ProtectTree(root); WatcherInstaller.VerifyAcl(root);
		var destination=Path.GetFullPath(Path.Combine(root,profileId)); if(!destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Enrollment destination escapes its root.");
		if(Directory.Exists(destination)&&!options.Replace) throw new InvalidOperationException("Enrollment conflicts with existing profile ID: "+profileId);
		var parent=Directory.GetParent(root)?.FullName??throw new InvalidDataException("Enrollment root has no parent."); var nonce=Guid.NewGuid().ToString("N"); var staging=Path.Combine(parent,"."+Path.GetFileName(root)+".staging-"+nonce); var backup=Path.Combine(parent,"."+Path.GetFileName(root)+".previous-"+nonce);
		var control=new WatchControlStore(options.ControlPath); var controlExisted=control.Exists(); var previousControl=control.Read(); var published=false;
		try {
			CopyTree(source,staging); var stagedProfile=Path.Combine(staging,Path.GetFileName(exported.ProfilePath)); profile.Enabled=true; ProfileCatalog.Write(stagedProfile,profile); WatcherInstaller.ProtectTree(staging); VerifyAclTree(staging);
			var staged=AssertSingle(ProfileCatalog.Load(staging),profileId); var existing=ProfileCatalog.Load(root,true).Where(value=>value.ProfileId!=profileId).ToArray(); var builtIn=Directory.Exists(options.BuiltInProfilesRoot)?ProfileCatalog.Load(options.BuiltInProfilesRoot,true):Array.Empty<WatchDefinition>(); var candidate=builtIn.Concat(existing).Append(staged).ToArray(); ValidateCombined(candidate);
			control.Update(disableProfile:profileId);
			if(Directory.Exists(destination)) Directory.Move(destination,backup);
			try { move(staging,destination); published=true; }
			catch { if(Directory.Exists(backup)) Directory.Move(backup,destination); throw; }
			var enrolled=ProfileCatalog.Load(root,true); var combined=builtIn.Concat(enrolled).ToArray(); ValidateCombined(combined); var readback=AssertSingle(enrolled,profileId); if(readback.DefinitionSha256!=exported.Package.Digest) throw new InvalidDataException("Enrolled package digest readback differs from the verified export.");
			TryDelete(backup);
			return new { status="enrolled",profileId,packageId=exported.Package.PackageId,packageDigest=exported.Package.Digest,enrollmentPath=destination,profileEnabled=false,catalogGeneration=StaticWatchCatalog.Generation(combined),definitionId=readback.Value.Id };
		}
		catch {
			if(published) { TryDelete(destination); if(Directory.Exists(backup)) Directory.Move(backup,destination); }
			if(controlExisted) control.Replace(previousControl); else control.Delete(); throw;
		}
		finally { TryDelete(staging); }
	}

	static WatchDefinition AssertSingle(IReadOnlyList<WatchDefinition> definitions,string profileId) {
		var matches=definitions.Where(value=>value.ProfileId==profileId).ToArray(); if(matches.Length!=1) throw new InvalidDataException("Enrollment readback did not contain exactly one profile: "+profileId); return matches[0];
	}
	static void ValidateCombined(IReadOnlyList<WatchDefinition> definitions) {
		var duplicateProfile=definitions.GroupBy(value=>value.ProfileId,StringComparer.Ordinal).FirstOrDefault(group=>group.Key is not null&&group.Count()>1); if(duplicateProfile is not null) throw new InvalidOperationException("Enrollment profile ID conflicts with the active catalog: "+duplicateProfile.Key);
		var duplicateHook=definitions.GroupBy(value=>value.Value.Id,StringComparer.Ordinal).FirstOrDefault(group=>group.Key is not null&&group.Count()>1); if(duplicateHook is not null) throw new InvalidOperationException("Enrollment hook ID conflicts with the active catalog: "+duplicateHook.Key);
	}
	static void VerifySourceTree(string root) {
		var actual=VerifyAclTree(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var exported=ProfileCatalog.LoadExport(root); var packageRoot=exported.Package.Root; var expected=VerifyAclTree(packageRoot).Append(exported.ProfilePath).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase); if(!actual.SetEquals(expected)) throw new InvalidDataException("Exported deployment inventory is not closed.");
	}
	static IReadOnlyList<string> VerifyAclTree(string root) {
		if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Enrollment deployment does not exist: "+root); if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Enrollment deployment root may not be a reparse point."); WatcherInstaller.VerifyAcl(root);
		var files=new List<string>(); var pending=new Stack<string>(); pending.Push(root);
		while(pending.Count>0) foreach(var path in Directory.EnumerateFileSystemEntries(pending.Pop(),"*",SearchOption.TopDirectoryOnly)) {
			var attributes=File.GetAttributes(path); if((attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Enrollment deployment contains a reparse point: "+path);
			WatcherInstaller.VerifyAcl(path); if((attributes&FileAttributes.Directory)!=0) pending.Push(path); else files.Add(Path.GetFullPath(path));
		}
		return files;
	}
	static void CopyTree(string source,string destination) { Directory.CreateDirectory(destination); foreach(var file in VerifyAclTree(source)) { var target=Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target); } }
	static void TryDelete(string path) { try { if(Directory.Exists(path)) Directory.Delete(path,true); } catch { } }
}
