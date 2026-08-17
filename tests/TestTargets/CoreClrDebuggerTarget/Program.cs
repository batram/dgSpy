using System;
using System.Diagnostics;
using System.Threading;

namespace CoreClrDebuggerTarget;

static class Program {
	static int Main(string[] arguments) {
		int exitAfterMilliseconds = arguments.Length == 2 && arguments[0] == "--exit-after-ms" ? int.Parse(arguments[1]) : 0;
		Console.WriteLine("READY " + Environment.ProcessId);
		Console.Out.Flush();
		if (exitAfterMilliseconds > 0) {
			Thread.Sleep(exitAfterMilliseconds);
			return 0;
		}
		int iteration = 0;
		while (true) {
			int result = Probe(iteration++);
			if ((result & 31) == 0) {
				Console.WriteLine("HEARTBEAT " + result);
				Console.Out.Flush();
			}
		}
	}

	[DebuggerStepThrough]
	static int Probe(int value) {
		int answer = value + 7;
		Thread.Sleep(25);
		return answer;
	}
}
