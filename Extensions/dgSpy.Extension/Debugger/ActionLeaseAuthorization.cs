using System;

namespace dgSpy.Extension.Debugger {
	/// <summary>
	/// The lease decision as a guarded debugger mutation actually needs it: captured on the thread that
	/// asks, evaluated on the thread that mutates.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <see cref="ActionLeaseCoordinator.TryGetBlock(int?, string, out ActionLeaseInfo)"/> answers for the
	/// calling thread, because an owner's authorization is thread-scoped. Every guarded dnSpy mutation
	/// entry point defers its engine call to the debugger dispatcher, so asking on the caller's thread
	/// decides one thing and mutates another: a lease acquired inside that window let the queued mutation
	/// through unguarded, which is the race this type exists to close.
	/// </para>
	/// <para>
	/// Moving the question to the mutating thread alone is not enough either - it refuses the lease
	/// owner's own mutations, since the debugger thread does not carry the owner's authorization by the
	/// time a queued callback runs. So the caller captures <em>which owner it is</em> first, and the
	/// mutating thread admits that capture only against that same owner. A capture taken under one lease
	/// never passes a mutation blocked by a different, later one.
	/// </para>
	/// <para>
	/// Both halves read the coordinator's own live state, and this type holds none of its own. An earlier
	/// version mirrored ownership from <c>LeaseChanged</c> notifications, which made every answer depend on
	/// this object having existed when the lease was acquired. It never does on the cold path: dnSpy
	/// imports its guards as <c>Lazy&lt;DbgActionGuard&gt;</c> and first realizes them inside
	/// <c>CaptureAuthorization()</c>, so the very first atomic action of a host's lifetime acquired its
	/// lease with nothing subscribed, then built this bridge with an empty mirror - and the mutating
	/// thread refused the owner's own mutation. Nothing here may derive current ownership from an event
	/// stream; ask the coordinator.
	/// </para>
	/// <para>
	/// This type is deliberately free of any dnSpy reference so the race can be tested directly against
	/// the real coordinator and the real debugger dispatcher.
	/// </para>
	/// </remarks>
	public sealed class ActionLeaseAuthorization : IDisposable {
		readonly ActionLeaseCoordinator coordinator;

		public ActionLeaseAuthorization(ActionLeaseCoordinator coordinator) =>
			this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

		/// <summary>
		/// Captures, on the calling thread, the identity of every lease this thread is currently authorized
		/// under. Returns null for a caller that owns nothing, which is every external and UI caller.
		/// </summary>
		public object? Capture() {
			var owned = coordinator.CaptureAuthorizedActionIds();
			return owned is null ? null : new OwnedLeases(owned);
		}

		/// <summary>
		/// The authoritative decision, made on the mutating thread immediately before the mutation.
		/// <paramref name="authorization"/> is what <see cref="Capture"/> returned on the caller's thread.
		/// A null process id means a mutation no single process owns - breakpoint state - and the
		/// coordinator consults every active owner for it in one snapshot read.
		/// </summary>
		public bool TryGetBlock(int? processId, string operation, object? authorization, out ActionLeaseInfo info) =>
			coordinator.TryGetBlock(processId, operation, (authorization as OwnedLeases)?.ActionIds, out info);

		/// <summary>Nothing to release: this bridge subscribes to nothing and caches nothing.</summary>
		public void Dispose() { }

		/// <summary>Opaque to every consumer; identity is compared, never inferred.</summary>
		sealed class OwnedLeases {
			internal string[] ActionIds { get; }
			internal OwnedLeases(string[] actionIds) => ActionIds = actionIds;
		}
	}
}
