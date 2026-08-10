using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>One atomic action's retrievable record: its cancellation source while it runs, and its
	/// terminal outcome afterwards.</summary>
	public sealed class AtomicActionRecord : IDisposable {
		public AtomicActionRecord(string id,DateTime startedUtc) { Id=id; StartedUtc=startedUtc; }
		public string Id { get; }
		public DateTime StartedUtc { get; }
		public CancellationTokenSource Cancelled { get; }=new CancellationTokenSource();
		public volatile bool Completed;
		public AtomicActionResult? Result;
		/// <summary>Set when the run threw before it could produce a result - a rejected request, or a lease
		/// conflict. Without it the record reported <c>completed=true, result=null</c>: a reconciliation
		/// operation with nothing to reconcile and no statement of what went wrong.</summary>
		public string? ErrorCode;
		public string? ErrorMessage;
		public DateTime TerminalUtc;
		/// <summary>Requests cancellation, reporting whether there was still a run to cancel. A terminal
		/// record has already released its cancellation source, so asking again is answered, not thrown.</summary>
		public bool TryCancel() {
			if(Completed) return false;
			try { Cancelled.Cancel(); return true; }
			catch(ObjectDisposedException) { return false; }
		}
		public void Dispose() { try { Cancelled.Dispose(); } catch(ObjectDisposedException) { } }
	}

	/// <summary>
	/// Bounded storage for atomic-action records. The original dictionary retained every id, result and
	/// cancellation source for the host's lifetime, so an <c>action_id</c> was burned permanently - a retry
	/// answered <c>action_exists</c> forever - and every run leaked a <see cref="CancellationTokenSource"/>.
	/// Terminal records are kept long enough to be reconciled and no longer.
	/// </summary>
	public sealed class AtomicActionRecordStore : IDisposable {
		/// <summary>How long a terminal record stays retrievable. Reconciliation happens right after an
		/// ambiguous answer, not hours later, and the status operation is explicitly documented as usable
		/// after the session ended - so this outlives a session, not a working day.</summary>
		public static readonly TimeSpan TerminalRetention=TimeSpan.FromMinutes(30);
		/// <summary>Ceiling on retained terminal records, so a host driven hard cannot grow without bound
		/// inside the retention window. The oldest terminal records are evicted first.</summary>
		public const int MaxTerminalRecords=64;

		readonly ConcurrentDictionary<string,AtomicActionRecord> records=new ConcurrentDictionary<string,AtomicActionRecord>(StringComparer.Ordinal);
		readonly Func<DateTime> utcNow;
		public AtomicActionRecordStore(Func<DateTime>? utcNow=null) => this.utcNow=utcNow ?? (()=>DateTime.UtcNow);

		public int Count => records.Count;

		/// <summary>Adds a record for a new run, or returns null when this action id is still live or still
		/// retained. Eviction runs first, so an id whose record has aged out is reusable.</summary>
		public AtomicActionRecord? TryStart(string actionId) {
			Evict();
			var record=new AtomicActionRecord(actionId,utcNow());
			if(records.TryAdd(actionId,record)) return record;
			record.Dispose();
			return null;
		}

		public bool TryGet(string actionId,out AtomicActionRecord record) => records.TryGetValue(actionId,out record!);

		public void Complete(AtomicActionRecord record,AtomicActionResult? result,string? errorCode=null,string? errorMessage=null) {
			if(record is null) throw new ArgumentNullException(nameof(record));
			record.Result=result; record.ErrorCode=errorCode; record.ErrorMessage=errorMessage;
			record.TerminalUtc=utcNow();
			record.Completed=true;
			// The run is over, so nothing can cancel it any more. The record stays readable; only the
			// cancellation source, which is the part that actually costs a handle, is disposed here.
			record.Dispose();
			Evict();
		}

		/// <summary>Drops terminal records past the retention window, then the oldest terminal records past
		/// the ceiling. A record whose run is still in flight is never evicted at any age: cancelling and
		/// reconciling it both need it.</summary>
		public void Evict() {
			var now=utcNow();
			foreach(var pair in records.ToArray())
				if(pair.Value.Completed && now-pair.Value.TerminalUtc>TerminalRetention) Remove(pair.Key);
			var terminal=records.ToArray().Where(pair=>pair.Value.Completed).OrderBy(pair=>pair.Value.TerminalUtc).ToArray();
			for(var index=0;index<terminal.Length-MaxTerminalRecords;index++) Remove(terminal[index].Key);
		}

		void Remove(string actionId) { if(records.TryRemove(actionId,out var evicted)) evicted.Dispose(); }

		public IReadOnlyList<string> Ids => records.Keys.ToArray();

		public void Dispose() { foreach(var pair in records.ToArray()) Remove(pair.Key); }
	}
}
