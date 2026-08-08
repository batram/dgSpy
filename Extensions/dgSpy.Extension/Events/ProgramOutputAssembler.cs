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
				flushTimer ??= new Timer(_=>Flush(),null,Timeout.Infinite,Timeout.Infinite);
				flushTimer.Change(quiet,Timeout.InfiniteTimeSpan);
			}
			foreach (var line in complete) emit(origin,line);
		}

		// Emit everything still buffered, eg. a final line written without a trailing newline before the
		// target exited. Idempotent.
		public void Flush() {
			var flushed=new List<KeyValuePair<ProgramOutputOrigin,string>>();
			lock(sync) {
				if (disposed) return;
				foreach (var entry in pending) {
					if (entry.Value.Length==0) continue;
					flushed.Add(new KeyValuePair<ProgramOutputOrigin,string>(entry.Key,entry.Value.ToString()));
					entry.Value.Clear();
				}
			}
			foreach (var entry in flushed) emit(entry.Key,entry.Value);
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
