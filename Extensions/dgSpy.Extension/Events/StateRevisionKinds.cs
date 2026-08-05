using dgSpy.Protocol;

namespace dgSpy.Extension {
	static class StateRevisionKinds {
		public static bool ChangesLifecycle(string kind) => kind==EventKinds.SessionStarted || kind==EventKinds.SessionEnded || kind==EventKinds.Attached || kind==EventKinds.AttachFailed || kind==EventKinds.Detached || kind==EventKinds.Restarted || kind==EventKinds.Terminated || kind==EventKinds.RestartProcessExited || kind==EventKinds.SessionExited || kind==EventKinds.ProcessCreated || kind==EventKinds.RuntimeCreated || kind==EventKinds.RuntimeExited;
		public static bool ChangesExecution(string kind) => kind==EventKinds.Continued || kind==EventKinds.Stopped;
	}
}
