using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace CoreClrDebuggerTarget;

static class Program {
	static int Main(string[] arguments) {
		int exitAfterMilliseconds = arguments.Length == 2 && arguments[0] == "--exit-after-ms" ? int.Parse(arguments[1]) : 0;
		Console.WriteLine("READY " + Environment.ProcessId);
		var probe = typeof(Program).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic)!;
		Console.WriteLine("MVID " + probe.Module.ModuleVersionId.ToString("D"));
		Console.WriteLine("TOKEN " + probe.MetadataToken);
		Console.WriteLine("SIGNATURE System.Int32 Probe(System.Int32)");
		Console.WriteLine("ILSHA " + Convert.ToHexString(SHA256.HashData(probe.GetMethodBody()!.GetILAsByteArray()!)).ToLowerInvariant());
		Console.Out.Flush();
		if (exitAfterMilliseconds > 0) {
			Thread.Sleep(exitAfterMilliseconds);
			return 0;
		}
		int iteration = 0;
		int lastDelta = Int32.MinValue;
		while (true) {
			int input = iteration++;
			int result = Probe(input);
			int delta = result - input;
			if (delta != lastDelta) { Console.WriteLine("DELTA " + delta); Console.Out.Flush(); lastDelta = delta; }
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
