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
					if(existing.Info.DeadlineUtc>DateTime.UtcNow) throw new ActionLeaseConflictException(existing.Info,"acquire_action_lease");
					expired=existing;
				}
				lease=new ActionLease(this,new ActionLeaseInfo(processId,actionName,actionId,utcDeadline,statusOperation,cancelOperation),new object());
				var next=new Dictionary<int,ActionLease>(snapshot) { [processId]=lease };
				snapshot=next;
			}
			if(expired is not null) { expired.ReleaseResources(); LeaseChanged?.Invoke(expired.Info,false); }
			LeaseChanged?.Invoke(lease.Info,true);
			lease.Initialize(ownerLifetime);
			return lease;
		}

		public bool TryGetBlock(int? processId,string operation,out ActionLeaseInfo info) {
			var current=snapshot;
			var now=DateTime.UtcNow;
			if(processId.HasValue) {
				if(current.TryGetValue(processId.Value,out var lease) && lease.Info.DeadlineUtc>now && !ReferenceEquals(currentAuthorization,lease.Authorization)) { info=lease.Info; return true; }
			}
			else {
				foreach(var lease in current.Values) if(lease.Info.DeadlineUtc>now && !ReferenceEquals(currentAuthorization,lease.Authorization)) { info=lease.Info; return true; }
			}
			info=null!; return false;
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
			}
			var previous=currentAuthorization;
			currentAuthorization=lease.Authorization;
			return new Scope(this,previous);
		}

		internal void Release(ActionLease lease) {
			var removed=false;
			lock(sync) {
				if(snapshot.TryGetValue(lease.Info.ProcessId,out var active) && ReferenceEquals(active,lease)) {
					var next=new Dictionary<int,ActionLease>(snapshot); next.Remove(lease.Info.ProcessId); snapshot=next; removed=true;
				}
			}
			lease.ReleaseResources();
			if(removed) LeaseChanged?.Invoke(lease.Info,false);
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
		readonly ActionLeaseCoordinator owner;
		readonly object resourceSync=new object();
		Timer? expiryTimer;
		CancellationTokenRegistration lifetimeRegistration;
		readonly CancellationTokenSource externalActionCancellation=new CancellationTokenSource();
		readonly CancellationToken externalActionToken;
		string? externalActionOperation;
		bool resourcesReleased;
		internal object Authorization { get; }
		public ActionLeaseInfo Info { get; }
		public CancellationToken ExternalActionCancellation => externalActionToken;
		public string? ExternalActionOperation => Volatile.Read(ref externalActionOperation);

		internal ActionLease(ActionLeaseCoordinator owner,ActionLeaseInfo info,object authorization) { this.owner=owner; Info=info; Authorization=authorization; externalActionToken=externalActionCancellation.Token; }
		internal void Initialize(CancellationToken ownerLifetime) {
			var due=Info.DeadlineUtc-DateTime.UtcNow;
			var timer=new Timer(_=>owner.Release(this),null,due>TimeSpan.Zero?due:TimeSpan.Zero,Timeout.InfiniteTimeSpan);
			var registration=ownerLifetime.CanBeCanceled ? ownerLifetime.Register(()=>owner.Release(this)) : default;
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
		internal void ReleaseResources() {
			lock(resourceSync) {
				if(resourcesReleased) return; resourcesReleased=true;
				expiryTimer?.Dispose(); expiryTimer=null; lifetimeRegistration.Dispose(); externalActionCancellation.Dispose();
			}
		}
	}
}
