using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>One atomic action's retrievable record: its cancellation source while it runs, and its
	/// terminal outcome afterwards.</summary>
	public sealed class AtomicActionRecord : IDisposable {
		public AtomicActionRecord(string id,DateTime startedUtc) : this(id,startedUtc,AtomicActionGeneration.Unscoped) { }
		public AtomicActionRecord(string id,DateTime startedUtc,AtomicActionGeneration generation) { Id=id; StartedUtc=startedUtc; Generation=generation; }
		public string Id { get; }
		public DateTime StartedUtc { get; }
		/// <summary>The session and process this record was created against. Cancel authorization compares
		/// it, so a stale <c>action_id</c> reused after the session or process was replaced answers
		/// <c>action_not_found</c> instead of cancelling whatever now holds that id.</summary>
		public AtomicActionGeneration Generation { get; }
		public CancellationTokenSource Cancelled { get; }=new CancellationTokenSource();
		public volatile bool Completed;
		/// <summary>The background run, for a host shutdown that has to reconcile every action rather than
		/// abandon it. Null for the blocking <c>run_atomic_action</c> path, which is owned by its request.</summary>
		public Task? Run;
		int phase;
		/// <summary>Monotonic: a phase never moves backwards, so a poller comparing two readings cannot see
		/// an action regress. Returns the phase now in effect, which is the later of the two when a stale
		/// report arrives out of order.</summary>
		public AtomicActionPhase AdvanceTo(AtomicActionPhase value) {
			while(true) {
				var current=Volatile.Read(ref phase);
				if((int)value<=current) return (AtomicActionPhase)current;
				if(Interlocked.CompareExchange(ref phase,(int)value,current)==current) return value;
			}
		}
		public AtomicActionPhase Phase => (AtomicActionPhase)Volatile.Read(ref phase);
		/// <summary>The <see cref="Action{T}"/>-shaped form the state machine reports through.</summary>
		public void AdvanceToPhase(AtomicActionPhase value) => AdvanceTo(value);
		/// <summary>How long a caller should wait before reading status again. Short while the run is being
		/// set up and while it is acting, longer while it is waiting for natural arrival - that is the phase
		/// that lasts, and polling it at 250 ms buys nothing. Zero once terminal: there is nothing left to
		/// wait for. This exists because a blocking wait operation would recreate the very defect the
		/// asynchronous shape removes - while a registered channel waits, a cancel cannot overtake it.</summary>
		public int RecommendedPollAfterMs => Completed ? 0 : Phase switch {
			AtomicActionPhase.running=>750,
			AtomicActionPhase.terminal=>0,
			_=>250,
		};
		public AtomicActionResult? Result;
		/// <summary>Set when the run threw before it could produce a result - a rejected request, or a lease
		/// conflict. Without it the record reported <c>completed=true, result=null</c>: a reconciliation
		/// operation with nothing to reconcile and no statement of what went wrong.</summary>
		public string? ErrorCode;
		public string? ErrorMessage;
		public DateTime TerminalUtc;
		/// <summary>Requests cancellation, reporting whether there was still a run to cancel. A terminal
		/// record has already released its cancellation source, so asking again is answered, not thrown.</summary>
		public bool TryCancel() => TryCancel(out _);
		/// <summary><paramref name="alreadyRequested"/> reports that a previous cancel had already been
		/// accepted for this record. Asking twice is still answered <c>true</c> - the request is idempotent,
		/// and a second caller learning "false" would read it as "too late", which is the one thing it does
		/// not mean.</summary>
		public bool TryCancel(out bool alreadyRequested) {
			alreadyRequested=CancelRequested;
			if(Completed) return false;
			CancelRequested=true;
			try { Cancelled.Cancel(); return true; }
			catch(ObjectDisposedException) { return false; }
		}
		/// <summary>Survives the disposal of <see cref="Cancelled"/>, which a terminal record performs, so a
		/// cancel that arrived before completion is still reportable afterwards.</summary>
		public volatile bool CancelRequested;
		public void Dispose() { try { Cancelled.Dispose(); } catch(ObjectDisposedException) { } }
	}

	/// <summary>
	/// The identity a cancel has to match, beyond the action id. Measurement removed
	/// <c>expected_execution_version</c> from <c>cancel_atomic_action</c> - it did not move during any
	/// observed action, and a value stamped onto the run's own response only arrives once the run is over,
	/// so it could never help a canceller. This is what replaces it: the session the record was created in
	/// and the process generation it captured. Controller authority is enforced a layer up, by the Gateway.
	/// </summary>
	public sealed class AtomicActionGeneration : IEquatable<AtomicActionGeneration> {
		/// <summary>For records created outside a session context - the in-process tests, and the blocking
		/// path's own bookkeeping. Matches anything, because there is nothing to match against.</summary>
		public static readonly AtomicActionGeneration Unscoped=new AtomicActionGeneration(null,0,0);
		public AtomicActionGeneration(string? sessionId,int processId,long lifecycleVersion) { SessionId=sessionId; ProcessId=processId; LifecycleVersion=lifecycleVersion; }
		public string? SessionId { get; }
		public int ProcessId { get; }
		/// <summary>The host's lifecycle counter at registration. A detach, terminate, restart or process
		/// exit moves it, so a record created before one of those is provably from an older generation.</summary>
		public long LifecycleVersion { get; }
		public bool Equals(AtomicActionGeneration? other) =>
			other is not null && String.Equals(SessionId,other.SessionId,StringComparison.Ordinal) && ProcessId==other.ProcessId && LifecycleVersion==other.LifecycleVersion;
		public override bool Equals(object? obj)=>Equals(obj as AtomicActionGeneration);
		public override int GetHashCode()=>(SessionId?.GetHashCode() ?? 0)^ProcessId^LifecycleVersion.GetHashCode();
		/// <summary>True when a cancel arriving under <paramref name="current"/> is talking about this same
		/// action. <see cref="Unscoped"/> on either side matches, so the in-process paths are unaffected.</summary>
		public bool Authorizes(AtomicActionGeneration current) =>
			ReferenceEquals(this,Unscoped) || ReferenceEquals(current,Unscoped) || Equals(current);
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
		public AtomicActionRecord? TryStart(string actionId) => TryStart(actionId,AtomicActionGeneration.Unscoped);

		public AtomicActionRecord? TryStart(string actionId,AtomicActionGeneration generation) {
			Evict();
			var record=new AtomicActionRecord(actionId,utcNow(),generation);
			if(records.TryAdd(actionId,record)) return record;
			record.Dispose();
			return null;
		}

		public bool TryGet(string actionId,out AtomicActionRecord record) => records.TryGetValue(actionId,out record!);

		public void Complete(AtomicActionRecord record,AtomicActionResult? result,string? errorCode=null,string? errorMessage=null) {
			if(record is null) throw new ArgumentNullException(nameof(record));
			record.Result=result; record.ErrorCode=errorCode; record.ErrorMessage=errorMessage;
			record.TerminalUtc=utcNow();
			// Phase before Completed, so a status read that sees completed=true can never still report a
			// non-terminal phase.
			record.AdvanceTo(AtomicActionPhase.terminal);
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

		/// <summary>Every record whose run has not finished. Host shutdown reconciles these; nothing else
		/// may enumerate them to decide policy, because the set is racy by construction.</summary>
		public IReadOnlyList<AtomicActionRecord> NonTerminal => records.Values.Where(value=>!value.Completed).ToArray();

		public void Dispose() { foreach(var pair in records.ToArray()) Remove(pair.Key); }
	}
}
