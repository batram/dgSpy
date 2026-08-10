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

	/// <summary>
	/// Defect 8, and the one check here that is a source-shape guard rather than behaviour: the hit
	/// handler closed over the OwnedBreakpoint that AddOwnerAsync had not returned yet, so a hit arriving
	/// first found null, paused the engine and completed no waiter - the action then waited out its
	/// deadline reporting trigger_not_reached on a target that had reached the slot. The waiter must be
	/// created before the owner is added. RpcHost.AtomicActions.cs is a partial of a class no test can
	/// construct, which is why this is asserted on the source; everything else in that file was moved
	/// into AtomicActionHostSupport.cs and AtomicActionRecordStore.cs and is tested directly.
	/// </summary>
	[Fact]
	public void The_owned_breakpoint_hit_waiter_exists_before_the_owner_does() {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var source=File.ReadAllText(Path.Combine(root,"Extensions","dgSpy.Extension","Debugger","AtomicActions","RpcHost.AtomicActions.cs"));
		var waiter=source.IndexOf("var pending=new TaskCompletionSource<OwnedBreakpointHit>",StringComparison.Ordinal);
		var add=source.IndexOf("ownedBreakpoints.AddOwnerAsync",StringComparison.Ordinal);
		Assert.True(waiter>=0,"the hit waiter is no longer created by name; update this guard deliberately");
		Assert.True(add>waiter,"the owned-breakpoint hit waiter must be created before AddOwnerAsync is called");
		Assert.DoesNotContain("if(created is null) return true;",source,StringComparison.Ordinal);
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
