using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

namespace HookLabCrossIdentityTarget {
	/// <summary>Everything that touches the work assembly, executed entirely inside the created domain.
	///
	/// <para>It is driven by <see cref="AppDomain.DoCallBack"/> rather than by calling methods on a
	/// MarshalByRefObject proxy, and that is the whole point. Measured on 2026-08-20: with a proxy, one
	/// call whose body touched <c>Fixture.Work</c> left the work assembly loaded in the <b>default</b>
	/// domain as well - confirmed independently by the target's own reflection and by list_modules, which
	/// reported the assembly in domains 1 and 2. A fixture for "a resident in the wrong domain cannot see
	/// the application's assemblies" that loads those assemblies into both domains proves nothing.</para>
	///
	/// <para>The callback is a static method taking no arguments, so nothing crosses the boundary except
	/// through <see cref="AppDomain.SetData"/>, and no signature the parent JITs mentions a type from the
	/// work assembly.</para></summary>
	static class RemoteWork {
		internal static void Run() {
			var facts = (string)AppDomain.CurrentDomain.GetData("facts");
			var method = typeof(Fixture.Work).GetMethod("Tick")!;
			Program.Write(facts, "WORKDOMAIN " + AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture));
			Program.Write(facts, "WORKDOMAINNAME " + AppDomain.CurrentDomain.FriendlyName);
			Program.Write(facts, "WORKASSEMBLY " + typeof(Fixture.Work).Assembly.GetName().Name);
			Program.Write(facts, "WORKTYPE " + typeof(Fixture.Work).FullName);
			Program.Write(facts, "WORKMETHOD " + method.Name);
			Program.Write(facts, "WORKTOKEN " + method.MetadataToken.ToString(CultureInfo.InvariantCulture));
			Program.Write(facts, "WORKMVID " + method.Module.ModuleVersionId.ToString("D"));
			Program.Write(facts, "READY");

			// Called forever, so a hook installed at any moment observes the next call rather than racing
			// a one-shot. This loop runs in the created domain for the life of the process.
			var value = 0;
			while (true) {
				value = Fixture.Work.Tick(value % 1000);
				Program.Write(facts, "TICK " + value.ToString(CultureInfo.InvariantCulture));
				Thread.Sleep(250);
			}
		}
	}

	/// <summary>A target for the cross-identity, cross-domain HookLab smoke.
	///
	/// <para>It exists because every green HookLab gate before it ran the debugger and the target as the
	/// same user, in the default application domain, on a developer machine - an environment that
	/// satisfies silently every assumption the 2026-08-19 incident violated. Five defects hid behind that
	/// arrangement, and three more were found live on 2026-08-20 for the same reason.</para>
	///
	/// <para>So this target does two things a console fixture normally would not: it runs its hookable
	/// code in a <b>second application domain</b>, and it writes its facts to a file rather than stdout,
	/// because it is launched as another account whose stdout the driver cannot redirect into a place
	/// both principals can reach.</para></summary>
	static class Program {
		/// <summary>Single-domain loading, explicitly. Without it the CLR may load the work assembly
		/// domain-neutral, in which case it is shared across domains and listed by both - measured here,
		/// with the remote call correctly executing in domain 2 while the default domain still reported
		/// the assembly resident. A fixture whose whole point is "this code exists in exactly one domain"
		/// cannot leave that to the loader's discretion.</summary>
		[LoaderOptimization(LoaderOptimization.SingleDomain)]
		static int Main(string[] args) {
			if (args.Length < 1) { Console.Error.WriteLine("usage: HookLabCrossIdentityTarget <facts-file>"); return 2; }
			var facts = args[0];
			try {
				var here = Path.GetDirectoryName(new Uri(typeof(Program).Assembly.CodeBase).LocalPath)!;
				var identity = WindowsIdentity.GetCurrent();
				Write(facts, "IDENTITY " + identity.Name);
				Write(facts, "SID " + (identity.User?.Value ?? "unknown"));
				Write(facts, "PROCESS " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
				Write(facts, "DEFAULTDOMAIN " + AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture));

				var domain = AppDomain.CreateDomain("dgspy-fixture-app", null, new AppDomainSetup { ApplicationBase = here });
				domain.SetData("facts", facts);
				// Reported from here, after the work has been running, so it measures the state the
				// debugger will actually find. The smoke asserts the same thing through list_modules,
				// which is the answer that matters: a resident in the default domain must not be able to
				// see the application's assemblies.
				new Thread(() => {
					Thread.Sleep(1500);
					Write(facts, "WORKLISTEDINDEFAULTDOMAIN " + LoadedHere());
				}) { IsBackground = true }.Start();
				// Blocks for the life of the process: RemoteWork.Run loops inside the created domain.
				domain.DoCallBack(RemoteWork.Run);
				return 0;
			}
			catch (Exception ex) {
				try { Write(facts, "FAULT " + ex.GetType().FullName + ": " + ex.Message); } catch { }
				return 1;
			}
		}

		/// <summary>Whether the work assembly is resident in THIS domain. The fixture's premise is that it
		/// is not: a resident in the default domain must be unable to see the hooked type.</summary>
		static string LoadedHere() =>
			Array.Exists(AppDomain.CurrentDomain.GetAssemblies(), value => value.GetName().Name == "HookLabCrossIdentityWork") ? "true" : "false";

		/// <summary>Append one line, tolerating the driver reading the file at the same moment. The driver
		/// polls this file; a sharing violation here would fault a target that is otherwise healthy.</summary>
		internal static void Write(string path, string line) {
			for (var attempt = 0; ; attempt++) {
				try {
					using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
					using (var writer = new StreamWriter(stream)) { writer.WriteLine(line); }
					return;
				}
				catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
			}
		}
	}
}
