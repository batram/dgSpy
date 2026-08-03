using System;
using System.Collections.Generic;
using System.Linq;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	sealed class DebugEventBuffer {
		readonly int capacity;
		readonly List<DebugEvent> events=new List<DebugEvent>();
		long lastEventId;

		public DebugEventBuffer(int capacity=256) {
			if (capacity<1) throw new ArgumentOutOfRangeException(nameof(capacity));
			this.capacity=capacity;
		}

		public long LastEventId => lastEventId;
		public long OldestEventId => events.Count==0 ? lastEventId+1 : events[0].EventId;

		public void Add(string kind,long stateVersion,bool terminal=false,int? processId=null,int? exitCode=null,string? reason=null) {
			events.Add(new DebugEvent { EventId=++lastEventId,Kind=kind,StateVersion=stateVersion,Terminal=terminal,ProcessId=processId,ExitCode=exitCode,Reason=reason });
			if (events.Count>capacity) events.RemoveAt(0);
		}

		public void Reset() { events.Clear(); lastEventId=0; }

		public DebugEvent[] FindAfter(long eventId,string kind) =>
			events.Where(e=>e.EventId>eventId && e.Kind==kind).ToArray();

		public DebugEvent[] FindAfter(long eventId) => events.Where(e=>e.EventId>eventId).ToArray();
	}
}
