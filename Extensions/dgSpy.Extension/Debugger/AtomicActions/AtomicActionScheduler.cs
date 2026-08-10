using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>
	/// Host-owned execution of an atomic action, and the reason <c>start_atomic_action</c> can return at all.
	///
	/// <para><b>The boundary is registration, not arming.</b> <see cref="Start"/> reserves the action id,
	/// creates the retained record and schedules the run - it does not wait for the lease, the breakpoint, or
	/// the dispatcher. Returning after "armed" was the first draft of this and was wrong: a host connection
	/// reads one request and awaits its full dispatch before reading the next, so a start that waits for
	/// arming still blocks the only channel the caller has, and a hung bind is still uncancellable. That
	/// moves the uncancellable window from "the whole action" to "arming" instead of removing it.</para>
	///
	/// <para>After acceptance the run belongs to the host, not to the request that started it. The start
	/// request then ends <em>normally</em>, which is why <c>disconnect_policy</c> has no coherent meaning
	/// here and stays on the blocking <c>run_atomic_action</c>: an asynchronous action cannot read the
	/// successful completion of its own start request as a client disconnect, and controller loss must not
	/// silently cancel a target mutation under a policy nobody designed or tested. Ending an accepted action
	/// early takes an explicit <c>cancel_atomic_action</c>.</para>
	/// </summary>
	public sealed class AtomicActionScheduler {
		readonly AtomicActionRecordStore store;
		public AtomicActionScheduler(AtomicActionRecordStore store) => this.store=store ?? throw new ArgumentNullException(nameof(store));

		/// <summary>How a terminal record is synthesized when the run never produced one of its own - a
		/// cancel that arrived while the action was still queued, or a host shutdown. The caller supplies it
		/// because only the caller knows the requested slot, the effective deadline and the audit id; a
		/// fabricated result missing those would be a reconciliation record with nothing to reconcile.</summary>
		public delegate AtomicActionResult TerminalFactory(InterruptionReason reason,string message);

		/// <summary>
		/// Validates nothing - that is the caller's synchronous job, and a rejected request must create no
		/// record - reserves the id, and schedules the run. Throws <c>action_exists</c> when the id is still
		/// queued, running, cleaning up, or retained for reconciliation.
		/// </summary>
		public AtomicActionAcceptance Start(string actionId,AtomicActionGeneration generation,Func<AtomicActionRecord,Task<AtomicActionResult>> run,TerminalFactory terminal) {
			if(String.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("Action id is required.",nameof(actionId));
			if(run is null) throw new ArgumentNullException(nameof(run));
			if(terminal is null) throw new ArgumentNullException(nameof(terminal));
			var record=store.TryStart(actionId,generation ?? AtomicActionGeneration.Unscoped)
				?? throw new RpcException("action_exists","An atomic action with this action_id already exists or is still retained for reconciliation.");
			record.AdvanceTo(AtomicActionPhase.queued);
			// Assigned before the task can observe it is missing: Run is what host shutdown waits on, and a
			// shutdown racing a start would otherwise find a non-terminal record with nothing to await.
			record.Run=Task.Run(()=>ExecuteAsync(record,run,terminal));
			return new AtomicActionAcceptance(actionId,record.Phase,record.RecommendedPollAfterMs);
		}

		/// <summary>
		/// The detached run. Every exit is a completed record: a result, or an error code, or a synthesized
		/// cancellation. Nothing escapes - an exception leaving this task would be unobserved, which loses
		/// the only account of what happened to a mutation that may already have applied, and takes the
		/// action_id with it.
		/// </summary>
		async Task ExecuteAsync(AtomicActionRecord record,Func<AtomicActionRecord,Task<AtomicActionResult>> run,TerminalFactory terminal) {
			try {
				// A cancel can land between acceptance and the first line of the run. Checking here is what
				// makes `queued` cancellable at all: the state machine's own linked token only starts
				// covering the run once the run has begun.
				if(record.Cancelled.IsCancellationRequested) {
					store.Complete(record,terminal(InterruptionReason.cancelled,"Cancelled after acceptance and before arming began; nothing was installed in the target."));
					return;
				}
				var result=await run(record).ConfigureAwait(false);
				store.Complete(record,result);
			}
			catch(OperationCanceledException) when(record.Cancelled.IsCancellationRequested) {
				store.Complete(record,terminal(InterruptionReason.cancelled,"Cancelled while the action was being armed."));
			}
			catch(RpcException ex) { store.Complete(record,null,ex.Code,ex.Message); }
			catch(Exception ex) { store.Complete(record,null,"internal_error",ex.GetType().Name+": "+ex.Message); }
		}

		/// <summary>
		/// Terminates or reconciles every background action, for host shutdown. Cancels each non-terminal
		/// record, waits out one shared bound for their own cleanup to report, and then forces a terminal
		/// record onto anything still running - a record that stayed non-terminal past shutdown is a
		/// mutation nobody can ever reconcile. Returns how many records it had to force.
		/// </summary>
		public async Task<int> ShutdownAsync(TimeSpan bound,TerminalFactory terminal) {
			if(terminal is null) throw new ArgumentNullException(nameof(terminal));
			var pending=store.NonTerminal;
			foreach(var record in pending) record.TryCancel();
			var runs=pending.Select(value=>value.Run).Where(value=>value is not null).Select(value=>value!).ToArray();
			if(runs.Length>0) {
				try { await Task.WhenAny(Task.WhenAll(runs),Task.Delay(bound)).ConfigureAwait(false); }
				catch { }
			}
			var forced=0;
			foreach(var record in pending) {
				if(record.Completed) continue;
				store.Complete(record,terminal(InterruptionReason.ui_shutdown,"The host shut down while this action was in progress; what it had reached is unknown."));
				forced++;
			}
			return forced;
		}
	}

	/// <summary>What <c>start_atomic_action</c> answers. Deliberately not a result: the action has not run.</summary>
	public sealed class AtomicActionAcceptance {
		public AtomicActionAcceptance(string actionId,AtomicActionPhase phase,int recommendedPollAfterMs) { ActionId=actionId; Phase=phase; RecommendedPollAfterMs=recommendedPollAfterMs; }
		// ProtocolJson applies no naming policy, so every wire name is spelled out here. Without the
		// attributes this shape would have gone out PascalCase while every neighbouring response is
		// snake_case.
		[JsonPropertyName("schema_version")] public int SchemaVersion=>1;
		[JsonPropertyName("action_id")] public string ActionId { get; }
		[JsonPropertyName("accepted")] public bool Accepted=>true;
		[JsonPropertyName("phase"),JsonConverter(typeof(JsonStringEnumConverter))] public AtomicActionPhase Phase { get; }
		[JsonPropertyName("recommended_poll_after_ms")] public int RecommendedPollAfterMs { get; }
		[JsonPropertyName("status_operation")] public string StatusOperation=>AtomicActionStateMachine.StatusOperation;
		[JsonPropertyName("cancel_operation")] public string CancelOperation=>AtomicActionStateMachine.CancelOperation;
	}
}
