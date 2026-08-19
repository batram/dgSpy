using Xunit;
using System.Security.Cryptography;

/// <summary>
/// Package policy for every native artifact dgSpy injects into a target process: its direct and
/// delay-load imports must stay inside a reviewed allowlist.
///
/// The defect this prevents: a Release build of HookLab.NativeBootstrap linked /MD and therefore
/// imported VCRUNTIME140_1.dll, which ships only with the Visual C++ 2019-or-later redistributable.
/// A Windows Server 2019 target carried the 2015/2017 one, so LoadLibraryW in the target returned
/// NULL and HookLab initialization reported only that the runtime component did not load - a message
/// that could equally have meant a denied path. The fix was a build setting (RuntimeLibrary
/// MultiThreaded) applied by hand; this makes it enforced, because the next build setting that
/// widens the surface will do so just as quietly.
///
/// The allowlist lives here, in the test, and not in the project. Widening it is then a reviewed
/// decision rather than a side effect of changing how something is compiled.
///
/// This is deterministic package policy. It proves the declared dependency surface stayed inside
/// what was reviewed. It does not prove the artifact loads: the target-side loader stays
/// authoritative for that, and no preflight replaces it.
/// </summary>
public sealed class InjectedNativeImportPolicyTests {
	/// <summary>Modules an injected artifact may import. KERNEL32 is present in every Windows process
	/// before anything is injected; mscoree is present in every process hosting a CLR, which is the
	/// only kind of process this artifact is ever injected into. Nothing else is guaranteed to exist
	/// on a machine we do not control.
	///
	/// OLEAUT32 was added deliberately on 2026-08-19, when the native bootstrap gained the ability to
	/// enter a chosen application domain. Reaching a specific AppDomain means talking COM to
	/// <c>_AppDomain</c>, and <c>BSTR</c> and <c>SAFEARRAY</c> live in oleaut32; there is no way to do
	/// it without them. It qualifies on the same test as the other two: a core Windows DLL, present on
	/// every installation and listed in KnownDLLs, so no machine can be missing it.
	///
	/// That is the whole distinction this list exists to enforce. The import that caused the original
	/// incident, VCRUNTIME140_1.dll, ships with a Visual C++ redistributable that a server may simply
	/// not have. "Always present on Windows" and "usually installed" are different claims, and only the
	/// first one belongs here.</summary>
	static readonly string[] AllowedModules={ "kernel32.dll","mscoree.dll","oleaut32.dll" };

	/// <summary>File names of the native artifacts the product injects into a target process.</summary>
	static readonly string[] InjectedArtifactNames={ "HookLab.NativeBootstrap.x64.dll" };

	[Fact]
	public void Injected_native_artifacts_import_only_reviewed_modules() {
		var artifacts=Discover();
		Assert.True(artifacts.Count>0,"No injected native artifact was found to check. Build the package first: dotnet run --project Build\\DgSpyTool -- pipeline. A vacuous pass here is the failure this test exists to prevent.");
		var violations=new List<string>();
		foreach(var artifact in artifacts) {
			var surface=NativeImportSurface.Read(artifact);
			// A real injected artifact always imports something. An empty surface means the reader
			// failed to find the directories rather than that the artifact is clean.
			Assert.True(surface.Direct.Count>0,artifact+" declares no direct imports, which no working injected artifact does. Suspect the reader before the artifact.");
			foreach(var module in surface.Direct) if(!Allowed(module)) violations.Add(artifact+" imports "+module);
			foreach(var module in surface.DelayLoad) if(!Allowed(module)) violations.Add(artifact+" delay-load imports "+module);
		}
		Assert.True(violations.Count==0,"Injected native artifacts left the reviewed import allowlist ("+String.Join(", ",AllowedModules)+"):"+Environment.NewLine+String.Join(Environment.NewLine,violations));
	}

	[Fact]
	public void Injected_native_artifacts_import_the_runtime_host_they_need() {
		var artifacts=Discover();
		Assert.True(artifacts.Count>0,"No injected native artifact was found to check. Build the package first.");
		foreach(var artifact in artifacts) {
			var surface=NativeImportSurface.Read(artifact);
			Assert.Contains(surface.All,module=>String.Equals(module,"mscoree.dll",StringComparison.OrdinalIgnoreCase));
			Assert.Contains(surface.All,module=>String.Equals(module,"kernel32.dll",StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void The_policy_rejects_an_artifact_carrying_the_dynamic_crt_surface() {
		var image=NativeImportSurfaceFixture.Build();
		var surface=NativeImportSurface.Read(image,NativeImportSurfaceFixture.FileName);
		Assert.Equal(NativeImportSurfaceFixture.DirectModules,surface.Direct);
		Assert.Equal(NativeImportSurfaceFixture.DelayLoadModules,surface.DelayLoad);
		Assert.Contains(surface.Direct,module=>!Allowed(module));
		Assert.Contains(surface.DelayLoad,module=>!Allowed(module));
		Assert.Equal(new[]{"VCRUNTIME140_1.dll"},surface.Direct.Where(module=>!Allowed(module)).ToArray());
	}

	/// <summary>The fixture is generated rather than checked in, so it cannot depend on whichever
	/// Visual C++ toolchain runs the gate. Pinning its digest keeps it a fixed artifact anyway: a
	/// change to the generator that silently stopped producing a violating image would otherwise
	/// turn the negative case into a vacuous pass.</summary>
	[Fact]
	public void The_negative_fixture_is_reproducible_and_pinned() {
		var digest=Convert.ToHexString(SHA256.HashData(NativeImportSurfaceFixture.Build())).ToLowerInvariant();
		Assert.Equal(Convert.ToHexString(SHA256.HashData(NativeImportSurfaceFixture.Build())).ToLowerInvariant(),digest);
		Assert.Equal(PinnedFixtureDigest,digest);
	}

	/// <summary>The fixture must never be mistaken for product: it is not written into any layout, and
	/// its name is not one an assembly or a loader would ever ask for.</summary>
	[Fact]
	public void The_negative_fixture_is_isolated_from_shipped_artifacts() {
		Assert.DoesNotContain(NativeImportSurfaceFixture.FileName,InjectedArtifactNames);
		Assert.EndsWith(".notadll",NativeImportSurfaceFixture.FileName,StringComparison.Ordinal);
		foreach(var root in Roots())
			Assert.Empty(Directory.EnumerateFiles(root,NativeImportSurfaceFixture.FileName,SearchOption.AllDirectories));
	}

	const string PinnedFixtureDigest="c4831a56670424ffa9afb316d719845d30a5067e5e682b6ca0c1bf81aefd2cac";

	static bool Allowed(string module)=>AllowedModules.Contains(module,StringComparer.OrdinalIgnoreCase);

	static IReadOnlyList<string> Discover() {
		var found=new List<string>();
		foreach(var root in Roots())
			foreach(var name in InjectedArtifactNames)
				found.AddRange(Directory.EnumerateFiles(root,name,SearchOption.AllDirectories));
		return found.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).ToArray();
	}

	/// <summary>The native build output, plus the composed layout when the gate has produced one. The
	/// layout matters because the artifact is copied to more than one place in it, and a copy is what
	/// actually reaches a machine.</summary>
	static IEnumerable<string> Roots() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var native=Path.Combine(repo,"HookLab","HookLab.NativeBootstrap","bin","Release");
		if(Directory.Exists(native)) yield return native;
		var layout=Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		if(!String.IsNullOrWhiteSpace(layout)&&Directory.Exists(layout)) yield return Path.GetFullPath(layout);
	}
}
