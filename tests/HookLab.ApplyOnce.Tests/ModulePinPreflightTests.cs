using HookLab.Injector;
using Xunit;

namespace HookLab.ApplyOnce.Tests;

public sealed class ModulePinPreflightTests {
	static readonly string Image=typeof(ModulePinPreflightTests).Assembly.Location;
	static readonly string AssemblyName=typeof(ModulePinPreflightTests).Assembly.GetName().Name!;
	static readonly Guid Mvid=typeof(ModulePinPreflightTests).Module.ModuleVersionId;

	[Fact]
	public void Reads_the_module_mvid_the_runtime_reports() => Assert.Equal(Mvid,ModulePinPreflight.ModuleMvid(Image,AssemblyName));

	[Fact]
	public void A_pin_matching_the_module_file_is_allowed() => ModulePinPreflight.Check(Image,Definition(AssemblyName,Mvid));

	// The vmconnect case: the module was rebuilt by an update, and the old pin used to reach the
	// resident, commit its payloads and strand them without an endpoint.
	[Fact]
	public void A_stale_pin_is_refused_before_injection_and_names_the_current_build() {
		var stale=Guid.NewGuid();
		var error=Assert.Throws<StaleModulePinException>(()=>ModulePinPreflight.Check(Image,Definition(AssemblyName,stale)));
		Assert.Contains(stale.ToString("D"),error.Message,StringComparison.Ordinal);
		Assert.Contains(Mvid.ToString("D"),error.Message,StringComparison.Ordinal);
		Assert.Contains("refusing",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void An_undeterminable_module_defers_to_the_resident_guard() {
		ModulePinPreflight.Check(Image,Definition("NoSuchAssembly",Guid.NewGuid()));
		ModulePinPreflight.Check(Path.Combine(Path.GetTempPath(),"missing-"+Guid.NewGuid().ToString("N")+".exe"),Definition("missing",Guid.NewGuid()));
	}

	static HookDefinition Definition(string assembly,Guid mvid)=>new() { Id="test-hook",Target=new() { Assembly=assembly,ModuleMvid=mvid.ToString("D") } };
}
