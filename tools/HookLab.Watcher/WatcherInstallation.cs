using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace HookLab.Watcher;

internal sealed record InstalledWatcherOptions(string Source,string InstallRoot,string StateRoot,bool RegisterTask,bool KeepState) {
	public static InstalledWatcherOptions Defaults(string? source=null,string? install=null,string? state=null,bool registerTask=true,bool keepState=false)=>new(
		Path.GetFullPath(source??AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),
		Path.GetFullPath(install??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","HookLab.Watcher")),
		Path.GetFullPath(state??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab")),registerTask,keepState);
}

internal interface IWatcherTaskScheduler {
	void Register(string executable,string arguments);
	void Remove();
	bool Matches(string executable,string arguments);
}

internal sealed class WindowsWatcherTaskScheduler:IWatcherTaskScheduler {
	internal const string TaskName="HookLab Watcher";
	public void Register(string executable,string arguments) {
		Run("/Create","/F","/TN",TaskName,"/SC","ONLOGON","/RL","HIGHEST","/IT","/TR",TaskCommandLine(executable,arguments));
		if(!Matches(executable,arguments)) throw new InvalidOperationException("Scheduled task readback differs from the requested HookLab watcher command.");
	}
	public void Remove() { if(Exists()) Run("/Delete","/F","/TN",TaskName); }
	public bool Matches(string executable,string arguments) {
		try { return MatchesXml(Capture("/Query","/TN",TaskName,"/XML"),executable,arguments); }
		catch { return false; }
	}
	internal static bool MatchesXml(string xml,string executable,string arguments) {
		var document=XDocument.Parse(xml); string? Value(string name)=>document.Descendants().FirstOrDefault(element=>element.Name.LocalName==name)?.Value;
		return String.Equals(Value("RunLevel"),"HighestAvailable",StringComparison.OrdinalIgnoreCase)&&
			String.Equals(Value("LogonType"),"InteractiveToken",StringComparison.OrdinalIgnoreCase)&&
			String.Equals(NormalizeCommand(Value("Command")),NormalizeCommand(executable),StringComparison.OrdinalIgnoreCase)&&
			String.Equals(NormalizeArguments(Value("Arguments")),NormalizeArguments(arguments),StringComparison.OrdinalIgnoreCase);
	}
	internal static string TaskCommandLine(string executable,string arguments)=>Quote(executable)+" "+arguments;
	static string NormalizeCommand(string? value)=>(value??String.Empty).Trim().Trim('"');
	static string NormalizeArguments(string? value)=>(value??String.Empty).Replace("\"",String.Empty,StringComparison.Ordinal).Trim();
	bool Exists() { try { Capture("/Query","/TN",TaskName); return true; } catch { return false; } }
	static void Run(params string[] arguments)=>Capture(arguments);
	static string Capture(params string[] arguments) {
		var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
		foreach(var argument in arguments) info.ArgumentList.Add(argument);
		using var process=Process.Start(info)??throw new InvalidOperationException("Could not start Task Scheduler command.");
		var output=process.StandardOutput.ReadToEnd(); var error=process.StandardError.ReadToEnd(); process.WaitForExit();
		if(process.ExitCode!=0) throw new InvalidOperationException("Task Scheduler operation failed: "+Sanitize(error));
		return output;
	}
	static string Quote(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ').Trim();
}

internal sealed class WatcherInstaller {
	const string ManifestName="hooklab-watcher-install.json";
	readonly IWatcherTaskScheduler scheduler;
	public WatcherInstaller(IWatcherTaskScheduler? scheduler=null)=>this.scheduler=scheduler??new WindowsWatcherTaskScheduler();

	public object Install(InstalledWatcherOptions options) {
		var source=VerifySource(options.Source); var parent=Directory.GetParent(options.InstallRoot)?.FullName??throw new InvalidDataException("Install root has no parent.");
		Directory.CreateDirectory(parent); Directory.CreateDirectory(options.StateRoot);
		var staging=options.InstallRoot+".staging-"+Guid.NewGuid().ToString("N"); var backup=options.InstallRoot+".previous-"+Guid.NewGuid().ToString("N"); var taskExecutable=TaskExecutable(); var command=TaskArguments(options.InstallRoot,options.StateRoot);
		var legacyExecutable=Path.Combine(options.InstallRoot,"HookLab.Watcher.exe"); var priorHiddenTask=scheduler.Matches(taskExecutable,command); var priorLegacyTask=!priorHiddenTask&&scheduler.Matches(legacyExecutable,"run-installed");
		try {
			CopyClosedTree(options.Source,staging,source); ProtectTree(staging); VerifyInstalled(staging);
			StopInstalledWatchers(options.InstallRoot);
			if(Directory.Exists(options.InstallRoot)) Directory.Move(options.InstallRoot,backup);
			try {
				Directory.Move(staging,options.InstallRoot);
				if(options.RegisterTask) scheduler.Register(taskExecutable,command);
				VerifyInstalled(options.InstallRoot);
				if(options.RegisterTask&&!scheduler.Matches(taskExecutable,command)) throw new InvalidOperationException("Scheduled task verification failed.");
			}
			catch { if(options.RegisterTask) TryRemoveTask(); TryDelete(options.InstallRoot); if(Directory.Exists(backup)) Directory.Move(backup,options.InstallRoot); if(priorHiddenTask) scheduler.Register(taskExecutable,command); else if(priorLegacyTask) scheduler.Register(legacyExecutable,"run-installed"); throw; }
			TryDelete(backup);
			return new { status="installed",installRoot=options.InstallRoot,stateRoot=options.StateRoot,taskRegistered=options.RegisterTask,fileCount=source.Files.Length };
		}
		finally { TryDelete(staging); }
	}

	public object Verify(InstalledWatcherOptions options) {
		var manifest=VerifyInstalled(options.InstallRoot); var command=TaskArguments(options.InstallRoot,options.StateRoot);
		return new { status="valid",installRoot=options.InstallRoot,fileCount=manifest.Files.Length,taskRegistered=scheduler.Matches(TaskExecutable(),command) };
	}

	public object Uninstall(InstalledWatcherOptions options) {
		scheduler.Remove(); StopInstalledWatchers(options.InstallRoot); TryDelete(options.InstallRoot); if(!options.KeepState) TryDelete(options.StateRoot);
		return new { status="uninstalled",installRoot=options.InstallRoot,stateRemoved=!options.KeepState };
	}

	internal static string TaskExecutable()=>Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
	internal static string TaskArguments(string installRoot,string stateRoot)=>"-NoProfile -NonInteractive -WindowStyle Hidden -File "+Quote(Path.Combine(installRoot,"run-installed.ps1"));
	static InstallManifest VerifySource(string source) {
		var root=Path.GetFullPath(source); var layout=Directory.GetParent(root)?.FullName??throw new InvalidDataException("Watcher source is not inside a dgSpy layout.");
		var layoutPath=Path.Combine(layout,"dgspy-layout.json"); if(!File.Exists(layoutPath)) throw new InvalidDataException("Watcher source is missing its authoritative dgSpy layout manifest.");
		using var document=JsonDocument.Parse(File.ReadAllBytes(layoutPath));
		var files=document.RootElement.GetProperty("files").EnumerateArray().Where(item=>item.GetProperty("owner").GetString()=="hooklab-watcher").Select(item=>new InstallFile(
			Path.GetRelativePath(root,Path.Combine(layout,item.GetProperty("path").GetString()!)).Replace('\\','/'),item.GetProperty("size").GetInt64(),item.GetProperty("sha256").GetString()!)).OrderBy(item=>item.Path,StringComparer.Ordinal).ToArray();
		if(files.Length==0||files.Any(file=>file.Path.StartsWith("../",StringComparison.Ordinal)||Path.IsPathRooted(file.Path))) throw new InvalidDataException("dgSpy layout does not contain a closed HookLab watcher inventory.");
		var manifest=new InstallManifest(1,files); VerifyFiles(root,manifest,false); return manifest;
	}
	static InstallManifest VerifyInstalled(string root) {
		var path=Path.Combine(root,ManifestName); if(!File.Exists(path)) throw new InvalidDataException("Installed watcher manifest is missing.");
		var manifest=JsonSerializer.Deserialize<InstallManifest>(File.ReadAllBytes(path))??throw new InvalidDataException("Installed watcher manifest is invalid.");
		if(manifest.FormatVersion!=1) throw new InvalidDataException("Installed watcher manifest version is unsupported."); VerifyFiles(root,manifest,true); return manifest;
	}
	static void VerifyFiles(string root,InstallManifest manifest,bool requireClosed) {
		if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Watcher root may not be a reparse point.");
		VerifyAcl(root);
		var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach(var file in manifest.Files) {
			if(!seen.Add(file.Path)||file.Path.Contains("..",StringComparison.Ordinal)||Path.IsPathRooted(file.Path)) throw new InvalidDataException("Watcher inventory contains an unsafe or duplicate path.");
			var path=Path.GetFullPath(Path.Combine(root,file.Path)); if(!path.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Watcher inventory escapes its root.");
			for(var parent=Directory.GetParent(path);parent is not null&&parent.FullName.StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase);parent=parent.Parent) if((parent.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Watcher inventory traverses a reparse point.");
			var info=new FileInfo(path); if(!info.Exists||info.Length!=file.Size||Hash(path)!=file.Sha256) throw new InvalidDataException("Watcher file differs from its manifest: "+file.Path);
			VerifyAcl(path);
		}
		if(requireClosed) { var actual=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>Path.GetRelativePath(root,path).Replace('\\','/')).Where(path=>path!=ManifestName).OrderBy(path=>path,StringComparer.Ordinal).ToArray(); var expected=manifest.Files.Select(file=>file.Path).OrderBy(path=>path,StringComparer.Ordinal).ToArray(); if(!actual.SequenceEqual(expected,StringComparer.Ordinal)) throw new InvalidDataException("Installed watcher inventory is not closed."); }
	}
	static void VerifyAcl(string path) {
		if(!OperatingSystem.IsWindows()) return;
		FileSystemSecurity security=Directory.Exists(path)?FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path)):FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
		var owner=security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; var current=WindowsIdentity.GetCurrent().User;
		var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null); var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
		if(owner is null||owner!=current&&owner!=administrators&&owner!=system) throw new UnauthorizedAccessException("Watcher input has an untrusted owner: "+path);
		var broad=new HashSet<string>(StringComparer.Ordinal) { new SecurityIdentifier(WellKnownSidType.WorldSid,null).Value, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null).Value, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null).Value };
		foreach(var rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()) if(rule.AccessControlType==AccessControlType.Allow&&broad.Contains(rule.IdentityReference.Value)&&(rule.FileSystemRights&(FileSystemRights.Write|FileSystemRights.Modify|FileSystemRights.FullControl|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))!=0) throw new UnauthorizedAccessException("Watcher input grants broad write access: "+path);
	}
	static void CopyClosedTree(string source,string destination,InstallManifest manifest) { Directory.CreateDirectory(destination); foreach(var file in manifest.Files) { var target=Path.Combine(destination,file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(source,file.Path),target); } File.WriteAllBytes(Path.Combine(destination,ManifestName),JsonSerializer.SerializeToUtf8Bytes(manifest)); }
	static void ProtectTree(string root) {
		if(!OperatingSystem.IsWindows()) return;
		var identities=new IdentityReference[]{WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException("Current Windows identity has no SID."),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)};
		var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false); foreach(var identity in identities) security.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow)); FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root),security);
		foreach(var directory in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories)) { var value=new DirectorySecurity(); value.SetAccessRuleProtection(false,false); FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory),value); }
		foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) { var value=new FileSecurity(); value.SetAccessRuleProtection(false,false); FileSystemAclExtensions.SetAccessControl(new FileInfo(file),value); }
	}
	static void StopInstalledWatchers(string root) { var executable=Path.GetFullPath(Path.Combine(root,"HookLab.Watcher.exe")); foreach(var process in Process.GetProcessesByName("HookLab.Watcher")) using(process) { try { if(String.Equals(process.MainModule?.FileName,executable,StringComparison.OrdinalIgnoreCase)) { process.Kill(); if(!process.WaitForExit(10000)) throw new InvalidOperationException("Installed watcher did not stop."); } } catch(ArgumentException) { } catch(System.ComponentModel.Win32Exception) { } } }
	void TryRemoveTask() { try { scheduler.Remove(); } catch { } }
	static void TryDelete(string path) { if(Directory.Exists(path)) Directory.Delete(path,true); }
	static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
	static string Quote(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
}

internal sealed record InstallManifest(int FormatVersion,InstallFile[] Files);
internal sealed record InstallFile(string Path,long Size,string Sha256);
