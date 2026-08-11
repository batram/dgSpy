using dgSpy.Extension;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookCarrierSelectionPolicyTests {
	[Fact]
	public void Shallow_repeating_frame_beats_deep_process_entry_point() {
		var repeatingFramework=HookCarrierSelectionPolicy.Score(frameIndex:1,isProcessImage:false,isFramework:true);
		var processEntryPoint=HookCarrierSelectionPolicy.Score(frameIndex:12,isProcessImage:true,isFramework:false);
		Assert.True(repeatingFramework>processEntryPoint);
	}

	[Fact]
	public void Shallower_managed_frame_wins_within_the_same_category() =>
		Assert.True(HookCarrierSelectionPolicy.Score(2,false,false)>HookCarrierSelectionPolicy.Score(8,false,false));

	[Fact]
	public void Oversized_debugger_locations_are_rejected_without_overflowing() {
		Assert.False(HookCarrierSelectionPolicy.TryNormalizeLocation(0x06000001,UInt32.MaxValue,out _,out _));
		Assert.False(HookCarrierSelectionPolicy.TryNormalizeLocation(UInt32.MaxValue,1,out _,out _));
		Assert.True(HookCarrierSelectionPolicy.TryNormalizeLocation(0x06000001,42,out var token,out var offset));
		Assert.Equal(0x06000001,token);
		Assert.Equal(42,offset);
	}

	[Theory]
	[InlineData(ActionOutcome.reached_not_evaluable,false,true)]
	[InlineData(ActionOutcome.nearby_slot_not_found,false,true)]
	[InlineData(ActionOutcome.trigger_not_reached,false,false)]
	[InlineData(ActionOutcome.reached_not_evaluable,true,false)]
	public void Only_safe_arrival_failures_try_another_carrier(ActionOutcome outcome,bool actionMayHaveExecuted,bool expected) =>
		Assert.Equal(expected,HookCarrierSelectionPolicy.ShouldTryAnotherCarrier(outcome,actionMayHaveExecuted));
}
