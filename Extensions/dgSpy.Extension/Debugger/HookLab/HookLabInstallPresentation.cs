using HookLab.Contracts;

namespace dgSpy.Extension {
	static class HookLabInstallPresentation {
		public const string DialogExplanation="HookLab initializes itself if needed, then installs this hook through its resident runtime. The selected method is the hook target, never the injection carrier.";

		public static string Waiting(string method) =>
			"Initializing HookLab if needed, then installing "+method+" through the resident runtime.";

		public static string Failure(string operation,ActionOutcome outcome,InterruptionReason reason,string? detail) {
			var message="HookLab "+operation+" failed: "+outcome+" ("+reason+").";
			if(!string.IsNullOrWhiteSpace(detail)) message+=" "+detail;
			if(outcome==ActionOutcome.trigger_not_reached && reason==InterruptionReason.timeout)
				message+=" The target never entered the selected method before the deadline. Trigger the method while HookLab is waiting, then retry.";
			return message;
		}
	}
}
