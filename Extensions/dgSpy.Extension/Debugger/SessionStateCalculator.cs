namespace dgSpy.Extension {
	static class SessionStateCalculator {
		public static string Get(bool faulted,bool attaching,bool isDebugging,bool? isRunning) {
			if (faulted) return "faulted";
			if (attaching && !isDebugging) return "attaching";
			if (!isDebugging) return "exited";
			if (isRunning==true) return "running";
			if (isRunning==false) return "paused";
			return "mixed";
		}
	}
}
