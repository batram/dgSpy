namespace dgSpy.Extension {
	/// <summary>Rejects an event cursor the stream has not reached, instead of waiting on it.
	///
	/// Every legitimate cursor comes from a response — a previous <c>last_event_id</c>, or
	/// set_il_breakpoint's <c>cursor_event_id</c> — so it can never exceed the last event id. One that
	/// does is an off-by-one, a cursor carried over from another session, or one from before a buffer
	/// reset. Left alone all three wait out the timeout and return <c>timed_out: true</c> with no events,
	/// which is byte-identical to "the target did not stop": a poll loop keeps asking forever and the
	/// caller concludes the breakpoint never binds. Same false-negative shape, and the same reasoning, as
	/// rejecting an unrecognized event kind rather than filtering on it.
	///
	/// Centralized here rather than inlined so the boundary — <c>after == last</c> is the ordinary
	/// caught-up case and must be allowed, <c>after == last + 1</c> is not — has a deterministic
	/// executable check, mirroring <see cref="FrameSnapshotGuard"/>.</summary>
	internal static class EventCursorGuard {
		internal static bool IsAheadOfStream(long after,long lastEventId) => after>lastEventId;

		internal static void EnsureReachable(long after,long lastEventId) {
			if (!IsAheadOfStream(after,lastEventId)) return;
			throw new RpcException("cursor_ahead_of_stream",
				$"after_event_id {after} is ahead of this session's last event id {lastEventId}, so no event can ever satisfy it. "+
				"Cursors come from a response: pass a previous response's last_event_id, or set_il_breakpoint's cursor_event_id. "+
				$"Resume from {lastEventId}, or from 0 to replay the retained session. A cursor from another session, or from before a restart, is never valid here.");
		}
	}
}
