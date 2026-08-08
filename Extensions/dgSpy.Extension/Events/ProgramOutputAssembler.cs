using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace dgSpy.Extension {
	// Which of a target's streams a chunk of console output came from, and from where.
	readonly struct ProgramOutputOrigin : IEquatable<ProgramOutputOrigin> {
		public readonly string Category; public readonly int? ProcessId; public readonly string? RuntimeId;
		public ProgramOutputOrigin(string category,int? processId,string? runtimeId) { Category=category; ProcessId=processId; RuntimeId=runtimeId; }
		public bool Equals(ProgramOutputOrigin other) => Category==other.Category && ProcessId==other.ProcessId && RuntimeId==other.RuntimeId;
		public override bool Equals(object? obj) => obj is ProgramOutputOrigin other && Equals(other);
		public override int GetHashCode() { unchecked { return (Category?.GetHashCode() ?? 0)*397 ^ (ProcessId ?? 0)*31 ^ (RuntimeId?.GetHashCode() ?? 0); } }
	}

	// The engines deliver a debuggee's console streams as raw pipe reads, not as lines: one read can carry
	// three lines, or half of one. Callers match on lines ("wait until it prints VERDICT"), so splitting a
	// line is worse than delaying it. This reassembles lines per stream, and a quiet-period timer flushes
	// whatever is still pending, so a program that writes a prompt without a newline still becomes visible
	// instead of being held back until its next write.
	sealed class ProgramOutputAssembler : IDisposable {
		readonly Action<ProgramOutputOrigin,string> emit; readonly TimeSpan quiet; readonly int maxPending;
		readonly Dictionary<ProgramOutputOrigin,StringBuilder> pending=new Dictionary<ProgramOutputOrigin,StringBuilder>();
		readonly object sync=new object(); Timer? flushTimer; bool disposed;
		// Records are ordered when they leave the buffer, then emitted in exactly that order.
		//
		// Taking text out of the buffer under `sync` and emitting it after the lock is released loses
		// that order: a thread descheduled between the two lets a later Append or the quiet-period timer
		// emit newer text first, and since `emit` is what assigns output_id, the newer fragment ends up
		// with the lower id. wait_for_output then reports a target's console output out of order, which
		// is indistinguishable from the target having printed it that way. Both Append and FlushCore had
		// this shape, so fixing one alone would still leave Append racing Append.
		//
		// `emit` is a caller-supplied callback that reaches the RPC host, so it must not run under
		// `sync`. Instead the queue fixes the order while `sync` is held, and `emitGate` serializes the
		// draining: FIFO plus one drainer at a time means emission order is enqueue order. A drainer
		// takes emitGate then sync; an enqueuer releases sync before taking emitGate, so the two never
		// nest in conflicting directions. Blocking on emitGate rather than handing off also keeps Flush
		// synchronous -- it returns only once its own records are out.
		readonly Queue<KeyValuePair<ProgramOutputOrigin,string>> outbox=new Queue<KeyValuePair<ProgramOutputOrigin,string>>();
		readonly object emitGate=new object();

		public ProgramOutputAssembler(Action<ProgramOutputOrigin,string> emit,TimeSpan? quietPeriod=null,int maxPendingChars=4096) {
			this.emit=emit; quiet=quietPeriod ?? TimeSpan.FromMilliseconds(250); maxPending=maxPendingChars;
		}

		public void Append(ProgramOutputOrigin origin,string? text) {
			if (string.IsNullOrEmpty(text)) return;
			var complete=new List<string>();
			lock(sync) {
				if (disposed) return;
				if (!pending.TryGetValue(origin,out var buffer)) pending[origin]=buffer=new StringBuilder();
				buffer.Append(text);
				TakeLines(buffer,complete);
				// A newline-less flood must not grow without bound; emit it as its own record instead.
				if (buffer.Length>=maxPending) { complete.Add(buffer.ToString()); buffer.Clear(); }
				foreach (var line in complete) outbox.Enqueue(new KeyValuePair<ProgramOutputOrigin,string>(origin,line));
				flushTimer ??= new Timer(_=>Flush(),null,Timeout.Infinite,Timeout.Infinite);
				flushTimer.Change(quiet,Timeout.InfiniteTimeSpan);
			}
			Drain();
		}

		// Emit everything still buffered, eg. a final line written without a trailing newline before the
		// target exited. Idempotent.
		public void Flush() {
			FlushCore(_=>true);
		}

		// Flush only one process when it exits. Other processes in the same session can still be writing
		// partial lines, and publishing those fragments here would split their eventual line in two.
		public void Flush(int processId) {
			FlushCore(origin=>origin.ProcessId==processId);
		}

		void FlushCore(Func<ProgramOutputOrigin,bool> include) {
			lock(sync) {
				if (disposed) return;
				foreach (var entry in pending) {
					if (!include(entry.Key)) continue;
					if (entry.Value.Length==0) continue;
					outbox.Enqueue(new KeyValuePair<ProgramOutputOrigin,string>(entry.Key,entry.Value.ToString()));
					entry.Value.Clear();
				}
			}
			Drain();
		}

		/// <summary>Emits queued records in enqueue order. One drainer at a time, so a caller that finds
		/// the gate taken blocks until the records ahead of its own are out, and then drains its own.</summary>
		void Drain() {
			lock(emitGate) {
				while(true) {
					KeyValuePair<ProgramOutputOrigin,string> record;
					lock(sync) { if (outbox.Count==0) return; record=outbox.Dequeue(); }
					emit(record.Key,record.Value);
				}
			}
		}

		// Drop partial lines left over from a previous session rather than prefixing them onto the next one.
		public void Reset() { lock(sync) { pending.Clear(); flushTimer?.Change(Timeout.InfiniteTimeSpan,Timeout.InfiniteTimeSpan); } }

		static void TakeLines(StringBuilder buffer,List<string> lines) {
			var start=0;
			for (var i=0;i<buffer.Length;i++) {
				if (buffer[i]!='\n') continue;
				var end=i>start && buffer[i-1]=='\r' ? i-1 : i;
				lines.Add(buffer.ToString(start,end-start));
				start=i+1;
			}
			if (start!=0) buffer.Remove(0,start);
		}

		public void Dispose() {
			Timer? timer;
			lock(sync) { if (disposed) return; disposed=true; timer=flushTimer; flushTimer=null; }
			timer?.Dispose();
		}
	}
}
