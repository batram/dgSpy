using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

namespace HookLabCrossIdentityTarget {
	/// <summary>Runs inside the created application domain and is the only thing that touches the work
	/// assembly.
	///
	/// <para>It lives in <b>this</b> assembly rather than in the work assembly on purpose. The default
	/// domain has to hold a proxy typed as something, and whatever that type is gets loaded there; if it
	/// came from the work assembly, the work assembly would be resident in both domains and the fixture
	/// would stop proving anything about domain selection. Every member below executes remotely, so
	/// Fixture.Work resolves only in the created domain.</para></summary>
	public sealed class RemoteWork : MarshalByRefObject {
		public override object InitializeLifetimeService() => null;
		public int DomainId => AppDomain.CurrentDomain.Id;
		public string DomainName => AppDomain.CurrentDomain.FriendlyName;
		public int Tick(int value) => Fixture.Work.Tick(value);
		/// <summary>Guard material for the hooked method, read where the assembly actually lives. The
		/// smoke needs the exact MVID, token, signature and IL digest, and reading them here avoids the
		/// driver having to resolve a module it deliberately cannot see.</summary>
		public string[] TargetFacts() {
			var method = typeof(Fixture.Work).GetMethod("Tick")!;
			return new[] {
				"FACTSRANINDOMAIN " + AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture),
				"WORKASSEMBLY " + typeof(Fixture.Work).Assembly.GetName().Name,
				"WORKTYPE " + typeof(Fixture.Work).FullName,
				"WORKMETHOD " + method.Name,
				"WORKTOKEN " + method.MetadataToken.ToString(CultureInfo.InvariantCulture),
				"WORKMVID " + method.Module.ModuleVersionId.ToString("D"),
			};
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
				var domain = AppDomain.CreateDomain("dgspy-fixture-app", null, new AppDomainSetup { ApplicationBase = here });
				var remote = (RemoteWork)domain.CreateInstanceAndUnwrap(typeof(RemoteWork).Assembly.FullName, typeof(RemoteWork).FullName!);

				var identity = WindowsIdentity.GetCurrent();
				Write(facts, "IDENTITY " + identity.Name);
				Write(facts, "SID " + (identity.User?.Value ?? "unknown"));
				Write(facts, "PROCESS " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
				Write(facts, "DEFAULTDOMAIN " + AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture));
				Write(facts, "WORKDOMAIN " + remote.DomainId.ToString(CultureInfo.InvariantCulture));
				Write(facts, "WORKDOMAINNAME " + remote.DomainName);
				foreach (var fact in remote.TargetFacts()) Write(facts, fact);
				// Recorded as an observation, NOT as the fixture's premise. Measured: the work assembly's
				// code runs in the created domain - TargetFacts reports domain 2 from inside - and the
				// default domain's GetAssemblies still lists it afterwards, with no remoting frames on the
				// stack that loads it. Rather than encode a reflection heuristic nobody has explained, the
				// premise is asserted where it matters and by the product itself: the smoke initializes
				// HookLab in the default domain and shows it cannot bind the work assembly, then
				// initializes in the created domain and shows it can. That is the discriminator defect 5
				// actually turned on.
				Write(facts, "WORKLISTEDINDEFAULTDOMAIN " + LoadedHere());
				Write(facts, "READY");

				// Called forever, so a hook installed at any moment observes the next call rather than
				// racing a one-shot. The value is echoed so the smoke can see a hook change behaviour.
				var value = 0;
				while (true) {
					value = remote.Tick(value % 1000);
					Write(facts, "TICK " + value.ToString(CultureInfo.InvariantCulture));
					Thread.Sleep(250);
				}
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
		static void Write(string path, string line) {
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
