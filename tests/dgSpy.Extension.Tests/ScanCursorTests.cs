using System.Collections.Generic;
using System.Linq;
using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>The property the resume cursor exists for: paging a bounded scan covers every slot exactly
/// once, with no gap and no repeat. A gap is the failure that was measured in the field -- an agent swept
/// a plugin, hit search_text's cap, and reported the sweep complete while three keybinds sat in the part
/// it never reached.</summary>
public sealed class ScanCursorTests {
	/// <summary>Walks <paramref name="slots"/> items with a bound of <paramref name="max"/>, resuming until
	/// the walk says it is done, and returns everything it claimed in order.</summary>
	static (List<int> Claimed,int Calls) PageThrough(int slots,int max) {
		var claimed=new List<int>(); var offset=0; var calls=0;
		while (true) {
			var walk=new ScanCursor { Skip=offset,Max=max };
			calls++;
			for (var slot=0;slot<slots;slot++) { if (!walk.Claim()) { if (walk.Truncated) break; continue; } claimed.Add(slot); }
			if (!walk.Truncated) return (claimed,calls);
			offset=walk.Inspected;
			// A page that advances nothing would loop forever; the bound is at least one slot, so it
			// cannot happen, and the assertion says so rather than hanging the suite.
			Assert.True(calls<1000,"the cursor stopped advancing");
		}
	}

	[Theory]
	[InlineData(20,7)]
	[InlineData(20,1)]
	[InlineData(20,20)]
	[InlineData(20,100)]
	[InlineData(0,5)]
	public void Paging_covers_every_slot_exactly_once(int slots,int max) {
		var (claimed,_)=PageThrough(slots,max);
		Assert.Equal(Enumerable.Range(0,slots).ToList(),claimed);
	}

	[Fact]
	public void A_bound_larger_than_the_scope_reports_nothing_left_to_resume() {
		var walk=new ScanCursor { Skip=0,Max=100 };
		for (var slot=0;slot<10;slot++) walk.Claim();
		// scan_truncated:false is the only signal that a sweep is actually complete.
		Assert.False(walk.Truncated);
		Assert.Equal(10,walk.Worked);
		Assert.Equal(10,walk.Inspected);
	}

	[Fact]
	public void A_spent_bound_reports_truncation_and_a_resume_point() {
		var walk=new ScanCursor { Skip=0,Max=3 };
		for (var slot=0;slot<10;slot++) walk.Claim();
		Assert.True(walk.Truncated);
		Assert.Equal(3,walk.Worked);
		Assert.Equal(3,walk.Inspected);
	}

	[Fact]
	public void Skipped_slots_cost_no_work_but_still_advance_the_cursor() {
		// The resumed page must not re-do the first page's work, and must not lose its place either.
		var walk=new ScanCursor { Skip=5,Max=3 };
		var claimed=new List<int>();
		for (var slot=0;slot<20;slot++) { if (!walk.Claim()) { if (walk.Truncated) break; continue; } claimed.Add(slot); }
		Assert.Equal(new[]{5,6,7},claimed);
		Assert.Equal(3,walk.Worked);
		Assert.Equal(8,walk.Inspected);
	}

	[Fact]
	public void Once_truncated_the_walk_stays_shut() {
		var walk=new ScanCursor { Skip=0,Max=1 };
		Assert.True(walk.Claim());
		Assert.False(walk.Claim());
		Assert.True(walk.Truncated);
		Assert.False(walk.Claim());
		Assert.Equal(1,walk.Inspected);
	}
}
