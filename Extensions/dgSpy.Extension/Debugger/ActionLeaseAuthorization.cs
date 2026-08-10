using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
	/// This type is deliberately free of any dnSpy reference so the race can be tested directly against
	/// the real coordinator and the real debugger dispatcher.
	/// </para>
	/// </remarks>
	public sealed class ActionLeaseAuthorization : IDisposable {
		/// <summary>Operation name used only to ask the coordinator a question; it is never reported.</summary>
		const string authorizationProbe = "capture_action_authorization";

		readonly ActionLeaseCoordinator coordinator;
		// Which action currently owns each process, mirrored from the coordinator's own notifications.
		// It answers "who would block an unauthorized caller", which TryGetBlock cannot report to the
		// owner itself, and it is the enumeration used by a process-less mutation.
		readonly ConcurrentDictionary<int, string> ownerByProcess = new ConcurrentDictionary<int, string>();
		bool disposed;

		public ActionLeaseAuthorization(ActionLeaseCoordinator coordinator) {
			this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
			coordinator.LeaseChanged += Coordinator_LeaseChanged;
		}

		void Coordinator_LeaseChanged(ActionLeaseInfo info, bool acquired) {
			if (acquired)
				ownerByProcess[info.ProcessId] = info.ActionId;
			else {
				// Remove only this action's own entry: an expiring lease and its replacement can report in
				// either order, and clearing unconditionally would forget the live owner.
				((ICollection<KeyValuePair<int, string>>)ownerByProcess).Remove(new KeyValuePair<int, string>(info.ProcessId, info.ActionId));
			}
		}

		/// <summary>
		/// Captures, on the calling thread, the identity of every lease this thread is currently authorized
		/// under. Returns null for a caller that owns nothing, which is every external and UI caller.
		/// </summary>
		public object? Capture() {
			List<string>? owned = null;
			foreach (var pair in ownerByProcess) {
				// A lease this thread is not authorized under blocks it, so a lease that does not block is
				// one this thread owns.
				if (!coordinator.TryGetBlock(pair.Key, authorizationProbe, out _)) {
					if (owned is null)
						owned = new List<string>();
					owned.Add(pair.Value);
				}
			}
			return owned is null ? null : new OwnedLeases(owned.ToArray());
		}

		/// <summary>
		/// The authoritative decision, made on the mutating thread immediately before the mutation.
		/// <paramref name="authorization"/> is what <see cref="Capture"/> returned on the caller's thread.
		/// </summary>
		public bool TryGetBlock(int? processId, string operation, object? authorization, out ActionLeaseInfo info) {
			if (processId.HasValue)
				return Blocks(processId.Value, operation, authorization, out info);
			// A process-less mutation - breakpoint state is not owned by one process - is blocked by any
			// active lease, so every owner has to be consulted. The coordinator reports only the first
			// lease that blocks this thread, and the captured authorization may name exactly that one
			// while another lease still blocks; asking per process is what makes the answer complete.
			if (coordinator.TryGetBlock(null, operation, out info) && !IsOwner(authorization, info))
				return true;
			foreach (var pair in ownerByProcess) {
				if (Blocks(pair.Key, operation, authorization, out info))
					return true;
			}
			info = null!;
			return false;
		}

		bool Blocks(int processId, string operation, object? authorization, out ActionLeaseInfo info) {
			if (!coordinator.TryGetBlock(processId, operation, out info))
				return false;
			if (IsOwner(authorization, info)) {
				info = null!;
				return false;
			}
			return true;
		}

		static bool IsOwner(object? authorization, ActionLeaseInfo info) =>
			authorization is OwnedLeases capture && capture.Owns(info.ActionId);

		public void Dispose() {
			if (disposed)
				return;
			disposed = true;
			coordinator.LeaseChanged -= Coordinator_LeaseChanged;
			ownerByProcess.Clear();
		}

		/// <summary>Opaque to every consumer; identity is compared, never inferred.</summary>
		sealed class OwnedLeases {
			readonly string[] actionIds;
			internal OwnedLeases(string[] actionIds) => this.actionIds = actionIds;
			internal bool Owns(string actionId) {
				for (int i = 0; i < actionIds.Length; i++) {
					if (String.Equals(actionIds[i], actionId, StringComparison.Ordinal))
						return true;
				}
				return false;
			}
		}
	}
}
