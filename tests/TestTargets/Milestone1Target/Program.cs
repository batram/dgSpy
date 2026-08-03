using System;
using System.Reflection;
using System.Threading;

namespace Milestone1Target {
	static class Program {
		static volatile bool keepRunning = true;

		static void Main() {
			Console.WriteLine("PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
			Console.WriteLine("TOKEN=" + typeof(Program).GetMethod(nameof(Tick), BindingFlags.Static | BindingFlags.NonPublic).MetadataToken);
			Console.Out.Flush();
			while (keepRunning)
				Tick(41);
		}

		static int Tick(int input) {
			int answer = input + 1;
			string label = "dgSpy-milestone-1";
			Thread.Sleep(100);
			return answer + label.Length;
		}
	}
}
