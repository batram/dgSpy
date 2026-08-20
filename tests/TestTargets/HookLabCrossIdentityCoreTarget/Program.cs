using System;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace HookLabCrossIdentityCoreTarget {
	/// <summary>The CoreCLR half of the cross-identity fixture.
	///
	/// <para><b>There is no domain axis here, and that is a property of the runtime rather than a gap in
	/// the fixture.</b> .NET Core has a single application domain; the multi-domain arrangement that hid
	/// defect 5 cannot exist on CoreCLR at all, so this target exercises the identity axis only. Its
	/// net48 sibling covers both.</para>
	///
	/// <para>It reports to a file rather than stdout for the same reason as the sibling: it is launched
	/// as another account, and a driver running as a different principal cannot redirect that account's
	/// console into a place both of them can read.</para></summary>
	static class Program {
		static int Main(string[] args) {
			if (args.Length < 1) { Console.Error.WriteLine("usage: HookLabCrossIdentityCoreTarget <facts-file>"); return 2; }
			var facts = args[0];
			try {
				var identity = WindowsIdentity.GetCurrent();
				var method = typeof(Work).GetMethod(nameof(Work.Tick))!;
				Write(facts, "IDENTITY " + identity.Name);
				Write(facts, "SID " + (identity.User?.Value ?? "unknown"));
				Write(facts, "PROCESS " + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
				Write(facts, "WORKASSEMBLY " + typeof(Work).Assembly.GetName().Name);
				Write(facts, "WORKTYPE " + typeof(Work).FullName);
				Write(facts, "WORKMETHOD " + method.Name);
				Write(facts, "WORKTOKEN " + method.MetadataToken.ToString(CultureInfo.InvariantCulture));
				Write(facts, "WORKMVID " + method.Module.ModuleVersionId.ToString("D"));
				Write(facts, "READY");

				var value = 0;
				while (true) {
					value = Work.Tick(value % 1000);
					Write(facts, "TICK " + value.ToString(CultureInfo.InvariantCulture));
					Thread.Sleep(250);
				}
			}
			catch (Exception ex) {
				try { Write(facts, "FAULT " + ex.GetType().FullName + ": " + ex.Message); } catch { }
				return 1;
			}
		}

		/// <summary>Append one line, tolerating the driver reading the file at the same moment.</summary>
		internal static void Write(string path, string line) {
			for (var attempt = 0; ; attempt++) {
				try {
					using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
					using var writer = new StreamWriter(stream);
					writer.WriteLine(line);
					return;
				}
				catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
			}
		}
	}

	/// <summary>The hooked code. In the target's own assembly rather than a satellite: with no domains to
	/// separate, a second assembly would add nothing the smoke could assert.</summary>
	public static class Work {
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static int Tick(int value) => value + 1;
	}
}
