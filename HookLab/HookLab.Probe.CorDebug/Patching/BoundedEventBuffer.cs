using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class BoundedEventBuffer : IHookEventSource {
		readonly object gate = new object();
		readonly Queue<HookEvent> events = new Queue<HookEvent>();
		readonly int eventCapacity;
		readonly long byteCapacity;
		long currentBytes;
		long dropped;

		public BoundedEventBuffer(int eventCapacity, long byteCapacity) {
			if (eventCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(eventCapacity));
			if (byteCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
			this.eventCapacity = eventCapacity; this.byteCapacity = byteCapacity;
		}

		public bool TryAppend(Func<long, HookEvent> factory) {
			if (factory == null) throw new ArgumentNullException(nameof(factory));
			lock (gate) {
				var next = factory(dropped);
				var bytes = Encoding.UTF8.GetByteCount(next.PayloadJson);
				if (events.Count >= eventCapacity || currentBytes + bytes > byteCapacity) { dropped++; return false; }
				events.Enqueue(next); currentBytes += bytes; return true;
			}
		}

		public IReadOnlyList<HookEvent> Drain(int maximumCount) {
			if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
			var result = new List<HookEvent>();
			lock (gate) while (result.Count < maximumCount && events.Count != 0) {
				var item = events.Dequeue(); currentBytes -= Encoding.UTF8.GetByteCount(item.PayloadJson); result.Add(item);
			}
			return result.AsReadOnly();
		}
		public long DroppedCount { get { lock (gate) return dropped; } }
		/// <summary>Whether a drain would return anything. The notification path re-checks this after
		/// clearing its pending flag: an append that lands between a consumer's final drain and that
		/// clear suppresses its own wake-up, so the delivering thread owns it.</summary>
		public bool HasPendingEvents { get { lock (gate) return events.Count != 0; } }
	}
}
