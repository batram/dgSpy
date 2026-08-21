using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
		public static int Tick(int iteration) => Work() + GenericWork(iteration) + Holder<int>.Work() + Inlineable();

		/// <summary>A generic method, present so the boundary gate can ask what happens when a hook names
		/// one. Its value is deliberately zero so it cannot move the observed total unless a hook moves
		/// it.</summary>
		public static int GenericWork<T>(T value) => 0;

		/// <summary>A generic declaring type, which is a different question from a generic method: the
		/// method here is not generic, but it has no single runtime method to patch until the type is
		/// closed over something.</summary>
		public static class Holder<T> {
			public static int Work() => 0;
		}

		/// <summary>Asks to be inlined, so that a hook on it is the case where patching the method changes
		/// nothing at call sites the JIT has already compiled. Zero for the same reason as above.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int Inlineable() => 0;

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

		/// <summary>Publishes one value atomically, and never throws.
		///
		/// <para>Written whole and swapped into place, because the gate polls these files from another
		/// process and a partially written <c>behavior.txt</c> reads as a hook that produced nonsense.</para>
		///
		/// <para>The retry and the catch are not defensive habit. The gate reads this file every 100 ms
		/// while this loop rewrites it every 100 ms, and Windows answers that collision with a sharing
		/// violation rather than a wait - so the swap really does fail, roughly once every few hundred
		/// writes. Without the catch that <c>IOException</c> is unhandled on the main thread, and an
		/// unhandled exception in a debugged target stops the target: the debugger is doing its job, but
		/// the resident is then frozen and cannot answer dgSpy's next control command. That cost a long
		/// investigation, during which the fault looked like a dgSpy or dnSpy defect from every angle
		/// except this one. A test fixture that stops itself is a fixture bug, and it must not be able to
		/// masquerade as a product one.</para></summary>
		static void Write(string path, string text) {
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, text);
			for (var attempt = 0; attempt < 50; attempt++) {
				try {
					if (File.Exists(path)) File.Delete(path);
					File.Move(temporary, path);
					return;
				}
				catch (IOException) {
					Thread.Sleep(10);
				}
			}
			// Half a second of contention is not a value worth publishing late; drop it and let the next
			// tick say the same thing. The stale file is still valid - it is simply one tick behind.
			try { File.Delete(temporary); }
			catch (IOException) { }
		}
	}
}
