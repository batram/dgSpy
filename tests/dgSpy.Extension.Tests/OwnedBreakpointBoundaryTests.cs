namespace dgSpy.Extension.Tests;

using Xunit;

public sealed class OwnedBreakpointBoundaryTests {
	[Fact]
	public void Owned_breakpoints_stay_below_the_public_breakpoint_guard() {
		var root=FindRepositoryRoot();
		var service=File.ReadAllText(Path.Combine(root,"Extensions","dgSpy.Extension","Debugger","OwnedBreakpoints","OwnedBreakpointService.cs"));
		var facade=File.ReadAllText(Path.Combine(root,"Extensions","dnSpy.Debugger","dnSpy.Debugger.DotNet.CorDebug","Impl","DgSpyOwnedBreakpointFacade.cs"));
		Assert.Contains("IDgSpyOwnedBreakpointService",service);
		Assert.DoesNotContain("DbgCodeBreakpointsService",service);
		Assert.Contains("DnDebugger",facade);
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
