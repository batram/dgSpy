using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace dgSpy.Extension.Debugger {
	public sealed class ActionLeaseInfo {
		public int ProcessId { get; }
		public string ActionName { get; }
		public string ActionId { get; }
		public DateTime DeadlineUtc { get; }
		public string StatusOperation { get; }
		public string CancelOperation { get; }

		internal ActionLeaseInfo(int processId,string actionName,string actionId,DateTime deadlineUtc,string statusOperation,string cancelOperation) {
			ProcessId=processId; ActionName=actionName; ActionId=actionId; DeadlineUtc=deadlineUtc;
			StatusOperation=statusOperation; CancelOperation=cancelOperation;
		}

		public string FormatBlockReason(string operation) =>
			$"Debugger operation '{operation}' is blocked because atomic action '{ActionName}' ({ActionId}) owns process {ProcessId} until {DeadlineUtc.ToUniversalTime():O}. Use '{StatusOperation}' to inspect it or '{CancelOperation}' to cancel it.";
	}

	public sealed class ActionLeaseConflictException : InvalidOperationException {
		public ActionLeaseInfo Owner { get; }
		public ActionLeaseConflictException(ActionLeaseInfo owner,string operation) : base(owner.FormatBlockReason(operation)) => Owner=owner;
	}

	/// <summary>Process-scoped, bounded ownership shared by RPC dispatch and dnSpy's optional guard export.</summary>
	public sealed class ActionLeaseCoordinator : IDisposable {
		public static ActionLeaseCoordinator Shared { get; }=new ActionLeaseCoordinator();

		readonly object sync=new object();
		volatile Dictionary<int,ActionLease> snapshot=new Dictionary<int,ActionLease>();
		[ThreadStatic] static object? currentAuthorization;
		bool disposed;

		public event Action<ActionLeaseInfo,bool>? LeaseChanged;
		public event Action<ActionLeaseInfo,string>? ExternalDebuggerAction;

		public ActionLease Acquire(int processId,string actionName,string actionId,DateTime deadlineUtc,string statusOperation,string cancelOperation,CancellationToken ownerLifetime=default) {
			if(processId<=0) throw new ArgumentOutOfRangeException(nameof(processId));
			if(string.IsNullOrWhiteSpace(actionName)) throw new ArgumentException("Action name is required.",nameof(actionName));
			if(string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("Action id is required.",nameof(actionId));
			if(string.IsNullOrWhiteSpace(statusOperation)) throw new ArgumentException("Status operation is required.",nameof(statusOperation));
			if(string.IsNullOrWhiteSpace(cancelOperation)) throw new ArgumentException("Cancel operation is required.",nameof(cancelOperation));
			var utcDeadline=deadlineUtc.ToUniversalTime();
			if(utcDeadline<=DateTime.UtcNow) throw new ArgumentOutOfRangeException(nameof(deadlineUtc),"The lease deadline must be in the future.");
			ActionLease lease;
			ActionLease? expired=null;
			lock(sync) {
				ThrowIfDisposed();
				if(snapshot.TryGetValue(processId,out var existing)) {
					// An expired lease still owns the process while its owner finishes cleanup: that owner is
					// still resuming the target and releasing its handles, and granting a second lease during
					// that window is exactly the double-ownership this coordinator exists to prevent. The
					// window is bounded, so a crashed owner cannot hold the process indefinitely.
					if(existing.OwnershipEndsUtc>DateTime.UtcNow) throw new ActionLeaseConflictException(existing.Info,"acquire_action_lease");
					expired=existing;
				}
				lease=new ActionLease(this,new ActionLeaseInfo(processId,actionName,actionId,utcDeadline,statusOperation,cancelOperation),new object());
				var next=new Dictionary<int,ActionLease>(snapshot) { [processId]=lease };
				snapshot=next;
			}
			if(expired is not null) { var announce=expired.MarkInactive(); expired.ReleaseResources(); if(announce) LeaseChanged?.Invoke(expired.Info,false); }
			LeaseChanged?.Invoke(lease.Info,true);
			lease.Initialize(ownerLifetime);
			return lease;
		}

		/// <summary>Answers for the calling thread's own authorization only. A thread that decides here and
		/// mutates elsewhere must use <see cref="CaptureAuthorizedActionIds"/> and the overload below.</summary>
		public bool TryGetBlock(int? processId,string operation,out ActionLeaseInfo info) =>
			TryGetBlock(processId,operation,null,out info);

		/// <summary>
		/// The authoritative decision for a mutation whose authorization was captured on another thread.
		/// <paramref name="authorizedActionIds"/> is what <see cref="CaptureAuthorizedActionIds"/> returned
		/// there; it admits exactly the leases it names, so a capture taken under one lease never passes a
		/// mutation blocked by a later, different one.
		/// </summary>
		public bool TryGetBlock(int? processId,string operation,IReadOnlyList<string>? authorizedActionIds,out ActionLeaseInfo info) {
			// One volatile read of one immutable dictionary decides the whole question, including the
			// process-less case, so no lease can be acquired "between" two reads and escape the check.
			var current=snapshot;
			var now=DateTime.UtcNow;
			if(processId.HasValue) {
				if(current.TryGetValue(processId.Value,out var lease) && Blocks(lease,now,authorizedActionIds)) { info=lease.Info; return true; }
			}
			else {
				// Breakpoint state is not owned by one process, so every active owner has to be consulted.
				foreach(var lease in current.Values) if(Blocks(lease,now,authorizedActionIds)) { info=lease.Info; return true; }
			}
			info=null!; return false;
		}

		/// <summary>
		/// A lease blocks an unauthorized caller for exactly as long as it owns the process - deadline
		/// <em>and</em> bounded cleanup window - which is the same horizon <see cref="Acquire"/> refuses a
		/// competing lease over. Expiry stops the lease granting new work; it does not stop it excluding
		/// other callers, because the cleanup window is when the owner is releasing its breakpoints, rolling
		/// back and resuming, and an external continue or detach landing there is precisely the concurrency
		/// this coordinator exists to prevent. Only the owner passes, by thread authorization or by a capture
		/// naming this lease.
		/// </summary>
		static bool Blocks(ActionLease lease,DateTime now,IReadOnlyList<string>? authorizedActionIds) {
			if(lease.OwnershipEndsUtc<=now) return false;
			if(ReferenceEquals(currentAuthorization,lease.Authorization)) return false;
			return !Names(authorizedActionIds,lease.Info.ActionId);
		}

		static bool Names(IReadOnlyList<string>? actionIds,string actionId) {
			if(actionIds is null) return false;
			for(int i=0;i<actionIds.Count;i++) if(string.Equals(actionIds[i],actionId,StringComparison.Ordinal)) return true;
			return false;
		}

		/// <summary>
		/// Captures, for the calling thread, the identity of every lease it is currently authorized under -
		/// null for a caller that owns nothing, which is every external and UI caller.
		/// </summary>
		/// <remarks>
		/// Read straight from the live snapshot and the calling thread's authorization, in that order and
		/// with no intermediate state of its own. There is therefore no mirror to prime and no
		/// snapshot-then-subscribe window: a lease this thread is authorized under was necessarily published
		/// into <c>snapshot</c> by <see cref="Acquire"/> before <see cref="Enter"/> could install its
		/// authorization on this thread, so if this thread is authorized, the lease is in the snapshot this
		/// call reads. A lease acquired by <em>another</em> thread after this read is not one this thread
		/// owns, and the authoritative <see cref="TryGetBlock(int?,string,IReadOnlyList{string},out ActionLeaseInfo)"/>
		/// on the mutating thread reads the snapshot again and refuses it.
		/// </remarks>
		public string[]? CaptureAuthorizedActionIds() {
			var authorization=currentAuthorization;
			if(authorization is null) return null;
			var current=snapshot;
			List<string>? owned=null;
			foreach(var lease in current.Values) {
				if(ReferenceEquals(authorization,lease.Authorization)) {
					owned??=new List<string>();
					owned.Add(lease.Info.ActionId);
				}
			}
			return owned?.ToArray();
		}

		/// <summary>
		/// Interrupts an active owner after an engine transition has already bypassed the guard. The caller
		/// must distinguish owner-expected transitions from external ones. This never changes debugger state.
		/// </summary>
		public bool ReportExternalDebuggerAction(int processId,string operation) {
			if(string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Operation is required.",nameof(operation));
			var current=snapshot;
			if(!current.TryGetValue(processId,out var lease) || !lease.Interrupt(operation)) return false;
			ExternalDebuggerAction?.Invoke(lease.Info,operation);
			return true;
		}

		internal IDisposable Enter(ActionLease lease) {
			lock(sync) {
				if(!snapshot.TryGetValue(lease.Info.ProcessId,out var active) || !ReferenceEquals(active,lease)) throw new ObjectDisposedException(nameof(ActionLease));
				if(lease.OwnershipEndsUtc<=DateTime.UtcNow) throw new ObjectDisposedException(nameof(ActionLease),"The action lease's bounded cleanup window elapsed before this mutation was authorized.");
			}
			var previous=currentAuthorization;
			currentAuthorization=lease.Authorization;
			return new Scope(this,previous);
		}

		/// <summary>
		/// Ends the lease's right to grant <em>new</em> work without ending its owner's authorization. The
		/// owner keeps it for a bounded cleanup window and calls <see cref="Release"/> when cleanup finishes.
		/// Releasing here instead is what made the owner's own final resume throw
		/// <see cref="ObjectDisposedException"/> at exactly the moment it had to resume the target.
		/// </summary>
		internal void Expire(ActionLease lease) {
			lock(sync) {
				if(!snapshot.TryGetValue(lease.Info.ProcessId,out var active) || !ReferenceEquals(active,lease)) return;
			}
			if(!lease.BeginCleanupWindow(()=>Release(lease))) return;
			LeaseChanged?.Invoke(lease.Info,false);
		}

		internal void Release(ActionLease lease) {
			var removed=false;
			lock(sync) {
				if(snapshot.TryGetValue(lease.Info.ProcessId,out var active) && ReferenceEquals(active,lease)) {
					var next=new Dictionary<int,ActionLease>(snapshot); next.Remove(lease.Info.ProcessId); snapshot=next; removed=true;
				}
			}
			var announce=lease.MarkInactive();
			lease.ReleaseResources();
			if(removed && announce) LeaseChanged?.Invoke(lease.Info,false);
		}

		void ThrowIfDisposed() { if(disposed) throw new ObjectDisposedException(nameof(ActionLeaseCoordinator)); }

		public void Dispose() {
			ActionLease[] active;
			lock(sync) { if(disposed) return; disposed=true; active=new List<ActionLease>(snapshot.Values).ToArray(); snapshot=new Dictionary<int,ActionLease>(); }
			foreach(var lease in active) lease.ReleaseResources();
		}

		sealed class Scope : IDisposable {
			ActionLeaseCoordinator? owner; readonly object? previous; readonly int threadId;
			public Scope(ActionLeaseCoordinator owner,object? previous) { this.owner=owner; this.previous=previous; threadId=Thread.CurrentThread.ManagedThreadId; }
			public void Dispose() {
				if(Thread.CurrentThread.ManagedThreadId!=threadId) throw new InvalidOperationException("An action authorization scope must be opened and closed around one synchronous mutation on the same thread; it must never cross an await.");
				var value=Interlocked.Exchange(ref owner,null); if(value is not null) currentAuthorization=previous;
			}
		}
	}

	public sealed class ActionLease : IDisposable {
		/// <summary>
		/// How long an expired lease's owner keeps authorization so it can finish cleanup - release its
		/// owned breakpoint, let the action roll back, resume the target and read the final state. It is a
		/// bound, not a promise: when it elapses the coordinator releases the lease itself, so a crashed or
		/// wedged owner cannot hold the process, and the owner's next mutation fails loudly and is reported
		/// as an ambiguous cleanup rather than being silently skipped.
		/// </summary>
		public static readonly TimeSpan CleanupWindow=TimeSpan.FromSeconds(5);

		readonly ActionLeaseCoordinator owner;
		readonly object resourceSync=new object();
		Timer? expiryTimer;
		Timer? cleanupTimer;
		CancellationTokenRegistration lifetimeRegistration;
		readonly CancellationTokenSource externalActionCancellation=new CancellationTokenSource();
		readonly CancellationToken externalActionToken;
		string? externalActionOperation;
		bool resourcesReleased;
		bool expired;
		bool inactiveAnnounced;
		DateTime cleanupEndsUtc;
		internal object Authorization { get; }
		public ActionLeaseInfo Info { get; }
		public CancellationToken ExternalActionCancellation => externalActionToken;
		public string? ExternalActionOperation => Volatile.Read(ref externalActionOperation);
		/// <summary>True once the deadline passed or the owner's lifetime ended: no new work is granted.</summary>
		public bool IsExpired { get { lock(resourceSync) return expired; } }
		/// <summary>The instant the owner's authorization ends, cleanup window included.</summary>
		public DateTime OwnershipEndsUtc { get { lock(resourceSync) return expired ? cleanupEndsUtc : Info.DeadlineUtc+CleanupWindow; } }

		internal ActionLease(ActionLeaseCoordinator owner,ActionLeaseInfo info,object authorization) { this.owner=owner; Info=info; Authorization=authorization; externalActionToken=externalActionCancellation.Token; }
		internal void Initialize(CancellationToken ownerLifetime) {
			var due=Info.DeadlineUtc-DateTime.UtcNow;
			var timer=new Timer(_=>owner.Expire(this),null,due>TimeSpan.Zero?due:TimeSpan.Zero,Timeout.InfiniteTimeSpan);
			var registration=ownerLifetime.CanBeCanceled ? ownerLifetime.Register(()=>owner.Expire(this)) : default;
			lock(resourceSync) {
				if(resourcesReleased) { timer.Dispose(); registration.Dispose(); }
				else { expiryTimer=timer; lifetimeRegistration=registration; }
			}
		}
		/// <summary>Executes exactly one synchronous guarded debugger mutation as this lease's owner.</summary>
		/// <remarks>
		/// Open authorization synchronously immediately around one guarded call; never hold it across an await.
		/// Owned-breakpoint operations do not belong in this scope: T07 calls DnDebugger directly and bypasses
		/// DbgCodeBreakpointsServiceImpl, so its asynchronous owner work is not on a guarded path.
		/// </remarks>
		public void ExecuteMutation(Action mutation) {
			if(mutation is null) throw new ArgumentNullException(nameof(mutation));
			RejectAsyncDelegate(mutation);
			using(owner.Enter(this)) mutation();
		}
		/// <summary>Executes exactly one synchronous guarded debugger mutation as this lease's owner.</summary>
		/// <remarks>
		/// Open authorization synchronously immediately around one guarded call; never hold it across an await.
		/// Owned-breakpoint operations do not belong in this scope: T07 calls DnDebugger directly and bypasses
		/// DbgCodeBreakpointsServiceImpl, so its asynchronous owner work is not on a guarded path.
		/// </remarks>
		public T ExecuteMutation<T>(Func<T> mutation) {
			if(mutation is null) throw new ArgumentNullException(nameof(mutation));
			RejectAsyncDelegate(mutation);
			RejectTaskLikeType(typeof(T),nameof(mutation));
			using(owner.Enter(this)) {
				var result=mutation();
				if(result is Task || result is ValueTask || result?.GetType().IsGenericType==true && result.GetType().GetGenericTypeDefinition()==typeof(ValueTask<>))
					throw new ArgumentException("An action authorization cannot wrap a Task or ValueTask. Await outside the scope, then authorize one synchronous debugger mutation.",nameof(mutation));
				return result;
			}
		}
		static void RejectTaskLikeType(Type type,string parameterName) {
			if(typeof(Task).IsAssignableFrom(type) || type==typeof(ValueTask) || type.IsGenericType && type.GetGenericTypeDefinition()==typeof(ValueTask<>))
				throw new ArgumentException("An action authorization cannot wrap a Task or ValueTask. Await outside the scope, then authorize one synchronous debugger mutation.",parameterName);
		}
		static void RejectAsyncDelegate(Delegate mutation) {
			if(mutation.Method.GetCustomAttributes(typeof(AsyncStateMachineAttribute),false).Length!=0)
				throw new ArgumentException("An action authorization delegate must be synchronous and must never cross an await.",nameof(mutation));
		}
		public void Dispose() => owner.Release(this);
		internal bool Interrupt(string operation) {
			lock(resourceSync) {
				if(resourcesReleased || externalActionOperation is not null) return false;
				externalActionOperation=operation;
				externalActionCancellation.Cancel();
				return true;
			}
		}
		/// <summary>Starts the bounded cleanup window. Returns false if the window is already open or the
		/// lease is gone, so the deadline timer and an owner disconnect arriving together open one window.</summary>
		internal bool BeginCleanupWindow(Action release) {
			Timer timer;
			lock(resourceSync) {
				if(resourcesReleased || expired) return false;
				expired=true;
				cleanupEndsUtc=DateTime.UtcNow+CleanupWindow;
				timer=new Timer(_=>release(),null,CleanupWindow,Timeout.InfiniteTimeSpan);
				cleanupTimer=timer;
			}
			return MarkInactive();
		}
		/// <summary>Reports whether this call owns the one "no longer active" notification for this lease.</summary>
		internal bool MarkInactive() { lock(resourceSync) { if(inactiveAnnounced) return false; inactiveAnnounced=true; return true; } }
		internal void ReleaseResources() {
			lock(resourceSync) {
				if(resourcesReleased) return; resourcesReleased=true;
				expiryTimer?.Dispose(); expiryTimer=null; cleanupTimer?.Dispose(); cleanupTimer=null; lifetimeRegistration.Dispose(); externalActionCancellation.Dispose();
			}
		}
	}
}
