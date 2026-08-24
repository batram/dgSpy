using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

return await DgSpyBuildTool.RunAsync(args);

internal static class DgSpyBuildTool {
	// Every assembly the extension needs at run time, named rather than globbed, so a build-output
	// change cannot quietly add a file to the packaged host. HookLab.Injector joined this list when the
	// extension started consuming the injector library instead of keeping a private copy of native
	// loading; leaving it out packaged an extension that loaded, composed and served every other
	// request, and threw FileNotFoundException at the first initialize_hooklab.
	// internal so the pipeline tests build their fixture from this list rather than restating it. It was
	// restated, and adding one entry here turned an unrelated compose test red for no reason a reader
	// could see.
	internal static readonly string[] ExtensionFiles={"dgSpy.Extension.x.dll","dgSpy.Extension.x.pdb","dgSpy.Protocol.dll","dgSpy.Protocol.pdb","HookLab.Contracts.dll","HookLab.Contracts.pdb","HookLab.Packaging.dll","HookLab.Packaging.pdb","HookLab.Host.Transport.dll","HookLab.Host.Transport.pdb","HookLab.Injector.dll","HookLab.Injector.pdb"};
	static readonly HashSet<string> FrameworkOverrides=new(StringComparer.OrdinalIgnoreCase){"Microsoft.VisualBasic.dll","System.Diagnostics.EventLog.dll","System.Drawing.dll","System.Security.Cryptography.Pkcs.dll","System.Security.Cryptography.Xml.dll","WindowsBase.dll"};
	const string ManifestName="dgspy-layout.json";
	internal const string PayloadFileName="hooklab-bootstrap.net48.payload";
	internal const string PayloadMatrixFileName="hooklab-payload-matrix.json";

	public static Task<int> RunAsync(string[] arguments) {
		try {
			if(arguments.Length==0) return Task.FromResult(Help());
			if(arguments.Length>=1 && arguments[0].ToLowerInvariant() is "codex" or "claude") {
				Install(SimpleInstallOptions(arguments[0],arguments.Skip(1),AppContext.BaseDirectory,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))); return Task.FromResult(0);
			}
			if(arguments.Length==1 && arguments[0].Equals("host-only",StringComparison.OrdinalIgnoreCase)) {
				Install(HostOnlyInstallOptions(AppContext.BaseDirectory,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))); return Task.FromResult(0);
			}
			var options=Options.Parse(arguments.Skip(1).ToArray());
				switch(arguments[0].ToLowerInvariant()) {
				case "pipeline": Pipeline(options); break;
				case "prune": Prune(options); break;
				case "build": Build(options); break;
				case "build-host": BuildHost(options); break;
				case "build-components": BuildComponents(options); break;
				case "compose": Compose(options); break;
				case "verify": Verify(options.Required("layout")); break;
				case "package": Package(options); break;
				case "verify-package": VerifyPackage(options.Required("package")); break;
				case "install": Install(options); break;
				case "snapshot": Snapshot(options); break;
				default: return Task.FromResult(Help());
			}
			return Task.FromResult(0);
		}
		catch(Exception ex) { Console.Error.WriteLine("dgspy-build: "+ex.Message); return Task.FromResult(1); }
	}

	internal static Options SimpleInstallOptions(string agent,IEnumerable<string> flags,string packageDirectory,string localAppData) {
		var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
			{"package",packageDirectory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)},
			{"install",Path.Combine(localAppData,"Programs","dgSpyMcp")},
			{"agent",agent.ToLowerInvariant()},
		};
		if(flags.Any(value=>value.Equals("--force",StringComparison.OrdinalIgnoreCase))) values["force"]="true";
		return new Options(values);
	}

	internal static Options HostOnlyInstallOptions(string packageDirectory,string localAppData)=>new(new(StringComparer.OrdinalIgnoreCase) {
		{"package",packageDirectory.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)},
		{"install",Path.Combine(localAppData,"Programs","dgSpyMcp")},
		{"host-only","true"},
	});

	static int Help() { Console.WriteLine("install-dgspy codex|claude [--force]\ninstall-dgspy host-only\nDgSpyTool pipeline|prune|build|build-host|build-components|compose|verify|package|verify-package|install|snapshot"); return 2; }

	/// <summary>The four artifact areas the pipeline writes, each keyed by build id.</summary>
	internal static readonly string[] BuildIdScopedAreas={ "layouts",@"packages\dgspy-win-x64","host-raw","dgspy-components" };

	/// <summary>
	/// Ages out single-use build outputs.
	///
	/// The pipeline is immutable by design: it never overwrites a layout or a package, which is what
	/// makes a build reproducible and a verification meaningful. Immutable with no retention is
	/// unbounded growth, though, and one day of gate runs is about 25 GB - each run leaves a layout, a
	/// package, a raw host and a component tree behind, and nothing ever removed them.
	///
	/// Only build ids that look single-use are eligible: a name containing a 32-character hex run, which
	/// is what a generated id like <c>gate-3f9c...</c> has and what a deliberate one like <c>local</c> or
	/// <c>glorpy</c> does not. That distinction is the point. A generated id is used once and never named
	/// again; a named id is reused and may be referenced by something outside this tool, so it is never
	/// touched however old it looks.
	/// </summary>
	static void Prune(Options options) {
		var artifacts=Full(options.Value("artifacts") ?? Path.Combine(Full(options.Value("repo") ?? Environment.CurrentDirectory),"artifacts"));
		var keep=Int32.TryParse(options.Value("keep"),NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed)?parsed:3;
		if(keep<1) throw new ArgumentException("--keep must be at least 1; retaining nothing would delete the build a gate just verified.");
		var dryRun=options.Flag("dry-run");
		// Never remove the layout a caller is actively using, whatever its age or name.
		var inUse=Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		var protectedPath=String.IsNullOrWhiteSpace(inUse)?null:Full(inUse);
		long reclaimed=0; var removed=0; var kept=0;
		foreach(var area in BuildIdScopedAreas) {
			var root=Path.Combine(artifacts,area);
			if(!Directory.Exists(root)) continue;
			var candidates=new DirectoryInfo(root).GetDirectories()
				.Where(directory=>IsSingleUseBuildId(directory.Name))
				.OrderByDescending(directory=>directory.LastWriteTimeUtc)
				.ToArray();
			foreach(var directory in candidates.Take(keep)) kept++;
			foreach(var directory in candidates.Skip(keep)) {
				if(protectedPath is not null&&String.Equals(directory.FullName,protectedPath,StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
				var size=DirectorySize(directory);
				Console.WriteLine((dryRun?"would remove ":"removing ")+directory.FullName+" ("+(size/(1024*1024)).ToString(CultureInfo.InvariantCulture)+" MB)");
				if(!dryRun) { try { directory.Delete(true); } catch(Exception ex) { Console.Error.WriteLine("could not remove "+directory.FullName+": "+ex.Message); continue; } }
				reclaimed+=size; removed++;
			}
		}
		Console.WriteLine((dryRun?"prune (dry run): ":"prune: ")+removed.ToString(CultureInfo.InvariantCulture)+" removed, "+kept.ToString(CultureInfo.InvariantCulture)+" retained, "+(reclaimed/(1024L*1024L)).ToString(CultureInfo.InvariantCulture)+" MB");
	}

	/// <summary>True when a build id was machine-generated for one run, which is the only kind this tool
	/// will delete. Recognised by a 32-character hex run, matching both <c>gate-3f9c...</c> and the
	/// <c>.gate-3f9c....staging-8a1b...</c> debris an interrupted run leaves behind.</summary>
	internal static bool IsSingleUseBuildId(string name) {
		var run=0;
		foreach(var ch in name) {
			var hex=(ch>='0'&&ch<='9')||(ch>='a'&&ch<='f')||(ch>='A'&&ch<='F');
			if(hex) { if(++run>=32) return true; }
			else run=0;
		}
		return false;
	}

	static long DirectorySize(DirectoryInfo directory) {
		try { return directory.EnumerateFiles("*",SearchOption.AllDirectories).Sum(file=>file.Length); }
		catch(Exception) { return 0; }
	}

	/// <summary>Runs only after a pipeline has published and verified its package. One generated build
	/// remains available for retry/diagnosis; named builds are excluded by <see cref="Prune"/>.</summary>
	internal static void PruneAfterSuccessfulPipeline(string artifacts)=>Prune(new Options(new(StringComparer.OrdinalIgnoreCase) {
		{"artifacts",artifacts},
		{"keep","1"},
	}));

	static void Pipeline(Options options) {
		var (repo,artifacts,buildId)=ResolvePipelineOptions(options);
		Build(new Options(new(StringComparer.OrdinalIgnoreCase){{"repo",repo},{"artifacts",artifacts},{"build-id",buildId}}));
		var host=Path.Combine(artifacts,"host-raw",buildId,"content"); var components=Path.Combine(artifacts,"dgspy-components",buildId,"content");
		var layout=Full(options.Value("layout") ?? Path.Combine(artifacts,"layouts",buildId));
		Compose(new Options(new(StringComparer.OrdinalIgnoreCase){{"host",host},{"components",Path.Combine(components,"extension")},{"cli",Path.Combine(components,"cli")},{"gateway",Path.Combine(components,"gateway")},{"installer",Path.Combine(components,"installer")},{"launcher",Path.Combine(components,"launcher")},{"watcher",Path.Combine(components,"watcher")},{"bootstrap",Path.Combine(components,"payload","HookLab.Bootstrap.dll")},{"native-bootstrap",Path.Combine(components,"payload","HookLab.NativeBootstrap.x64.dll")},{"output",layout}}));
		var package=Full(options.Value("package") ?? Path.Combine(artifacts,"packages","dgspy-win-x64",buildId));
		Package(new Options(new(StringComparer.OrdinalIgnoreCase){{"layout",layout},{"output",package}}));
		PruneAfterSuccessfulPipeline(artifacts);
		Console.WriteLine("pipeline complete: "+package);
	}

	internal static (string Repo,string Artifacts,string BuildId) ResolvePipelineOptions(Options options) {
		var repo=Full(options.Value("repo") ?? Environment.CurrentDirectory);
		var artifacts=Full(options.Value("artifacts") ?? Path.Combine(repo,"artifacts"));
		var buildId=SafeId(options.Value("build-id") ?? "local");
		return (repo,artifacts,buildId);
	}

	static void Build(Options options) {
		var repo=Full(options.Required("repo"));
		var artifacts=Full(options.Required("artifacts"));
		var buildId=SafeId(options.Required("build-id"));
		BuildHost(new Options(new(StringComparer.OrdinalIgnoreCase){{"repo",repo},{"output",Path.Combine(artifacts,"host-raw",buildId)}}));
		BuildComponents(new Options(new(StringComparer.OrdinalIgnoreCase){{"repo",repo},{"host",Path.Combine(artifacts,"host-raw",buildId,"content")},{"output",Path.Combine(artifacts,"dgspy-components",buildId)}}));
		Console.WriteLine("build artifacts complete: "+buildId);
	}

	static void BuildHost(Options options) {
		var repo=Full(options.Required("repo")); var output=Full(options.Required("output"));
		var publish=Path.Combine(repo,"dnSpy","dnSpy","bin","Release","net10.0-windows","win-x64","publish");
		CleanUnsupportedHostBuildOutputs(Path.Combine(repo,"dnSpy"),publish);
		Run(repo,"dotnet","build",Path.Combine(repo,"Build","AppHostPatcher","AppHostPatcher.csproj"),"-c","Release","-f","net48","--nologo","-v:minimal","-clp:ErrorsOnly");
		// Publishing a solution publishes every project independently. With a self-contained RID that
		// copied the complete runtime, Roslyn and every language satellite into dozens of class-library
		// publish directories: 14 GB of bin/obj for a single x64 host. Build the complete solution so all
		// extensions still participate, then publish only the application entry project from those outputs.
		Run(repo,"dotnet","build",Path.Combine(repo,"dnSpy.sln"),"-c","Release","-f","net10.0-windows","--nologo","-v:minimal","-clp:ErrorsOnly");
		Run(repo,"dotnet","publish",Path.Combine(repo,"dnSpy","dnSpy","dnSpy.csproj"),"-c","Release","-f","net10.0-windows","-r","win-x64","--self-contained","true","--no-restore","--nologo","-v:minimal","-clp:ErrorsOnly");
		Run(repo,"dotnet","publish",Path.Combine(repo,"dnSpy","dnSpy.Console","dnSpy.Console.csproj"),"-c","Release","-f","net10.0-windows","-r","win-x64","--self-contained","true","--no-restore","-o",publish,"--nologo","-v:minimal","-clp:ErrorsOnly");
		// These contracts are implemented by optional debugger extensions and intentionally are not
		// references of the GUI entry project. dgSpy compiles against them, so project-only publication
		// must project the complete-solution contract surface explicitly.
		foreach(var contract in new[]{"dnSpy.Contracts.Debugger.DotNet.CorDebug","dnSpy.Contracts.Debugger.DotNet.Mono"})
			foreach(var extension in new[]{"dll","pdb","xml"}) {
				var source=Path.Combine(repo,"dnSpy",contract,"bin","Release","net10.0-windows",contract+"."+extension);
				if(!File.Exists(source)) throw new InvalidOperationException("Built host contract is missing: "+source);
				File.Copy(source,Path.Combine(publish,Path.GetFileName(source)),true);
			}
		if(!File.Exists(Path.Combine(publish,"dnSpy.exe"))) throw new InvalidOperationException("Host publish output is missing: "+publish);
		// The whole publish directory becomes the layout's bin, so a bin inside it becomes bin/bin and
		// ships. dotnet publish never deletes what it no longer produces, so one left by an older layout
		// scheme survives every rebuild: 866 files, some dated 2017, including a second copy of every host
		// contract and extension assembly.
		//
		// That was invisible for as long as the duplicates stayed interface-compatible. When
		// IDgSpyOwnedBreakpointService gained a method, the stale extension beside the fresh one still
		// implemented the old interface, the export lost its match, and the owned-breakpoint service
		// vanished from composition - taking everything that imported it, silently, in the way this
		// repository documents and the composition test exists to catch.
		if(Directory.Exists(Path.Combine(publish,"bin")))
			throw new InvalidOperationException("The host publish output contains a stale nested bin directory: "+Path.Combine(publish,"bin")+
				". dotnet publish does not remove it, and composing it would ship a second copy of every host assembly as bin/bin. Delete that directory and build again.");
		PublishDirectory(output,staging=>{
			var content=Path.Combine(staging,"content"); var bin=Path.Combine(content,"bin"); CopyDirectory(publish,bin);
			foreach(var exe in new[]{"dnSpy.exe","dnSpy.Console.exe"}) { var from=Path.Combine(bin,exe); var to=Path.Combine(content,exe); File.Move(from,to); Run(repo,Path.Combine(repo,"Build","AppHostPatcher","bin","Release","net48","AppHostPatcher.exe"),to,"-d","bin"); }
			var ownership=Directory.EnumerateFiles(content,"*",SearchOption.AllDirectories).ToDictionary(path=>Relative(staging,path),_=>"host",StringComparer.OrdinalIgnoreCase);
			WriteManifest(staging,ownership); VerifyInventoryOnly(staging); Require(content,"dnSpy.exe"); Require(content,"bin/dnSpy.dll");
		},"build-host --repo "+Quote(repo)+" --output "+Quote(output));
	}

	/// <summary>Removes output shapes created by the old solution-wide publish. A win-x86 runtime asset
	/// inside an x64 application (runtimes/win-x86) is data, not an x86 build tree, and is preserved.</summary>
	internal static void CleanUnsupportedHostBuildOutputs(string dnSpyRoot,string canonicalPublish) {
		if(!Directory.Exists(dnSpyRoot)) return;
		var canonical=Full(canonicalPublish);
		foreach(var directory in Directory.EnumerateDirectories(dnSpyRoot,"publish",SearchOption.AllDirectories).Select(Full).Where(path=>!String.Equals(path,canonical,StringComparison.OrdinalIgnoreCase)).ToArray())
			Directory.Delete(directory,true);
		foreach(var directory in Directory.EnumerateDirectories(dnSpyRoot,"win-x86",SearchOption.AllDirectories).Select(Full).Where(path=>!new DirectoryInfo(path).Parent!.Name.Equals("runtimes",StringComparison.OrdinalIgnoreCase)&&IsUnderBuildOutput(path,dnSpyRoot)).ToArray())
			Directory.Delete(directory,true);
	}

	static bool IsUnderBuildOutput(string path,string root) {
		for(var parent=new DirectoryInfo(path).Parent;parent is not null&&!String.Equals(parent.FullName,root,StringComparison.OrdinalIgnoreCase);parent=parent.Parent)
			if(parent.Name.Equals("bin",StringComparison.OrdinalIgnoreCase)||parent.Name.Equals("obj",StringComparison.OrdinalIgnoreCase)) return true;
		return false;
	}

	static void BuildComponents(Options options) {
		var repo=Full(options.Required("repo")); var host=Full(options.Required("host")); var output=Full(options.Required("output"));
		Require(host,"bin/dnSpy.Contracts.DnSpy.dll");
		Run(repo,"dotnet","msbuild",Path.Combine(repo,"Build","DgSpy.Components.proj"),"/restore","/t:Build","/p:Configuration=Release","/p:DgSpyHostContractsRoot="+Path.Combine(host,"bin"),"/m","/nologo","/v:minimal","/clp:ErrorsOnly");
		BuildNative(repo);
		PublishDirectory(output,staging=>{
			var content=Path.Combine(staging,"content"); var extension=Path.Combine(content,"extension"); var payload=Path.Combine(content,"payload"); var launcher=Path.Combine(content,"launcher");
			RunMany(new[]{
				(repo,"dotnet",new[]{"publish",Path.Combine(repo,"dgSpy.Cli","dgSpy.Cli.csproj"),"-c","Release","-r","win-x64","--self-contained","true","--no-restore","-o",Path.Combine(content,"cli"),"--nologo","-v:minimal","-clp:ErrorsOnly"}),
				(repo,"dotnet",new[]{"publish",Path.Combine(repo,"dgSpy.Gateway","dgSpy.Gateway.csproj"),"-c","Release","-r","win-x64","--self-contained","true","--no-restore","-o",Path.Combine(content,"gateway"),"--nologo","-v:minimal","-clp:ErrorsOnly"}),
				(repo,"dotnet",new[]{"publish",Path.Combine(repo,"Build","DgSpyTool","DgSpyTool.csproj"),"-c","Release","-r","win-x64","--self-contained","true","--no-restore","-p:PublishSingleFile=true","-p:IncludeNativeLibrariesForSelfExtract=true","-o",Path.Combine(content,"installer"),"--nologo","-v:minimal","-clp:ErrorsOnly"})});
			Run(repo,"dotnet","publish",Path.Combine(repo,"tools","HookLab.Watcher","HookLab.Watcher.csproj"),"-c","Release","-r","win-x64","--self-contained","true","--no-restore","-o",Path.Combine(content,"watcher"),"--nologo","-v:minimal","-clp:ErrorsOnly");
			Run(repo,"dotnet","publish",Path.Combine(repo,"tools","HookLab.Watcher.Companion","HookLab.Watcher.Companion.csproj"),"-c","Release","-r","win-x64","--self-contained","true","--no-restore","-o",Path.Combine(content,"watcher"),"--nologo","-v:minimal","-clp:ErrorsOnly");
			CopyDirectory(Path.Combine(repo,"tools","HookLab.Watcher","deployments"),Path.Combine(content,"watcher","deployments"));
			Directory.CreateDirectory(Path.Combine(content,"watcher","payload"));
			File.Copy(Path.Combine(repo,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll"),Path.Combine(content,"watcher","payload","HookLab.Bootstrap.dll"));
			File.Copy(Path.Combine(repo,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll"),Path.Combine(content,"watcher","payload","HookLab.NativeBootstrap.x64.dll"));
			Directory.CreateDirectory(extension); Directory.CreateDirectory(payload); Directory.CreateDirectory(launcher);
			var extensionOutput=Path.Combine(repo,"Extensions","dgSpy.Extension","bin","Release","net10.0-windows");
			foreach(var file in ExtensionFiles) File.Copy(Path.Combine(extensionOutput,file),Path.Combine(extension,file));
			File.Copy(Path.Combine(repo,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll"),Path.Combine(payload,"HookLab.Bootstrap.dll"));
			File.Copy(Path.Combine(repo,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll"),Path.Combine(payload,"HookLab.NativeBootstrap.x64.dll"));
			foreach(var file in new[]{"Start-dgSpyRemoteHost.ps1","Start-dgSpyRemoteHost.cmd"}) File.Copy(Path.Combine(repo,"packaging","remote-host",file),Path.Combine(launcher,file));
			var ownership=Directory.EnumerateFiles(content,"*",SearchOption.AllDirectories).ToDictionary(path=>Relative(staging,path),_=>"component",StringComparer.OrdinalIgnoreCase);
			WriteManifest(staging,ownership); VerifyInventoryOnly(staging);
		},"build-components --repo "+Quote(repo)+" --host "+Quote(host)+" --output "+Quote(output));
	}

	static void BuildNative(string repo) {
		var candidates=new[]{@"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",@"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",@"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"};
		var msbuild=FindMSBuildWithVcx64() ?? candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("MSBuild with the Visual C++ x64 toolchain was not found.");
		Run(repo,msbuild,Path.Combine(repo,"HookLab","HookLab.NativeBootstrap","HookLab.NativeBootstrap.vcxproj"),"/nologo","/m:1","/p:Configuration=Release","/p:Platform=x64","/v:minimal","/clp:ErrorsOnly");
	}

	static string? FindMSBuildWithVcx64() {
		// Editions and versions move around (CI runners ship Enterprise, VS 2026 lives under "18"),
		// so ask vswhere for any install that actually has the VC++ x64 toolchain.
		var vswhere=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft Visual Studio","Installer","vswhere.exe");
		if(!File.Exists(vswhere)) return null;
		var info=new ProcessStartInfo(vswhere){RedirectStandardOutput=true,UseShellExecute=false};
		foreach(var argument in new[]{"-latest","-products","*","-requires","Microsoft.VisualStudio.Component.VC.Tools.x86.x64","-find",@"MSBuild\**\Bin\amd64\MSBuild.exe"}) info.ArgumentList.Add(argument);
		using var process=Process.Start(info);
		if(process is null) return null;
		var first=process.StandardOutput.ReadLine();
		process.WaitForExit();
		return process.ExitCode==0 && File.Exists(first) ? first : null;
	}

	static void Run(string workingDirectory,string executable,params string[] arguments) {
		var watch=Stopwatch.StartNew(); Console.WriteLine("start "+Path.GetFileName(executable)+" "+String.Join(' ',arguments.Take(2)));
		var shell=Path.GetExtension(executable) is ".cmd" or ".bat";
		var info=new ProcessStartInfo(executable){WorkingDirectory=workingDirectory,UseShellExecute=shell};
		foreach(var argument in arguments) info.ArgumentList.Add(argument);
		if(!shell) {
			info.Environment["MSBUILDDISABLENODEREUSE"]="1";
			// VC's environment import is case-insensitive and rejects duplicate Path/PATH entries.
			var path=Environment.GetEnvironmentVariable("Path"); info.Environment.Remove("PATH"); if(path is not null) info.Environment["Path"]=path;
		}
		using var process=Process.Start(info) ?? throw new InvalidOperationException("Could not start: "+executable); process.WaitForExit();
		if(process.ExitCode!=0) throw new InvalidOperationException(Path.GetFileName(executable)+" failed with exit code "+process.ExitCode+".");
		Console.WriteLine("done  "+Path.GetFileName(executable)+" "+watch.Elapsed.TotalSeconds.ToString("0.0")+"s");
	}
	static void RunMany(IEnumerable<(string WorkingDirectory,string Executable,string[] Arguments)> commands) {
		Task.WaitAll(commands.Select(command=>Task.Run(()=>Run(command.WorkingDirectory,command.Executable,command.Arguments))).ToArray());
	}
	static string SafeId(string value)=>value.Length>0&&value.All(ch=>Char.IsLetterOrDigit(ch)||ch is '.' or '_' or '-')?value:throw new ArgumentException("--build-id may contain only letters, digits, dot, underscore, and dash.");

	static void Compose(Options options) {
		var output=Full(options.Required("output"));
		var parent=Directory.GetParent(output)?.FullName ?? throw new InvalidOperationException("Output must have a parent directory.");
		Directory.CreateDirectory(parent);
		var staging=Path.Combine(parent,"."+Path.GetFileName(output)+".staging-"+Guid.NewGuid().ToString("N"));
		var previous=output+".previous-"+Guid.NewGuid().ToString("N");
		try {
			Directory.CreateDirectory(staging);
			var ownership=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
			CopyTree(options.Required("host"),staging,"host",ownership);
			var bin=Path.Combine(staging,"bin");
			MergeTree(options.Required("cli"),bin,"cli",ownership);
			MergeTree(options.Required("gateway"),bin,"gateway",ownership);
			var installer=options.Value("installer"); if(!string.IsNullOrWhiteSpace(installer)) MergeTree(installer!,bin,"installer",ownership);
			MergeTree(options.Required("launcher"),Path.Combine(staging,"launcher"),"launcher",ownership);
			MergeTree(options.Required("watcher"),Path.Combine(staging,"hooklab-watcher"),"hooklab-watcher",ownership);
			ProtectPrivilegedTree(Path.Combine(staging,"hooklab-watcher"));
			var extension=Path.Combine(bin,"Extensions","dgSpy"); Directory.CreateDirectory(extension);
			foreach(var name in ExtensionFiles) CopyOwned(Path.Combine(options.Required("components"),name),Path.Combine(extension,name),"extension",staging,ownership,false);
			var hooklab=Path.Combine(staging,"hooklab"); Directory.CreateDirectory(hooklab);
			var payload=Path.Combine(hooklab,PayloadFileName);
			CopyOwned(options.Required("bootstrap"),payload,"hooklab",staging,ownership,false);
			CopyOwned(options.Required("native-bootstrap"),Path.Combine(hooklab,"HookLab.NativeBootstrap.x64.dll"),"hooklab",staging,ownership,false);
			var payloadInfo=new { format_version=1,payloads=new[]{new { id="hooklab_bootstrap",file=Path.GetFileName(payload),target_framework="net48",architecture="x64",size=new FileInfo(payload).Length,sha256=Hash(payload) }} };
			File.WriteAllText(Path.Combine(hooklab,"hooklab-payload-manifest.json"),JsonSerializer.Serialize(payloadInfo,JsonOptions)+Environment.NewLine);
			ownership[Relative(staging,Path.Combine(hooklab,"hooklab-payload-manifest.json"))]="hooklab";
			// The delivery manifest above describes the one file the host opens. This describes what is
			// inside it: every resident payload, which runtime family it is for, where it came from, and
			// what identity it carries. Written from the payload's own embedded matrix after that matrix has
			// been proved against the payload's bytes, so it is a projection of evidence, not a restatement.
			File.WriteAllText(Path.Combine(hooklab,PayloadMatrixFileName),JsonSerializer.Serialize(PayloadMatrixVerification.Publishable(PayloadMatrixVerification.VerifyPayloadFile(payload)),JsonOptions)+Environment.NewLine);
			ownership[Relative(staging,Path.Combine(hooklab,PayloadMatrixFileName))]="hooklab";
			WriteManifest(staging,ownership);
			Verify(staging);
			if(Directory.Exists(output)) Directory.Move(output,previous);
			try { Directory.Move(staging,output); }
			catch { if(!Directory.Exists(output)&&Directory.Exists(previous)) Directory.Move(previous,output); throw; }
			if(Directory.Exists(previous)) Directory.Delete(previous,true);
			Console.WriteLine(output);
		}
		catch { Console.Error.WriteLine("Retry unchanged after correcting the input: dotnet run --project Build/DgSpyTool -- "+String.Join(' ',Environment.GetCommandLineArgs().Skip(1))); throw; }
		finally { if(Directory.Exists(staging)) Directory.Delete(staging,true); }
	}
	static void Snapshot(Options options) {
		var source=Full(options.Required("source")); var output=Full(options.Required("output"));
		PublishDirectory(output,staging=>{
			var content=Path.Combine(staging,"content"); CopyDirectory(source,content);
			var ownership=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
			foreach(var file in Directory.EnumerateFiles(content,"*",SearchOption.AllDirectories)) ownership[Relative(staging,file)]="input";
			WriteManifest(staging,ownership); VerifyInventoryOnly(staging);
		},"snapshot --source "+Quote(source)+" --output "+Quote(output));
	}

	static void Verify(string layoutPath) {
		var root=Full(layoutPath); var manifestPath=Path.Combine(root,ManifestName);
		var manifest=VerifyInventoryOnly(root);
		Require(root,"dnSpy.exe"); Require(root,"bin/dnSpy.dll"); Require(root,"bin/dgspy.exe"); Require(root,"bin/dgSpy.Gateway.exe"); Require(root,"bin/Extensions/dgSpy/dgSpy.Extension.x.dll"); Require(root,"hooklab/HookLab.NativeBootstrap.x64.dll"); Require(root,"hooklab-watcher/HookLab.Watcher.exe"); Require(root,"hooklab-watcher/HookLab.Watcher.Companion.exe"); Require(root,"hooklab-watcher/payload/HookLab.Bootstrap.dll"); Require(root,"hooklab-watcher/payload/HookLab.NativeBootstrap.x64.dll"); Require(root,"hooklab-watcher/deployments/vmconnect-fullscreen/vmconnect-fullscreen.json"); Require(root,"launcher/Start-dgSpyRemoteHost.ps1"); Require(root,"launcher/Start-dgSpyRemoteHost.cmd"); Require(root,"hooklab/"+PayloadFileName); Require(root,"hooklab/hooklab-payload-manifest.json"); Require(root,"hooklab/"+PayloadMatrixFileName);
		// The other end of the check in BuildHost. That one refuses the cause at the moment it is created;
		// this refuses the result however it arrived, including from a host artifact composed before the
		// cause was understood. A second copy of every host assembly under bin/bin removes MEF parts
		// without logging anything.
		if(Directory.Exists(Path.Combine(root,"bin","bin")))
			throw new InvalidOperationException("The layout contains a duplicate host assembly tree at bin/bin: "+Path.Combine(root,"bin","bin")+
				". It shadows the real one during MEF composition and silently removes parts. Rebuild the host artifact from a publish directory with no nested bin.");
		var rootProtocol=Hash(Path.Combine(root,"bin","dgSpy.Protocol.dll")); var extensionProtocol=Hash(Path.Combine(root,"bin","Extensions","dgSpy","dgSpy.Protocol.dll"));
		if(rootProtocol!=extensionProtocol) throw new InvalidOperationException("App-base and extension protocol assemblies differ.");
		VerifyPayloadMatrix(root);
		Console.WriteLine($"verified {manifest.Files.Length} files: {root}");
	}
	/// <summary>Re-derives the resident payload matrix from the packaged payload's own bytes and requires
	/// the published projection to agree with it exactly. Running this in Verify rather than only in
	/// Compose is the point: package verification, installation checks and CI all reach it, so a layout
	/// whose payload was replaced after composition cannot pass by carrying an agreeable JSON file.</summary>
	static void VerifyPayloadMatrix(string root) {
		var matrix=PayloadMatrixVerification.VerifyPayloadFile(Path.Combine(root,"hooklab",PayloadFileName));
		var published=Path.Combine(root,"hooklab",PayloadMatrixFileName);
		var expected=JsonSerializer.Serialize(PayloadMatrixVerification.Publishable(matrix),JsonOptions)+Environment.NewLine;
		if(File.ReadAllText(published)!=expected) throw new InvalidOperationException("The published payload matrix differs from the matrix inside the HookLab payload: "+published);
	}
	static LayoutManifest VerifyInventoryOnly(string root) {
		var manifestPath=Path.Combine(root,ManifestName); if(!File.Exists(manifestPath)) throw new InvalidOperationException("Artifact manifest is missing: "+manifestPath);
		var manifest=ReadLayout(root); if(manifest.FormatVersion!=1) throw new InvalidOperationException("Unsupported artifact manifest version.");
		var actual=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Select(path=>Relative(root,path)).Where(path=>!path.Equals(ManifestName,StringComparison.OrdinalIgnoreCase)).OrderBy(path=>path,StringComparer.Ordinal).ToArray();
		var expected=manifest.Files.Select(file=>file.Path).OrderBy(path=>path,StringComparer.Ordinal).ToArray(); if(!actual.SequenceEqual(expected,StringComparer.Ordinal)) throw new InvalidOperationException("Artifact file inventory differs from its manifest.");
		Parallel.ForEach(manifest.Files,file=>{ var path=Path.Combine(root,file.Path); var info=new FileInfo(path); if(info.Length!=file.Size||Hash(path)!=file.Sha256) throw new InvalidOperationException("Artifact file differs from its manifest: "+file.Path); });
		return manifest;
	}

	static void Package(Options options) {
		var layout=Full(options.Required("layout")); Verify(layout);
		var output=Full(options.Required("output")); PublishDirectory(output,staging=>{
			CopyDirectory(layout,Path.Combine(staging,"cli"));
			ProtectPrivilegedTree(Path.Combine(staging,"cli","hooklab-watcher"));
			File.Copy(Path.Combine(layout,"bin","DgSpyTool.exe"),Path.Combine(staging,"install-dgspy.exe"));
			var layoutManifest=Path.Combine(staging,"cli",ManifestName);
			var files=Directory.EnumerateFiles(Path.Combine(staging,"cli"),"*",SearchOption.AllDirectories).ToArray();
			var manifest=new PackageManifest(1,"win-x64","cli/dnSpy.exe","cli/bin/dgspy.exe","cli/bin/dgSpy.Gateway.exe","install-dgspy.exe",Hash(Path.Combine(staging,"install-dgspy.exe")),Hash(layoutManifest),files.Length,files.Sum(file=>new FileInfo(file).Length));
			File.WriteAllText(Path.Combine(staging,"manifest.json"),JsonSerializer.Serialize(manifest,JsonOptions)+Environment.NewLine);
			VerifyPackage(staging);
		},"package --layout "+Quote(layout)+" --output "+Quote(output));
	}

	static void VerifyPackage(string packagePath) {
		var root=Full(packagePath); var manifestPath=Path.Combine(root,"manifest.json");
		var manifest=JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath),JsonOptions) ?? throw new InvalidOperationException("Package manifest is invalid.");
		if(manifest.FormatVersion!=1) throw new InvalidOperationException("Unsupported package manifest version.");
		Require(root,manifest.Installer); if(Hash(Path.Combine(root,manifest.Installer))!=manifest.InstallerSha256) throw new InvalidOperationException("Package installer digest differs.");
		var layout=Path.Combine(root,"cli"); Verify(layout);
		if(Hash(Path.Combine(layout,ManifestName))!=manifest.LayoutManifestSha256) throw new InvalidOperationException("Package layout manifest digest differs.");
		var files=Directory.EnumerateFiles(layout,"*",SearchOption.AllDirectories).ToArray();
		if(files.Length!=manifest.FileCount||files.Sum(file=>new FileInfo(file).Length)!=manifest.PayloadBytes) throw new InvalidOperationException("Package tree shape differs from its manifest.");
		Console.WriteLine("verified package: "+root);
	}

	static void Install(Options options) {
		var package=Full(options.Required("package")); VerifyPackage(package);
		var install=Full(options.Required("install")); var source=Path.Combine(package,"cli");
		if(options.Flag("host-only")) { InstallHostOnly(source,install); return; }
		var agent=options.Value("agent");
		if(agent is not null && agent is not ("codex" or "claude")) throw new ArgumentException("--agent must be codex or claude.");
		StopInstallProcesses(install,options.Flag("force"),agent);
		var parent=Directory.GetParent(install)?.FullName ?? throw new InvalidOperationException("Install path must have a parent directory.");
		Directory.CreateDirectory(parent);
		var staging=install+".staging-"+Guid.NewGuid().ToString("N"); var backup=install+".previous-"+Guid.NewGuid().ToString("N");
		try {
			Directory.CreateDirectory(staging); CopyDirectory(source,Path.Combine(staging,"cli")); File.Copy(Path.Combine(package,"manifest.json"),Path.Combine(staging,"manifest.json")); Verify(Path.Combine(staging,"cli"));
			if(Directory.Exists(install)) Directory.Move(install,backup);
			try {
				Directory.Move(staging,install);
				if(agent is not null) RegisterAgent(agent,install);
			}
			catch { TryDeleteDirectory(install); if(Directory.Exists(backup)) Directory.Move(backup,install); throw; }
			TryDeleteDirectory(backup);
		}
		catch { Console.Error.WriteLine("Retry unchanged after closing processes: dotnet run --project Build/DgSpyTool -- install --package "+Quote(package)+" --install "+Quote(install)+(agent is null?"":" --agent "+agent)); throw; }
		finally { TryDeleteDirectory(staging); }
	}

	static void StopInstallProcesses(string install,bool force,string? agent) {
		if(!Directory.Exists(install)) return;
		var prefix=install.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
		var holders=Process.GetProcesses().Select(process=>{ try { return (Process:process,Path:process.MainModule?.FileName); } catch { process.Dispose(); return (Process:process,Path:(string?)null); } }).Where(item=>item.Path?.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)==true).ToArray();
		if(holders.Length==0) return;
		var detail=String.Join(", ",holders.Select(item=>$"{item.Process.ProcessName} (PID {item.Process.Id})"));
		if(!force) {
			var forceCommand=agent is null?"rerun with --force true":$"run .\\install-dgspy.exe {agent} --force";
			throw new InvalidOperationException($"Cannot replace {install} while these processes run from it: {detail}. For a compatible host-only update without restarting the agent, run .\\install-dgspy.exe host-only. For a full replacement, close the agent and dnSpy, or {forceCommand} and restart the agent afterward.");
		}
		foreach(var holder in holders) { try { holder.Process.Kill(true); holder.Process.WaitForExit(15000); } finally { holder.Process.Dispose(); } }
	}

	static void RegisterAgent(string agent,string install) {
		var cli=Path.Combine(install,"cli","bin","dgspy.exe");
		if(agent=="codex") { Run(install,cli,"configure","codex","--apply"); return; }
		var claude=ResolveCommand("claude") ?? throw new InvalidOperationException("Claude Code CLI was not found on PATH.");
		RunAllowFailure(install,claude,"mcp","remove","dgspy","--scope","user");
		Run(install,claude,"mcp","add","dgspy","--scope","user","--",cli,"mcp");
	}

	static string? ResolveCommand(string name) {
		foreach(var directory in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))
			foreach(var extension in new[]{".exe",".cmd",".bat",""}) { var candidate=Path.Combine(directory.Trim(),name+extension); if(File.Exists(candidate)) return candidate; }
		return null;
	}

	static void RunAllowFailure(string workingDirectory,string executable,params string[] arguments) { try { Run(workingDirectory,executable,arguments); } catch { } }

	static void InstallHostOnly(string sourceLayout,string installRoot) {
		var installedLayout=Path.Combine(installRoot,"cli");
		if(!File.Exists(Path.Combine(installedLayout,ManifestName))) throw new InvalidOperationException("Host-only install requires a new-format DgSpyTool installation. Run one full install first; legacy trees are replaced, not migrated.");
		VerifyInventoryOnly(installedLayout);
		Require(installedLayout,"bin/dgspy.exe"); Require(installedLayout,"bin/dgSpy.Gateway.exe"); Require(installedLayout,"bin/dgSpy.Protocol.dll");
		var sourceManifest=ReadLayout(sourceLayout); var installedManifest=ReadLayout(installedLayout);
		var sourceProtocol=sourceManifest.Files.Single(file=>file.Path.Equals("bin/dgSpy.Protocol.dll",StringComparison.OrdinalIgnoreCase)).Sha256;
		var installedProtocol=installedManifest.Files.Single(file=>file.Path.Equals("bin/dgSpy.Protocol.dll",StringComparison.OrdinalIgnoreCase)).Sha256;
		if(sourceProtocol!=installedProtocol) throw new InvalidOperationException("Host-only install refused: the package changes the protocol contract. No installed files were changed.");
		var owned=sourceManifest.Files.Where(file=>IsHostOwned(file.Owner)).ToArray();
		var oldOwned=installedManifest.Files.Where(file=>IsHostOwned(file.Owner)).Select(file=>file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var sourcePaths=sourceManifest.Files.Select(file=>file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var obsolete=oldOwned.Where(path=>!sourcePaths.Contains(path)).ToArray();
		var affected=obsolete.Concat(owned.Select(file=>file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var transaction=Path.Combine(installRoot,".host-update-"+Guid.NewGuid().ToString("N")); var backup=Path.Combine(transaction,"backup"); var incoming=Path.Combine(transaction,"incoming");
		try {
			Directory.CreateDirectory(backup); Directory.CreateDirectory(incoming);
			File.Copy(Path.Combine(installedLayout,ManifestName),Path.Combine(backup,ManifestName));
			foreach(var relative in affected) {
				var current=Path.Combine(installedLayout,relative); if(File.Exists(current)) { var saved=Path.Combine(backup,relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(current,saved); }
			}
			foreach(var file in owned) { var staged=Path.Combine(incoming,file.Path); Directory.CreateDirectory(Path.GetDirectoryName(staged)!); File.Copy(Path.Combine(sourceLayout,file.Path),staged); }
			foreach(var relative in obsolete) File.Delete(Path.Combine(installedLayout,relative));
			foreach(var file in owned) { var destination=Path.Combine(installedLayout,file.Path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(Path.Combine(incoming,file.Path),destination,true); }
			var ownership=installedManifest.Files.Where(file=>!obsolete.Contains(file.Path,StringComparer.OrdinalIgnoreCase)).ToDictionary(file=>file.Path,file=>file.Owner,StringComparer.OrdinalIgnoreCase);
			foreach(var file in sourceManifest.Files.Where(file=>ownership.ContainsKey(file.Path))) ownership[file.Path]=file.Owner;
			foreach(var file in owned) ownership[file.Path]=file.Owner;
			WriteManifest(installedLayout,ownership);
			Verify(installedLayout);
		}
		catch {
			foreach(var relative in affected) { var saved=Path.Combine(backup,relative); var destination=Path.Combine(installedLayout,relative); if(File.Exists(saved)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(saved,destination,true); } else File.Delete(destination); }
			var savedManifest=Path.Combine(backup,ManifestName); if(File.Exists(savedManifest)) File.Copy(savedManifest,Path.Combine(installedLayout,ManifestName),true);
			throw;
		}
		finally { TryDeleteDirectory(transaction); }
	}
	static bool IsHostOwned(string owner)=>owner is "host" or "extension" or "hooklab" or "launcher";

	static void PublishDirectory(string output,Action<string> build,string retry) {
		var parent=Directory.GetParent(output)?.FullName ?? throw new InvalidOperationException("Output must have a parent directory."); Directory.CreateDirectory(parent);
		var staging=Path.Combine(parent,"."+Path.GetFileName(output)+".staging-"+Guid.NewGuid().ToString("N")); var previous=output+".previous-"+Guid.NewGuid().ToString("N");
		try { Directory.CreateDirectory(staging); build(staging); if(Directory.Exists(output)) Directory.Move(output,previous); try { Directory.Move(staging,output); } catch { if(!Directory.Exists(output)&&Directory.Exists(previous)) Directory.Move(previous,output); throw; } TryDeleteDirectory(previous); }
		catch { Console.Error.WriteLine("Retry unchanged: dotnet run --project Build/DgSpyTool -- "+retry); throw; }
		finally { TryDeleteDirectory(staging); }
	}
	static LayoutManifest ReadLayout(string root)=>JsonSerializer.Deserialize<LayoutManifest>(File.ReadAllText(Path.Combine(root,ManifestName)),JsonOptions) ?? throw new InvalidOperationException("Layout manifest is invalid.");
	static void CopyDirectory(string source,string destination) { Directory.CreateDirectory(destination); foreach(var directory in Directory.EnumerateDirectories(source,"*",SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination,Path.GetRelativePath(source,directory))); Parallel.ForEach(Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories),file=>{ var target=Path.Combine(destination,Path.GetRelativePath(source,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target); }); }
	static void ProtectPrivilegedTree(string root) {
		if(!OperatingSystem.IsWindows()) return;
		var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit; var propagation=PropagationFlags.None; var full=FileSystemRights.FullControl;
		var identities=new IdentityReference[]{WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException("Current Windows identity has no SID."),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)};
		var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false); foreach(var identity in identities) security.AddAccessRule(new FileSystemAccessRule(identity,full,inheritance,propagation,AccessControlType.Allow)); FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root),security);
		foreach(var directory in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories)) { var value=new DirectorySecurity(); value.SetAccessRuleProtection(false,false); FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory),value); }
		foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) { var value=new FileSecurity(); value.SetAccessRuleProtection(false,false); FileSystemAclExtensions.SetAccessControl(new FileInfo(file),value); }
	}
	static void TryDeleteDirectory(string path) { if(!Directory.Exists(path)) return; try { Directory.Delete(path,true); } catch(Exception ex) { Console.Error.WriteLine("warning: completed artifact retained, but cleanup failed for "+path+": "+ex.Message); } }
	static string Quote(string value)=>'"'+value.Replace("\"","\\\"")+'"';

	static void CopyTree(string source,string destination,string owner,Dictionary<string,string> ownership) { foreach(var file in Directory.EnumerateFiles(Full(source),"*",SearchOption.AllDirectories)) CopyOwned(file,Path.Combine(destination,Path.GetRelativePath(Full(source),file)),owner,destination,ownership,false); }
	static void MergeTree(string source,string destination,string owner,Dictionary<string,string> ownership) { foreach(var file in Directory.EnumerateFiles(Full(source),"*",SearchOption.AllDirectories)) CopyOwned(file,Path.Combine(destination,Path.GetRelativePath(Full(source),file)),owner,Directory.GetParent(destination)!.FullName,ownership,true); }
	static void CopyOwned(string source,string destination,string owner,string root,Dictionary<string,string> ownership,bool merge) {
		if(!File.Exists(source)) throw new FileNotFoundException("Required input is missing.",source); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		if(File.Exists(destination)) { if(Hash(source)==Hash(destination)) { if(merge) ownership[Relative(root,destination)]=owner; return; } if(!merge||!FrameworkOverrides.Contains(Path.GetFileName(destination))||FileVersionInfo.GetVersionInfo(source).FileVersion!=FileVersionInfo.GetVersionInfo(destination).FileVersion) throw new InvalidOperationException("Incompatible layout collision: "+Relative(root,destination)); ownership[Relative(root,destination)]=owner; return; }
		File.Copy(source,destination); ownership[Relative(root,destination)]=owner;
	}
	static void WriteManifest(string root,Dictionary<string,string> ownership) { var files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(path=>!Path.GetFileName(path).Equals(ManifestName,StringComparison.OrdinalIgnoreCase)).AsParallel().Select(path=>new LayoutFile(Relative(root,path),new FileInfo(path).Length,Hash(path),ownership.GetValueOrDefault(Relative(root,path),"generated"))).OrderBy(file=>file.Path,StringComparer.Ordinal).ToArray(); var destination=Path.Combine(root,ManifestName); var temporary=destination+".tmp-"+Guid.NewGuid().ToString("N"); try { File.WriteAllText(temporary,JsonSerializer.Serialize(new LayoutManifest(1,files),JsonOptions)+Environment.NewLine); File.Move(temporary,destination,true); } finally { if(File.Exists(temporary)) File.Delete(temporary); } }
	static void Require(string root,string relative) { if(!File.Exists(Path.Combine(root,relative))) throw new InvalidOperationException("Required layout file is missing: "+relative); }
	static string Hash(string path) { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
	static string Full(string path)=>Path.GetFullPath(path);
	static string Relative(string root,string path)=>Path.GetRelativePath(root,path).Replace('\\','/');
	static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=true};
	sealed record LayoutManifest(int FormatVersion,LayoutFile[] Files);
	sealed record LayoutFile(string Path,long Size,string Sha256,string Owner);
	sealed record PackageManifest(int FormatVersion,string Runtime,string Host,string Entrypoint,string Gateway,string Installer,string InstallerSha256,string LayoutManifestSha256,int FileCount,long PayloadBytes);
	internal sealed class Options(Dictionary<string,string> values) { public string Required(string name)=>values.TryGetValue(name,out var value)?value:throw new ArgumentException("--"+name+" is required."); public string? Value(string name)=>values.GetValueOrDefault(name); public bool Flag(string name)=>values.TryGetValue(name,out var value)&&Boolean.TryParse(value,out var parsed)&&parsed; public static Options Parse(string[] args) { var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); for(var i=0;i<args.Length;i+=2) { if(i+1>=args.Length||!args[i].StartsWith("--")) throw new ArgumentException("Options use --name value pairs."); values[args[i][2..]]=args[i+1]; } return new(values); } }
}
