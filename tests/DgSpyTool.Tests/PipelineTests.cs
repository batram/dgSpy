using Xunit;
using System.Text.Json.Nodes;

public sealed class PipelineTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-tool-tests-"+Guid.NewGuid().ToString("N"));
	public PipelineTests()=>Directory.CreateDirectory(root);
	public void Dispose()=>Directory.Delete(root,true);
	[Fact]
	public void Publish_projects_declare_the_runtime_restored_by_the_component_graph() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		foreach(var project in new[]{"dgSpy.Cli/dgSpy.Cli.csproj","dgSpy.Gateway/dgSpy.Gateway.csproj","Build/DgSpyTool/DgSpyTool.csproj"})
			Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>",File.ReadAllText(Path.Combine(repo,project.Replace('/',Path.DirectorySeparatorChar))));
	}
	[Fact]
	public void Packaged_extension_uses_only_file_references_for_upstream_contracts() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var graph=File.ReadAllText(Path.Combine(repo,"Build","DgSpy.Components.proj"));
		var extension=File.ReadAllText(Path.Combine(repo,"Extensions","dgSpy.Extension","dgSpy.Extension.csproj"));
		var tool=File.ReadAllText(Path.Combine(repo,"Build","DgSpyTool","Program.cs"));
		Assert.DoesNotContain("BuildProjectReferences=false",graph,StringComparison.Ordinal);
		Assert.Contains("DgSpyHostContractsRoot=$(DgSpyHostContractsRoot)",graph,StringComparison.Ordinal);
		Assert.Contains("/p:DgSpyHostContractsRoot=",tool,StringComparison.Ordinal);
		Assert.Contains("<ItemGroup Condition=\"'$(DgSpyHostContractsRoot)' != ''\">",extension,StringComparison.Ordinal);
		foreach(var contract in new[]{"DnSpy","Debugger","Debugger.DotNet","Debugger.DotNet.CorDebug","Debugger.DotNet.Mono","Logic"})
			Assert.Contains("<Reference Include=\"dnSpy.Contracts."+contract+"\">",extension,StringComparison.Ordinal);
		foreach(var dependency in new[]{"dnlib","System.ComponentModel.Composition","Microsoft.VisualStudio.CoreUtility","Microsoft.VisualStudio.Text.Data","Microsoft.VisualStudio.Text.Logic","Microsoft.VisualStudio.Text.UI","Microsoft.VisualStudio.Text.UI.Wpf","dnSpy.Debugger.DotNet.Metadata"})
			Assert.Contains("<Reference Include=\""+dependency+"\">",extension,StringComparison.Ordinal);
		Assert.Contains("<ProjectReference Include=\"..\\..\\dgSpy.Protocol\\dgSpy.Protocol.csproj\" />",extension,StringComparison.Ordinal);
		Assert.Contains("<ProjectReference Include=\"..\\..\\HookLab\\HookLab.Contracts\\HookLab.Contracts.csproj\" />",extension,StringComparison.Ordinal);
		Assert.Contains("<ProjectReference Include=\"..\\..\\HookLab\\HookLab.Packaging\\HookLab.Packaging.csproj\" />",extension,StringComparison.Ordinal);
	}
	[Fact]
	public void Ci_builds_the_net10_package_once_and_reuses_it() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var workflow=File.ReadAllText(Path.Combine(repo,".github","workflows","dgspy-ci.yml"));
		Assert.Equal(1,Count(workflow,"-- pipeline `"));
		Assert.DoesNotContain(".\\build.ps1 net-x64",workflow,StringComparison.Ordinal);
		Assert.Contains("needs: build-net10-package",workflow,StringComparison.Ordinal);
		Assert.True(Count(workflow,"actions/download-artifact@v4")>=2);
	}
	[Fact]
	public void Short_installer_command_uses_its_package_and_the_standard_install_root() {
		var package=Path.Combine(root,"release"); var local=Path.Combine(root,"local-app-data");
		var options=DgSpyBuildTool.SimpleInstallOptions("CoDeX",new[]{"--force"},package+Path.DirectorySeparatorChar,local);
		Assert.Equal(package,options.Required("package"));
		Assert.Equal(Path.Combine(local,"Programs","dgSpyMcp"),options.Required("install"));
		Assert.Equal("codex",options.Required("agent"));
		Assert.True(options.Flag("force"));
	}
	[Fact]
	public void Short_host_only_command_uses_its_package_and_the_standard_install_root() {
		var package=Path.Combine(root,"release"); var local=Path.Combine(root,"local-app-data");
		var options=DgSpyBuildTool.HostOnlyInstallOptions(package+Path.DirectorySeparatorChar,local);
		Assert.Equal(package,options.Required("package"));
		Assert.Equal(Path.Combine(local,"Programs","dgSpyMcp"),options.Required("install"));
		Assert.True(options.Flag("host-only"));
	}
	[Fact]
	public void Pipeline_defaults_to_the_current_repo_local_artifacts_and_local_build_id() {
		var previous=Environment.CurrentDirectory;
		try {
			Environment.CurrentDirectory=root;
			var resolved=DgSpyBuildTool.ResolvePipelineOptions(new DgSpyBuildTool.Options(new(StringComparer.OrdinalIgnoreCase)));
			Assert.Equal(root,resolved.Repo);
			Assert.Equal(Path.Combine(root,"artifacts"),resolved.Artifacts);
			Assert.Equal("local",resolved.BuildId);
		}
		finally { Environment.CurrentDirectory=previous; }
	}

	[Fact]
	public async Task ComposeIsDeterministicAndPublishesOnlyVerifiedTrees() {
		var input=Fixture(); var output=Path.Combine(root,"layout");
		Assert.Equal(0,await Run("compose",input,"--output",output));
		var first=File.ReadAllText(Path.Combine(output,"dgspy-layout.json"));
		Assert.Equal(0,await Run("compose",input,"--output",output));
		Assert.Equal(first,File.ReadAllText(Path.Combine(output,"dgspy-layout.json")));
		File.AppendAllText(Path.Combine(output,"dnSpy.exe"),"tamper");
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"verify","--layout",output}));
	}
	[Fact]
	public async Task SnapshotPublishesImmutableHashedInput() {
		var source=Dir("snapshot-source"); Write(source,"nested/file.txt","one"); var output=Path.Combine(root,"snapshot");
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"snapshot","--source",source,"--output",output}));
		Assert.Equal("one",File.ReadAllText(Path.Combine(output,"content","nested","file.txt")));
		Write(source,"nested/file.txt","two"); Assert.Equal("one",File.ReadAllText(Path.Combine(output,"content","nested","file.txt")));
	}

	[Fact]
	public async Task FailedComposePreservesPreviousArtifact() {
		var input=Fixture(); var output=Path.Combine(root,"layout"); Assert.Equal(0,await Run("compose",input,"--output",output));
		var manifest=File.ReadAllText(Path.Combine(output,"dgspy-layout.json")); File.Delete(Path.Combine(input[0],"dnSpy.exe"));
		Assert.Equal(1,await Run("compose",input,"--output",output));
		Assert.Equal(manifest,File.ReadAllText(Path.Combine(output,"dgspy-layout.json")));
	}

	[Fact]
	public async Task PackageAndInstallVerifyBeforePublication() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Assert.Equal(0,await Run("compose",input,"--output",layout));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		Assert.True(File.Exists(Path.Combine(package,"install-dgspy.exe")));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		Assert.True(File.Exists(Path.Combine(install,"cli","dnSpy.exe")));
		File.AppendAllText(Path.Combine(package,"cli","dnSpy.exe"),"tamper");
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		Assert.Equal("host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
	}

	[Fact]
	public async Task FullInstallReplacesLegacyTreeInsteadOfMigratingIt() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Write(install,"cli/legacy.txt","old"); Write(install,"manifest.json","legacy");
		Assert.Equal(0,await Run("compose",input,"--output",layout));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		Assert.False(File.Exists(Path.Combine(install,"cli","legacy.txt")));
		Assert.True(File.Exists(Path.Combine(install,"cli","dgspy-layout.json")));
	}

	[Fact]
	public async Task HostOnlyRefusesLegacyInstallWithoutChangingIt() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Write(install,"cli/dnSpy.exe","legacy-host");
		Assert.Equal(0,await Run("compose",input,"--output",layout));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"}));
		Assert.Equal("legacy-host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
	}

	[Fact]
	public async Task HostOnlyRepairsAnEarlierVerifiedLayoutMissingNewHostFiles() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package})); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		var installedLayout=Path.Combine(install,"cli");
		foreach(var name in new[]{"Start-dgSpyRemoteHost.ps1","Start-dgSpyRemoteHost.cmd"}) File.Delete(Path.Combine(installedLayout,"launcher",name));
		var manifestPath=Path.Combine(installedLayout,"dgspy-layout.json"); var manifest=JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject(); var files=manifest["files"]!.AsArray();
		foreach(var entry in files.Where(entry=>((string?)entry?["path"])?.StartsWith("launcher/",StringComparison.OrdinalIgnoreCase)==true).ToArray()) files.Remove(entry);
		File.WriteAllText(manifestPath,manifest.ToJsonString());
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"}));
		Assert.True(File.Exists(Path.Combine(installedLayout,"launcher","Start-dgSpyRemoteHost.ps1")));
		Assert.True(File.Exists(Path.Combine(installedLayout,"launcher","Start-dgSpyRemoteHost.cmd")));
	}

	[Fact]
	public async Task HostOnlyPreservesControlPlaneAndRefusesProtocolDriftBeforeMutation() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package})); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		var installedCli=Path.Combine(install,"cli","bin","dgspy.exe"); var originalCli=File.ReadAllText(installedCli);
		Write(input[0],"dnSpy.exe","updated-host"); Write(input[2],"dgspy.exe","new-control-plane"); Write(input[6],"Start-dgSpyRemoteHost.cmd","updated-launcher");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		var installedManifestPath=Path.Combine(install,"cli","dgspy-layout.json"); var installedManifest=JsonNode.Parse(File.ReadAllText(installedManifestPath))!.AsObject();
		installedManifest["files"]!.AsArray().Single(entry=>(string?)entry?["path"]=="bin/shared-runtime.dll")!["owner"]="host"; File.WriteAllText(installedManifestPath,installedManifest.ToJsonString());
		using(var loadedControlPlaneDependency=File.Open(Path.Combine(install,"cli","bin","shared-runtime.dll"),FileMode.Open,FileAccess.Read,FileShare.Read))
			Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"}));
		Assert.Equal(originalCli,File.ReadAllText(installedCli)); Assert.Equal("updated-host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe"))); Assert.Equal("updated-launcher",File.ReadAllText(Path.Combine(install,"cli","launcher","Start-dgSpyRemoteHost.cmd")));
		Write(input[2],"dgSpy.Protocol.dll","changed-protocol"); Write(input[3],"dgSpy.Protocol.dll","changed-protocol"); Write(input[1],"dgSpy.Protocol.dll","changed-protocol");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"})); Assert.Equal("updated-host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
	}

	async Task<int> Run(string command,string[] input,params string[] extra)=>await DgSpyBuildTool.RunAsync(new[]{command,"--host",input[0],"--components",input[1],"--cli",input[2],"--gateway",input[3],"--bootstrap",input[4],"--native-bootstrap",input[5],"--launcher",input[6],"--watcher",input[7]}.Concat(extra).ToArray());
	string[] Fixture() {
		var host=Dir("host"); var components=Dir("components"); var cli=Dir("cli"); var gateway=Dir("gateway");
		Write(host,"dnSpy.exe","host"); Write(host,"bin/dnSpy.dll","host-bin"); Write(host,"bin/shared-runtime.dll","shared");
		Write(cli,"dgspy.exe","cli"); Write(cli,"DgSpyTool.exe","installer"); Write(cli,"dgSpy.Protocol.dll","protocol"); Write(cli,"shared-runtime.dll","shared");
		Write(gateway,"dgSpy.Gateway.exe","gateway"); Write(gateway,"dgSpy.Protocol.dll","protocol");
		// From the tool's own list, not a copy of it. This used to restate the ten names, so adding one
		// file to the packaged extension failed a compose test that has nothing to do with the change.
		foreach(var name in DgSpyBuildTool.ExtensionFiles) Write(components,name,name=="dgSpy.Protocol.dll"?"protocol":name);
		var bootstrap=Write(root,"bootstrap.payload","bootstrap"); var native=Write(root,"native.dll","native"); var launcher=Dir("launcher");
		Write(launcher,"Start-dgSpyRemoteHost.ps1","launcher"); Write(launcher,"Start-dgSpyRemoteHost.cmd","launcher");
		var watcher=Dir("watcher"); Write(watcher,"HookLab.Watcher.exe","watcher"); Write(watcher,"HookLab.Watcher.Companion.exe","companion"); Write(watcher,"payload/HookLab.Bootstrap.dll","bootstrap"); Write(watcher,"payload/HookLab.NativeBootstrap.x64.dll","native"); Write(watcher,"deployments/vmconnect-fullscreen/vmconnect-fullscreen.json","profile");
		return new[]{host,components,cli,gateway,bootstrap,native,launcher,watcher};
	}
	string Dir(string name) { var path=Path.Combine(root,name); Directory.CreateDirectory(path); return path; }
	static string Write(string directory,string relative,string text) { var path=Path.Combine(directory,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,text); return path; }
	static int Count(string text,string value) { var count=0; for(var at=0;(at=text.IndexOf(value,at,StringComparison.Ordinal))>=0;at+=value.Length) count++; return count; }
}
