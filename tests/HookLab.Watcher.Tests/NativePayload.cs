using Xunit;

namespace HookLab.Watcher.Tests;

/// <summary>
/// Live injection needs the HookLab payloads, and the native half is a C++ vcxproj that only the
/// packaging pipeline builds -- "dotnet test" never produces it. A fresh checkout therefore has no
/// payload at all, so these tests report as skipped there instead of failing on a missing artifact.
/// The search mirrors OneShotInjector.FindPayload so the probe cannot drift from real resolution.
/// </summary>
static class NativePayload {
	internal const string SkipReason="HookLab payloads are not built. Run the packaging pipeline (Build/DgSpyTool build-components) to enable live injection tests.";

	internal static string? MissingReason=>Found()?null:SkipReason;

	static bool Found() {
		foreach(var candidate in new[]{AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"hooklab")}) if(Complete(candidate)) return true;
		for(var current=new DirectoryInfo(Environment.CurrentDirectory);current is not null;current=current.Parent) {
			var managed=Path.Combine(current.FullName,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll");
			var native=Path.Combine(current.FullName,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll");
			if(File.Exists(managed)&&File.Exists(native)) return true;
		}
		return false;
	}

	static bool Complete(string directory)=>File.Exists(Path.Combine(directory,"HookLab.NativeBootstrap.x64.dll"))&&File.Exists(Path.Combine(directory,"HookLab.Bootstrap.dll"));
}

sealed class NativePayloadFactAttribute:FactAttribute {
	public override string? Skip { get=>NativePayload.MissingReason??base.Skip; set=>base.Skip=value; }
}

sealed class NativePayloadTheoryAttribute:TheoryAttribute {
	public override string? Skip { get=>NativePayload.MissingReason??base.Skip; set=>base.Skip=value; }
}
