using Xunit;

/// <summary>
/// Retention for build-id-scoped artifacts.
///
/// The pipeline is immutable on purpose - it never overwrites a layout or a package, which is what
/// makes a verification mean anything. Immutable with no retention is unbounded growth: one day of
/// gate runs left 25 GB behind, four trees per run, and nothing ever removed them.
///
/// The discriminator these tests exist to protect is which build ids are eligible. A generated id is
/// used once and never named again; a deliberate one like <c>local</c> or <c>glorpy</c> is reused and
/// may be referenced from outside this tool, so it is never deleted however old it looks.
/// </summary>
public sealed class PruneTests : IDisposable {
	readonly string root=Path.Combine(Path.GetTempPath(),"dgspy-prune-tests-"+Guid.NewGuid().ToString("N"));
	public PruneTests()=>Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root,true); } catch { } }

	[Theory]
	[InlineData("gate-4456231ea6454f4a99e7f8aec014a5d9")]
	[InlineData(".gate-13c364698e8348d69de1da1864eff3fa.staging-91a5a6e758574ba9951ad5ee55de2ffa")]
	[InlineData("ci-0123456789abcdef0123456789abcdef")]
	public void A_generated_build_id_is_single_use(string name)=>Assert.True(DgSpyBuildTool.IsSingleUseBuildId(name));

	[Theory]
	[InlineData("local")]
	[InlineData("glorpy")]
	[InlineData("pipeline-v2")]
	[InlineData("concise-installer")]
	[InlineData("hooklab-eligibility")]
	[InlineData("coldstart-test")]
	// Twelve hex characters, not thirty-two: a deliberate short name, not a generated id.
	[InlineData("bundled-7f396d2f969c")]
	public void A_named_build_id_is_never_eligible(string name)=>Assert.False(DgSpyBuildTool.IsSingleUseBuildId(name));

	[Fact]
	public async Task Prune_keeps_the_newest_and_removes_the_rest() {
		var area=Area("layouts");
		var oldest=Aged(area,"gate-"+new string('a',32),TimeSpan.FromHours(5));
		var middle=Aged(area,"gate-"+new string('b',32),TimeSpan.FromHours(3));
		var newest=Aged(area,"gate-"+new string('c',32),TimeSpan.FromHours(1));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",root,"--keep","1"}));
		Assert.True(Directory.Exists(newest));
		Assert.False(Directory.Exists(middle));
		Assert.False(Directory.Exists(oldest));
	}

	/// <summary>The property that keeps this safe to run unattended.</summary>
	[Fact]
	public async Task Prune_never_removes_a_named_build_however_old() {
		var area=Area("packages\\dgspy-win-x64");
		var named=Aged(area,"glorpy",TimeSpan.FromDays(90));
		var alsoNamed=Aged(area,"local",TimeSpan.FromDays(90));
		var generated=Aged(area,"gate-"+new string('d',32),TimeSpan.FromDays(90));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",root,"--keep","1"}));
		Assert.True(Directory.Exists(named));
		Assert.True(Directory.Exists(alsoNamed));
		// The only generated one is also the newest generated one, so keep 1 retains it.
		Assert.True(Directory.Exists(generated));
	}

	[Fact]
	public async Task Prune_covers_every_area_the_pipeline_writes() {
		foreach(var area in DgSpyBuildTool.BuildIdScopedAreas) {
			Aged(Area(area),"gate-"+new string('e',32),TimeSpan.FromHours(4));
			Aged(Area(area),"gate-"+new string('f',32),TimeSpan.FromHours(1));
		}
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",root,"--keep","1"}));
		foreach(var area in DgSpyBuildTool.BuildIdScopedAreas) {
			Assert.False(Directory.Exists(Path.Combine(Area(area),"gate-"+new string('e',32))),area+" was not pruned.");
			Assert.True(Directory.Exists(Path.Combine(Area(area),"gate-"+new string('f',32))),area+" lost its newest build.");
		}
	}

	[Fact]
	public async Task A_dry_run_removes_nothing() {
		var area=Area("host-raw");
		var old=Aged(area,"gate-"+new string('1',32),TimeSpan.FromHours(9));
		Aged(area,"gate-"+new string('2',32),TimeSpan.FromHours(1));
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",root,"--keep","1","--dry-run","true"}));
		Assert.True(Directory.Exists(old));
	}

	/// <summary>Retaining nothing would delete the layout a gate had just verified, so it is refused
	/// rather than obeyed.</summary>
	[Fact]
	public async Task Keeping_nothing_is_refused() {
		Aged(Area("layouts"),"gate-"+new string('3',32),TimeSpan.FromHours(1));
		Assert.NotEqual(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",root,"--keep","0"}));
	}

	[Fact]
	public async Task Pruning_an_absent_artifacts_tree_is_not_an_error() =>
		Assert.Equal(0,await DgSpyBuildTool.RunAsync(new[]{"prune","--artifacts",Path.Combine(root,"never-created")}));

	[Fact]
	public void A_successful_pipeline_retains_only_the_newest_generated_build() {
		foreach(var area in DgSpyBuildTool.BuildIdScopedAreas) {
			Aged(Area(area),"gate-"+new string('a',32),TimeSpan.FromHours(3));
			Aged(Area(area),"gate-"+new string('b',32),TimeSpan.FromHours(2));
			Aged(Area(area),"gate-"+new string('c',32),TimeSpan.FromHours(1));
			Aged(Area(area),"release",TimeSpan.FromDays(30));
		}

		DgSpyBuildTool.PruneAfterSuccessfulPipeline(root);

		foreach(var area in DgSpyBuildTool.BuildIdScopedAreas) {
			Assert.False(Directory.Exists(Path.Combine(Area(area),"gate-"+new string('a',32))),area);
			Assert.False(Directory.Exists(Path.Combine(Area(area),"gate-"+new string('b',32))),area);
			Assert.True(Directory.Exists(Path.Combine(Area(area),"gate-"+new string('c',32))),area);
			Assert.True(Directory.Exists(Path.Combine(Area(area),"release")),area);
		}
	}

	[Fact]
	public void Host_cleanup_removes_legacy_publish_and_x86_builds_but_preserves_runtime_assets() {
		var dnSpy=Area("dnSpy-source");
		var canonical=Path.Combine(dnSpy,"dnSpy","bin","Release","net10.0-windows","win-x64","publish");
		var legacy=Path.Combine(dnSpy,"Roslyn","Project","bin","Release","net10.0-windows","win-x64","publish");
		var x86=Path.Combine(dnSpy,"Roslyn","Project","obj","Release","net10.0-windows","win-x86");
		var runtimeAsset=Path.Combine(dnSpy,"dnSpy","bin","Release","net10.0-windows","runtimes","win-x86");
		foreach(var directory in new[]{canonical,legacy,x86,runtimeAsset}) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory,"content.txt"),directory); }

		DgSpyBuildTool.CleanUnsupportedHostBuildOutputs(dnSpy,canonical);

		Assert.True(Directory.Exists(canonical));
		Assert.False(Directory.Exists(legacy));
		Assert.False(Directory.Exists(x86));
		Assert.True(Directory.Exists(runtimeAsset));
	}

	[Fact]
	public void Host_overlay_adds_shared_extensions_without_nested_publish_or_x86_launcher() {
		var shared=Area("shared-host"); var publish=Area("canonical-publish");
		File.WriteAllText(Path.Combine(shared,"dnSpy.Debugger.x.dll"),"extension");
		File.WriteAllText(Path.Combine(shared,"dnSpy-x86.exe"),"unsupported");
		Directory.CreateDirectory(Path.Combine(shared,"Themes")); File.WriteAllText(Path.Combine(shared,"Themes","Dark.xaml"),"theme");
		Directory.CreateDirectory(Path.Combine(shared,"FileLists")); File.WriteAllText(Path.Combine(shared,"FileLists","build.txt"),"internal");
		Directory.CreateDirectory(Path.Combine(shared,"win-x64","publish")); File.WriteAllText(Path.Combine(shared,"win-x64","publish","nested.dll"),"nested");
		File.WriteAllText(Path.Combine(publish,"coreclr.dll"),"runtime");

		DgSpyBuildTool.OverlaySharedHostOutput(shared,publish);

		Assert.True(File.Exists(Path.Combine(publish,"coreclr.dll")));
		Assert.True(File.Exists(Path.Combine(publish,"dnSpy.Debugger.x.dll")));
		Assert.True(File.Exists(Path.Combine(publish,"Themes","Dark.xaml")));
		Assert.False(File.Exists(Path.Combine(publish,"dnSpy-x86.exe")));
		Assert.False(Directory.Exists(Path.Combine(publish,"FileLists")));
		Assert.False(Directory.Exists(Path.Combine(publish,"win-x64")));
	}

	string Area(string name) { var path=Path.Combine(root,name); Directory.CreateDirectory(path); return path; }

	static string Aged(string area,string name,TimeSpan age) {
		var path=Path.Combine(area,name);
		Directory.CreateDirectory(path);
		File.WriteAllText(Path.Combine(path,"content.txt"),name);
		Directory.SetLastWriteTimeUtc(path,DateTime.UtcNow-age);
		return path;
	}
}
