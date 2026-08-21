namespace dgSpy.Extension.Tests;

using Xunit;

public sealed class OwnedBreakpointBoundaryTests {
	/// <summary>The boundary, per engine: dgSpy reaches an engine only through the narrow provider
	/// contract, never through the public breakpoint collection, and each engine's bridge is the only
	/// place its raw debugger type is named.
	///
	/// <para>Both engines are checked. While CorDebug was the only implementation this could be one
	/// assertion; a second engine that reached for <c>DbgCodeBreakpointsService</c> instead would put
	/// dgSpy's arrival breakpoint in the user's breakpoint list, which is exactly the thing the whole
	/// owned-breakpoint mechanism exists to avoid.</para></summary>
	[Fact]
	public void Owned_breakpoints_stay_below_the_public_breakpoint_guard() {
		var root=FindRepositoryRoot();
		var service=File.ReadAllText(Path.Combine(root,"Extensions","dgSpy.Extension","Debugger","OwnedBreakpoints","OwnedBreakpointService.cs"));
		Assert.Contains("IDgSpyOwnedBreakpointProvider",service);
		Assert.DoesNotContain("DbgCodeBreakpointsService",service);

		var corDebug=Engine("dnSpy.Debugger.DotNet.CorDebug");
		Assert.Contains("DnDebugger",corDebug);
		Assert.DoesNotContain("DbgCodeBreakpointsService",corDebug);

		var mono=Engine("dnSpy.Debugger.DotNet.Mono");
		Assert.Contains("VirtualMachine",mono);
		Assert.DoesNotContain("DbgCodeBreakpointsService",mono);

		string Engine(string project) =>
			File.ReadAllText(Path.Combine(root,"Extensions","dnSpy.Debugger",project,"Impl","DgSpyOwnedBreakpointFacade.cs"));
	}

	static string FindRepositoryRoot() {
		var directory=new DirectoryInfo(AppContext.BaseDirectory);
		while(directory is not null) {
			if(File.Exists(Path.Combine(directory.FullName,"AGENTS.md"))) return directory.FullName;
			directory=directory.Parent;
		}
		throw new DirectoryNotFoundException("Could not locate dgSpy repository root.");
	}
}
