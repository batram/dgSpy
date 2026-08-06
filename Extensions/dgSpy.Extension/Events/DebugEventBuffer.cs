using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	sealed class DebugEventBuffer {
		readonly int capacity;
		readonly List<DebugEvent> events=new List<DebugEvent>();
		readonly object sync=new object();
		TaskCompletionSource<bool> changed=NewSignal();
		long lastEventId;

		public DebugEventBuffer(int capacity=256) {
			if (capacity<1) throw new ArgumentOutOfRangeException(nameof(capacity));
			this.capacity=capacity;
		}

		public long LastEventId { get { lock(sync) return lastEventId; } }
		public long OldestEventId { get { lock(sync) return OldestEventIdCore; } }
		long OldestEventIdCore => events.Count==0 ? lastEventId+1 : events[0].EventId;

		public DebugEvent Add(DebugEvent value,long stateVersion) {
			TaskCompletionSource<bool> wake;
			lock(sync) {
				value.EventId=++lastEventId;
				value.StateVersion=stateVersion;
				value.TimestampUtc=DateTime.UtcNow;
				events.Add(value);
				if (events.Count>capacity) events.RemoveAt(0);
				wake=changed;
				changed=NewSignal();
			}
			wake.TrySetResult(true);
			return value;
		}

		public void Add(string kind,long stateVersion,bool terminal=false,int? processId=null,int? exitCode=null,string? reason=null) =>
			Add(new DebugEvent { Kind=kind,Terminal=terminal,ProcessId=processId,ExitCode=exitCode,Reason=reason },stateVersion);

		public void Reset() {
			TaskCompletionSource<bool> wake;
			lock(sync) { events.Clear(); lastEventId=0; wake=changed; changed=NewSignal(); }
			wake.TrySetResult(true);
		}

		public EventBufferSnapshot Snapshot(long eventId,IReadOnlyCollection<string>? kinds=null) {
			lock(sync) {
				var oldest=OldestEventIdCore;
				var oldestCursor=Math.Max(0,oldest-1);
				return new EventBufferSnapshot {
					Events=events.Where(e=>e.EventId>eventId && (kinds is null || kinds.Count==0 || kinds.Contains(e.Kind))).ToArray(),
					OldestEventId=oldest,OldestAvailableCursor=oldestCursor,LastEventId=lastEventId,
					Truncated=eventId<oldestCursor,
				};
			}
		}

		public DebugEvent[] FindAfter(long eventId,string kind) => Snapshot(eventId,new[]{kind}).Events;
		public DebugEvent[] FindAfter(long eventId) => Snapshot(eventId).Events;

		public DebugEvent? Latest(string kind) { lock(sync) return events.LastOrDefault(e=>e.Kind==kind); }

		public Task WaitForChangeAsync(long observedLastEventId,CancellationToken cancellationToken) {
			Task signal;
			lock(sync) {
				if (lastEventId!=observedLastEventId) return Task.CompletedTask;
				signal=changed.Task;
			}
			return cancellationToken.CanBeCanceled ? WaitWithCancellationAsync(signal,cancellationToken) : signal;
		}

		static async Task WaitWithCancellationAsync(Task signal,CancellationToken cancellationToken) {
			var cancellation=Task.Delay(Timeout.Infinite,cancellationToken);
			if (await Task.WhenAny(signal,cancellation).ConfigureAwait(false)==cancellation) cancellationToken.ThrowIfCancellationRequested();
			await signal.ConfigureAwait(false);
		}

		static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	sealed class EventBufferSnapshot {
		public DebugEvent[] Events { get; set; }=Array.Empty<DebugEvent>();
		public long OldestEventId { get; set; }
		public long OldestAvailableCursor { get; set; }
		public long LastEventId { get; set; }
		public bool Truncated { get; set; }
		public bool SatisfiesWait => Events.Length!=0 || Truncated;
	}
}
