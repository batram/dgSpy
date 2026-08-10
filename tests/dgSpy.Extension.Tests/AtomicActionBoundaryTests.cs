using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class AtomicActionBoundaryTests {
	[Fact]
	public void Atomic_actions_use_only_the_owned_breakpoint_facility() {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var directory=Path.Combine(root,"Extensions","dgSpy.Extension","Debugger","AtomicActions");
		var source=String.Join("\n",Directory.GetFiles(directory,"*.cs").Select(File.ReadAllText));
		Assert.Contains("ownedBreakpoints.AddOwnerAsync",source,StringComparison.Ordinal);
		Assert.DoesNotContain("DbgCodeBreakpointsService",source,StringComparison.Ordinal);
		Assert.DoesNotContain("breakpoints.Add",source,StringComparison.Ordinal);
		Assert.DoesNotContain("breakpoints.Clear",source,StringComparison.Ordinal);
	}

	[Fact]
	public void State_machine_never_carries_authorization_across_an_await() {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var source=File.ReadAllText(Path.Combine(root,"Extensions","dgSpy.Extension","Debugger","AtomicActions","AtomicActionStateMachine.cs"));
		Assert.Contains("lease.ExecuteMutation(mutation)",source,StringComparison.Ordinal);
		Assert.DoesNotContain("ExecuteMutation(async",source,StringComparison.Ordinal);
		Assert.DoesNotContain("ExecuteMutation<Task",source,StringComparison.Ordinal);
	}
}
