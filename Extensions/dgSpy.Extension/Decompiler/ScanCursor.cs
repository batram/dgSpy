namespace dgSpy.Extension {
	/// <summary>A bounded, resumable walk over symbol slots.
	///
	/// Every scanning tool here has to stop before it has looked at everything: a Unity Assembly-CSharp
	/// carries 20311 methods and the framework assemblies around it hundreds of thousands of symbols, so
	/// an unbounded walk is a wedged debugger thread. A bound on its own is not enough, though. A tool
	/// that stops at slot N and offers no way to ask for slot N+1 does not return a partial answer, it
	/// returns an unreachable region: <c>analyze_symbol</c> could not find the callers of a method in a
	/// 7227-method module at any <c>max_methods</c> setting, and a caller that swept a plugin for hotkey
	/// definitions hit <c>search_text</c>'s cap, was told the scan was truncated, and reported the sweep
	/// complete anyway.
	///
	/// So the bound comes with a cursor. <see cref="Inspected"/> counts slots in traversal order rather
	/// than results, which is what lets the count mean "resume exactly here" instead of "roughly there".
	/// That only holds while the traversal order is deterministic and the caller repeats the same
	/// filtering arguments, so every tool using this walks its modules in an explicitly sorted order.</summary>
	class ScanCursor {
		/// <summary>Slots reached so far, counted from the very start of the traversal rather than from
		/// the resume point. This is the value a caller passes back as its next <c>scan_offset</c>.</summary>
		public int Inspected;
		/// <summary>Slots to pass over before doing any work: the caller's <c>scan_offset</c>.</summary>
		public int Skip;
		/// <summary>Work bound, in slots, for this call alone.</summary>
		public int Max;
		/// <summary>True when <see cref="Max"/> stopped the walk before the scope was exhausted. False
		/// means there is nothing left to resume.</summary>
		public bool Truncated;
		/// <summary>Slots actually worked in this call: what was inspected past the resume cursor.</summary>
		public int Worked;

		/// <summary>Claims one slot. False means either this slot precedes the resume cursor or the work
		/// bound is spent; the caller must not evaluate the symbol in that case. Check
		/// <see cref="Truncated"/> to tell the two apart -- a spent bound must end the walk, a skipped
		/// slot must not.</summary>
		public bool Claim() {
			if (Truncated) return false;
			if (Inspected>=Skip+Max) { Truncated=true; return false; }
			Inspected++;
			if (Inspected<=Skip) return false;
			Worked++;
			return true;
		}
	}
}
