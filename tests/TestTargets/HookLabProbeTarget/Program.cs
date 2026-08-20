using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HookLabProbeTarget {
	/// <summary>A disposable target for the resident compatibility probe, built for both CLR v4 and
	/// CoreCLR.
	///
	/// <para>It loads the shipped payload into itself rather than waiting to be injected or func-evaled.
	/// That is deliberate and is the probe's stated boundary: arrival differs per runtime and is what the
	/// packaged live gates prove, while everything after arrival - dependency closure, compiler, patch
	/// engine, behavior, events, retirement - is what actually varies with the runtime and is what this
	/// fixture exists to exercise. Self-loading is the CoreCLR delivery path minus the debugger.</para>
	///
	/// <para>It reports its own exact guards. A probe that computed the target's MVID, token, signature
	/// and IL digest from the file on disk would be asserting agreement between two of its own readings;
	/// having the target state what it actually loaded is the only reading that can disagree.</para></summary>
	internal static class Program {
		static int Main(string[] arguments) {
			if (arguments.Length != 1) { Console.Error.WriteLine("usage: HookLabProbeTarget <run-directory>"); return 2; }
			var run = Path.GetFullPath(arguments[0]);
			var report = Path.Combine(run, "report.txt");
			try {
				Directory.CreateDirectory(run);
				WriteFacts(Path.Combine(run, "facts.txt"));
				File.WriteAllText(Path.Combine(run, "ready.txt"), "ready\n", Utf8);

				var parameters = Path.Combine(run, "initialize.params");
				if (!Wait(() => File.Exists(parameters), TimeSpan.FromSeconds(30))) throw new TimeoutException("The probe did not write initialize.params.");
				var payload = Path.Combine(run, "payload.path");
				if (!Wait(() => File.Exists(payload), TimeSpan.FromSeconds(30))) throw new TimeoutException("The probe did not name a payload.");

				var bootstrap = Assembly.Load(File.ReadAllBytes(File.ReadAllText(payload).Trim()));
				var entry = bootstrap.GetType("HookLab.Bootstrap.HookLabBootstrap", true)!;
				// Prepare then Commit, not Start: both shipping arrival paths - the native bootstrap's
				// NativeEntry.Initialize and the debugger evaluation CoreCLR uses - go through these two,
				// and only this path honours the endpoint secret the caller supplied. Start generates its
				// own secret and reports it in a field no completion report carries, so a probe using it
				// authenticates against a credential nobody gave it.
				//
				// Both reports are published, because they say different things: Prepare names the control
				// endpoint, Commit reports the behaviour commit. Collapsing them would lose one of the two.
				var prepared = (string)entry.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static)!
					.Invoke(null, new object[] { File.ReadAllText(parameters) })!;
				Write(Path.Combine(run, "prepare.txt"), prepared);
				if (!prepared.StartsWith("status=ok", StringComparison.Ordinal)) { Write(report, prepared); return 1; }
				Write(report, (string)entry.GetMethod("Commit", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!);

				// Tick until told to stop. The probe hooks this method, so it has to keep being called
				// from ordinary managed code rather than through reflection: a patched method observed
				// only through an invoke path would prove less than the product does.
				var behavior = Path.Combine(run, "behavior.txt");
				var stop = Path.Combine(run, "stop.txt");
				var deadline = DateTime.UtcNow.AddMinutes(3);
				while (!File.Exists(stop) && DateTime.UtcNow < deadline) {
					Write(behavior, Work.Tick(41).ToString(CultureInfo.InvariantCulture) + "\n");
					Thread.Sleep(25);
				}

				var shutdown = entry.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
				if (shutdown != null) Write(Path.Combine(run, "shutdown.txt"), (string)shutdown.Invoke(null, null)!);
				return 0;
			}
			catch (Exception ex) {
				Write(report, "status=error\nerror_type=" + (ex.GetType().FullName ?? "Exception") + "\nerror_message=" + Flatten(ex.Message) + "\n");
				return 1;
			}
		}

		static bool Wait(Func<bool> condition, TimeSpan timeout) {
			var deadline = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < deadline) { if (condition()) return true; Thread.Sleep(20); }
			return condition();
		}

		/// <summary>Writes through a temporary file. The probe polls these, and a reader that catches a
		/// half-written report would report a parse failure instead of the stage that actually failed.</summary>
		static void Write(string path, string text) {
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, text, Utf8);
			if (File.Exists(path)) File.Delete(path);
			File.Move(temporary, path);
		}

		static void WriteFacts(string path) {
			var method = typeof(Work).GetMethod(nameof(Work.Tick))!;
			var lines = new[] {
				"assembly=" + method.Module.Assembly.GetName().Name,
				"mvid=" + method.Module.ModuleVersionId.ToString("D"),
				"type=" + method.DeclaringType!.FullName,
				"method=" + method.Name,
				"token=" + unchecked((uint)method.MetadataToken).ToString(CultureInfo.InvariantCulture),
				"signature=" + Signature(method),
				"il_sha256=" + IlSha256(method),
				"process_id=" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
				"framework=" + (typeof(object).Assembly.GetName().Name == "mscorlib" ? "clrv4" : "coreclr"),
				// The resident guards runtime_id and appdomain_id against what it measures in this process,
				// so they have to come from this process. Everything else about the target's identity - PID,
				// creation time, image path - the probe reads independently from the outside, which is where
				// a guard can actually disagree with something.
				"runtime_id=" + System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion(),
				"appdomain_id=" + AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture),
			};
			Write(path, string.Join("\n", lines) + "\n");
		}

		static string Signature(MethodInfo method) =>
			(method.ReturnType.FullName ?? method.ReturnType.Name) + " " + method.Name + "(" +
			string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name).ToArray()) + ")";

		static string IlSha256(MethodInfo method) {
			using (var sha = SHA256.Create())
				return string.Concat(sha.ComputeHash(method.GetMethodBody()!.GetILAsByteArray()!).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)).ToArray());
		}

		static string Flatten(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
		static UTF8Encoding Utf8 => new UTF8Encoding(false);
	}

	public static class Work {
		/// <summary>The hooked method. Not inlined, so the patch engine has a body to redirect on both
		/// runtimes; unoptimized at the project level for the same reason.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static int Tick(int value) => value + 1;
	}
}
