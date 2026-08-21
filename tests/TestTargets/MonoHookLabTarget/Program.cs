using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace MonoHookLabTarget {
	/// <summary>A plain Mono target for the HookLab live gate: no Unity, no player, no licence.
	///
	/// <para>It exists because the only live Mono HookLab evidence used to require a Unity player built
	/// from another repository, which made "does HookLab arrive on Mono" answerable only where a Unity
	/// editor was installed. Mono is the runtime; a player merely embeds it, and the arrival path - one
	/// debugger evaluation placed on an owned internal breakpoint over the soft debugger - is identical.
	/// Proving it here first and treating Unity as the special case is the right order.</para>
	///
	/// <para>Run under a Mono the caller supplies. This repository ships no Mono and discovers none:
	/// which Mono happens to be installed must not decide what was measured.</para></summary>
	static class Program {
		/// <summary>The hooked method. Its own method, never inlined by construction - Mono will not
		/// inline across the file write in the loop below anyway, but a hook that silently patched a
		/// method nothing calls would still report success, so the loop observes the return value rather
		/// than the call.</summary>
		public static int Work() => 42;

		/// <summary>The carrier. Arrival on Mono needs a suspended thread with managed frames, so the gate
		/// stops here; a method that only the loop calls gives it a stable, always-reachable place to do
		/// that without depending on where the target happens to be.</summary>
		public static int Tick(int iteration) => Work();

		static int Main(string[] args) {
			if (args.Length < 1) {
				Console.Error.WriteLine("usage: MonoHookLabTarget <run-directory>");
				return 2;
			}
			var run = args[0];
			Directory.CreateDirectory(run);
			var behavior = Path.Combine(run, "behavior.txt");
			var stop = Path.Combine(run, "stop.txt");
			using (var self = System.Diagnostics.Process.GetCurrentProcess())
				Write(Path.Combine(run, "facts.txt"),
					"process_id=" + self.Id.ToString(CultureInfo.InvariantCulture) + "\n" +
					"runtime=" + RuntimeName() + "\n");
			// A second thread that does nothing but count, and a file it advances.
			//
			// Not decoration: arrival is a func-eval on the stopped thread, and the resident's own work
			// runs while every other thread is let run. A single-threaded target is the one shape where
			// "let the others run" has nothing to let run, and no real Mono or Unity target looks like
			// that. It also gives the gate a way to see whether the process is alive while the stopped
			// thread is busy inside an evaluation.
			var heartbeat = Path.Combine(run, "heartbeat.txt");
			var beat = new Thread(() => {
				for (var count = 0L; !File.Exists(stop); count++) {
					Write(heartbeat, count.ToString(CultureInfo.InvariantCulture) + "\n");
					Thread.Sleep(50);
				}
			});
			beat.IsBackground = true;
			beat.Start();

			// Written last, so a reader that sees it can rely on everything above being there.
			Write(Path.Combine(run, "ready.txt"), "ready\n");

			for (var iteration = 0; !File.Exists(stop); iteration++) {
				Write(behavior, Tick(iteration).ToString(CultureInfo.InvariantCulture) + "\n");
				Thread.Sleep(100);
			}
			return 0;
		}

		/// <summary>Which runtime is actually executing this, from the runtime's own answer rather than
		/// from how it was launched. A gate that assumed Mono because it called mono.exe would keep
		/// passing if the launch silently fell through to the CLR.</summary>
		static string RuntimeName() {
			var mono = Type.GetType("Mono.Runtime");
			if (mono is null) return "not-mono";
			var display = mono.GetMethod("GetDisplayName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
			return "mono " + (display?.Invoke(null, null) as string ?? "unknown");
		}

		static void Write(string path, string text) {
			// Written whole and moved into place: the gate polls these files from another process, and a
			// partially written behavior.txt reads as a hook that produced nonsense.
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, text);
			if (File.Exists(path)) File.Delete(path);
			File.Move(temporary, path);
		}
	}
}
