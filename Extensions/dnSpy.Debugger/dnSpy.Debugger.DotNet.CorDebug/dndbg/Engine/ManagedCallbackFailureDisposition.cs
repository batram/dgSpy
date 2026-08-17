/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

namespace dndbg.Engine {
	enum ManagedCallbackFailureDisposition {
		Continue,
		KeepPaused,
		CompleteExit,
	}

	static class ManagedCallbackFailurePolicy {
		public static ManagedCallbackFailureDisposition Decide(bool isExitProcess, bool hasQueuedCallbacks, bool hasIntentionalPause) {
			if (isExitProcess)
				return ManagedCallbackFailureDisposition.CompleteExit;
			if (hasQueuedCallbacks)
				return ManagedCallbackFailureDisposition.Continue;
			if (hasIntentionalPause)
				return ManagedCallbackFailureDisposition.KeepPaused;
			return ManagedCallbackFailureDisposition.Continue;
		}
	}
}
