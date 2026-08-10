using dgSpy.Extension.Debugger.AtomicActions;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>
/// The record dictionary retained every action id, result and cancellation source for the host's
/// lifetime: an action_id was burned permanently, and a run that threw left <c>completed=true</c> with
/// nothing to reconcile.
/// </summary>
public sealed class AtomicActionRecordStoreTests {
	sealed class Clock { public DateTime Utc=new DateTime(2026,8,10,12,0,0,DateTimeKind.Utc); public DateTime Now()=>Utc; }

	[Fact] public void A_live_action_id_is_refused_and_a_terminal_one_is_reusable_after_retention() {
		var clock=new Clock();
		using var store=new AtomicActionRecordStore(clock.Now);
		var first=store.TryStart("a1");
		Assert.NotNull(first);
		Assert.Null(store.TryStart("a1"));
		store.Complete(first!,null,"boom","it failed");
		Assert.Null(store.TryStart("a1"));
		clock.Utc=clock.Utc.Add(AtomicActionRecordStore.TerminalRetention).AddSeconds(1);
		Assert.NotNull(store.TryStart("a1"));
	}

	[Fact] public void An_in_flight_record_is_never_evicted_by_age() {
		var clock=new Clock();
		using var store=new AtomicActionRecordStore(clock.Now);
		var live=store.TryStart("a1")!;
		clock.Utc=clock.Utc.AddDays(1);
		store.Evict();
		Assert.True(store.TryGet("a1",out var found));
		Assert.Same(live,found);
	}

	[Fact] public void Terminal_records_are_bounded_and_the_oldest_go_first() {
		var clock=new Clock();
		using var store=new AtomicActionRecordStore(clock.Now);
		for(var index=0;index<AtomicActionRecordStore.MaxTerminalRecords+5;index++) {
			var record=store.TryStart("a"+index)!;
			store.Complete(record,null,"code","message");
			clock.Utc=clock.Utc.AddSeconds(1);
		}
		Assert.Equal(AtomicActionRecordStore.MaxTerminalRecords,store.Count);
		Assert.False(store.TryGet("a0",out _));
		Assert.True(store.TryGet("a"+(AtomicActionRecordStore.MaxTerminalRecords+4),out _));
	}

	[Fact] public void A_run_that_threw_records_why_rather_than_a_bare_completion() {
		using var store=new AtomicActionRecordStore();
		var record=store.TryStart("a1")!;
		store.Complete(record,null,"action_lease_conflict","another action owns process 42");
		Assert.True(record.Completed);
		Assert.Null(record.Result);
		Assert.Equal("action_lease_conflict",record.ErrorCode);
		Assert.Equal("another action owns process 42",record.ErrorMessage);
	}

	[Fact] public void Cancellation_is_answered_after_the_run_is_over_rather_than_throwing() {
		using var store=new AtomicActionRecordStore();
		var record=store.TryStart("a1")!;
		Assert.True(record.TryCancel());
		Assert.True(record.Cancelled.IsCancellationRequested);
		store.Complete(record,null);
		Assert.False(record.TryCancel());
	}
}
