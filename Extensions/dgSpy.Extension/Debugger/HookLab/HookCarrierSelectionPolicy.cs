using System;
using HookLab.Contracts;

namespace dgSpy.Extension {
	static class HookCarrierSelectionPolicy {
		// Atomic arrival needs a method that will be entered again after resume. The shallowest managed
		// frame is the best available proxy for active/repeating work. A process-image entry point is the
		// opposite: it scores attractively by ownership but is commonly entered only once.
		public static int Score(int frameIndex,bool isProcessImage,bool isFramework) =>
			10000-Math.Min(frameIndex,1000)*10-(isProcessImage?5000:0)-(isFramework?1:0);

		public static bool TryNormalizeLocation(ulong methodToken,ulong ilOffset,out int normalizedToken,out int normalizedOffset) {
			normalizedToken=0;
			normalizedOffset=0;
			if(methodToken==0 || methodToken>Int32.MaxValue || ilOffset>Int32.MaxValue) return false;
			normalizedToken=(int)methodToken;
			normalizedOffset=(int)ilOffset;
			return true;
		}

		public static bool ShouldTryAnotherCarrier(ActionOutcome outcome,bool actionMayHaveExecuted) =>
			!actionMayHaveExecuted && (outcome==ActionOutcome.reached_not_evaluable || outcome==ActionOutcome.nearby_slot_not_found);
	}
}
