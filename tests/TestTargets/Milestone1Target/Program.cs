using System;
using System.Reflection;
using System.Threading;

namespace Milestone1Target {
	interface IWorker { int Run(int value); }
	class BaseWorker { public virtual int Run(int value) => value; }
	sealed class Worker : BaseWorker, IWorker { public override int Run(int value) => value + 7; }

	static class Program {
		static volatile bool keepRunning = true;

		static void Main(string[] commandLine) {
			Console.WriteLine("PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
			Console.WriteLine("TOKEN=" + typeof(Program).GetMethod(nameof(Tick), BindingFlags.Static | BindingFlags.NonPublic).MetadataToken);
			Console.Out.Flush();
			var exitAfterMs=ReadInt(commandLine,"--exit-after-ms");
			var exitCode=ReadInt(commandLine,"--exit-code");
			if (exitAfterMs.HasValue) new Thread(()=>{ Thread.Sleep(exitAfterMs.Value); Environment.Exit(exitCode ?? 0); }) { IsBackground=true }.Start();
			while (keepRunning)
				Tick(41);
		}

		static int? ReadInt(string[] commandLine,string name) {
			for (var index=0;index+1<commandLine.Length;index++)
				if (commandLine[index]==name && int.TryParse(commandLine[index+1],out var value)) return value;
			return null;
		}

		static int Tick(int input) {
			int answer = input + 1;
			string label = "dgSpy-milestone-1";
			answer = UseWorker(new Worker(),answer);
			Thread.Sleep(100);
			return answer + label.Length;
		}

		static int UseWorker(IWorker worker,int value) {
			string searchableText = "phase-six-text-search-fixture";
			return worker.Run(value) + searchableText.Length;
		}
	}
}
