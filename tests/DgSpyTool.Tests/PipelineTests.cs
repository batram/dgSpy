using Xunit;

public sealed class PipelineTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-tool-tests-"+Guid.NewGuid().ToString("N"));
	public PipelineTests()=>Directory.CreateDirectory(root);
	public void Dispose()=>Directory.Delete(root,true);
	[Fact]
	public void Publish_projects_declare_the_runtime_restored_by_the_component_graph() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		foreach(var project in new[]{"dgSpy.Cli/dgSpy.Cli.csproj","dgSpy.Gateway/dgSpy.Gateway.csproj"})
			Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>",File.ReadAllText(Path.Combine(repo,project.Replace('/',Path.DirectorySeparatorChar))));
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
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		Assert.True(File.Exists(Path.Combine(install,"cli","dnSpy.exe")));
		File.AppendAllText(Path.Combine(package,"cli","dnSpy.exe"),"tamper");
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		Assert.Equal("host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
	}

	[Fact]
	public async Task HostOnlyPreservesControlPlaneAndRefusesProtocolDriftBeforeMutation() {
		var input=Fixture(); var layout=Path.Combine(root,"layout"); var package=Path.Combine(root,"package"); var install=Path.Combine(root,"install");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package})); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install}));
		var installedCli=Path.Combine(install,"cli","bin","dgspy.exe"); var originalCli=File.ReadAllText(installedCli);
		Write(input[0],"dnSpy.exe","updated-host"); Write(input[2],"dgspy.exe","new-control-plane");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package})); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"}));
		Assert.Equal(originalCli,File.ReadAllText(installedCli)); Assert.Equal("updated-host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
		Write(input[2],"dgSpy.Protocol.dll","changed-protocol"); Write(input[3],"dgSpy.Protocol.dll","changed-protocol"); Write(input[1],"dgSpy.Protocol.dll","changed-protocol");
		Assert.Equal(0,await Run("compose",input,"--output",layout)); Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"package","--layout",layout,"--output",package}));
		Assert.Equal(1,await DgSpyBuildTool.RunAsync(new[]{"install","--package",package,"--install",install,"--host-only","true"})); Assert.Equal("updated-host",File.ReadAllText(Path.Combine(install,"cli","dnSpy.exe")));
	}

	async Task<int> Run(string command,string[] input,params string[] extra)=>await DgSpyBuildTool.RunAsync(new[]{command,"--host",input[0],"--components",input[1],"--cli",input[2],"--gateway",input[3],"--bootstrap",input[4],"--native-bootstrap",input[5]}.Concat(extra).ToArray());
	string[] Fixture() {
		var host=Dir("host"); var components=Dir("components"); var cli=Dir("cli"); var gateway=Dir("gateway");
		Write(host,"dnSpy.exe","host"); Write(host,"bin/dnSpy.dll","host-bin");
		Write(cli,"dgspy.exe","cli"); Write(cli,"dgSpy.Protocol.dll","protocol");
		Write(gateway,"dgSpy.Gateway.exe","gateway"); Write(gateway,"dgSpy.Protocol.dll","protocol");
		foreach(var name in new[]{"dgSpy.Extension.x.dll","dgSpy.Extension.x.pdb","dgSpy.Protocol.dll","dgSpy.Protocol.pdb","HookLab.Contracts.dll","HookLab.Contracts.pdb","HookLab.Host.Transport.dll","HookLab.Host.Transport.pdb"}) Write(components,name,name=="dgSpy.Protocol.dll"?"protocol":name);
		var bootstrap=Write(root,"bootstrap.payload","bootstrap"); var native=Write(root,"native.dll","native"); return new[]{host,components,cli,gateway,bootstrap,native};
	}
	string Dir(string name) { var path=Path.Combine(root,name); Directory.CreateDirectory(path); return path; }
	static string Write(string directory,string relative,string text) { var path=Path.Combine(directory,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path,text); return path; }
	static int Count(string text,string value) { var count=0; for(var at=0;(at=text.IndexOf(value,at,StringComparison.Ordinal))>=0;at+=value.Length) count++; return count; }
}
