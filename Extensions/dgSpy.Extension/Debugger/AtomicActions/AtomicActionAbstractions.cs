using System;
using System.Threading;
using System.Threading.Tasks;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>T09 adds the managed-payload implementation through this boundary.</summary>
	public interface IAtomicAction {
		string Kind { get; }
		Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken cancellationToken);
		Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken cancellationToken);
	}

	public sealed class AtomicActionContext {
		public AtomicActionContext(AtomicActionRequest request,AtomicActionStop stop,AtomicActionSlot slot) { Request=request; Stop=stop; Slot=slot; }
		public AtomicActionRequest Request { get; }
		public AtomicActionStop Stop { get; }
		public AtomicActionSlot Slot { get; }
	}

	public interface IAtomicActionBreakpoint : IDisposable {
		Guid OwnerToken { get; }
		string? BindError { get; }
		Task<bool> WaitBoundAsync(CancellationToken cancellationToken);
		Task ReleaseAsync(CancellationToken cancellationToken);
	}

	public interface IAtomicActionHost {
		long CaptureEventCursor();
		PatchedTargetState DetectPatchedTarget(AtomicActionSlot slot);
		Task<IAtomicActionBreakpoint> AddOwnedBreakpointAsync(AtomicActionSlot slot,Action<AtomicActionStop> hit,CancellationToken cancellationToken);
		/// <summary>The host marshals to the debugger thread and passes its one synchronous engine call to authorize.</summary>
		Task ContinueAsync(Action<Action> authorize,CancellationToken cancellationToken);
		Task<AtomicActionStop> WaitForOwnedStopAsync(Guid ownerToken,long afterEventCursor,CancellationToken cancellationToken);
		Task<AtomicActionSlot?> SelectNearbySlotAsync(AtomicActionRequest request,AtomicActionStop? stop,CancellationToken cancellationToken);
		Task ReleaseTemporaryHandlesAsync(CancellationToken cancellationToken);
		Task ResumeAsync(Action<Action> authorize,CancellationToken cancellationToken);
		Task<AtomicActionFinalState> ReadFinalStateAsync(CancellationToken cancellationToken);
	}
}
