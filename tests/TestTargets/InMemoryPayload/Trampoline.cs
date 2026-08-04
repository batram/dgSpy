using System;

namespace InMemoryPayload {
	// Reached only through a callback, so this assembly needs no reference to the target that loads it.
	// Calling back means a breakpoint in the target leaves this frame — belonging to a module with no
	// path — on the stack, which is the case a UCH stack produced and the CorDebug fixture never did.
	public static class Trampoline {
		public static int Call(Func<int,int> callback,int value) {
			int throughInMemoryModule = callback(value);
			return throughInMemoryModule + 1;
		}
	}
}
