using System;
using System.Threading;

namespace NoPdbTarget {
	// An object whose expansion is an aggregate: instance members from one provider plus a "Static members"
	// row from another. Mirrors Milestone1Target.AggregateFixture, because that is the shape that exercised
	// the aggregate paging path, and it has to keep working without a PDB too.
	sealed class NoPdbFixture {
		public static int SharedCount = 3;
		public int InstanceCount = 5;
		public string InstanceName = "no-pdb-instance";
	}

#if DGSPY_REBUILD_VARIANT
	// Present only in the second build. Declared before Program so its methods shift every later method
	// token, which is exactly what makes a cached document from the first build stop matching.
	sealed class RebuildVariantProbe {
		public int Compute(int value) => value * 3;
		public string Describe() => "rebuild variant";
	}
#endif

	static class Program {
		static volatile bool keepRunning = true;

		static void Main(string[] commandLine) {
			Console.WriteLine("PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
#if DGSPY_REBUILD_VARIANT
			Console.WriteLine("VARIANT=rebuild");
#else
			Console.WriteLine("VARIANT=base");
#endif
			Console.Out.Flush();
			var fixture = new NoPdbFixture();
			while (keepRunning) {
				Tick(41);
				// Read the fixture every iteration so it cannot be reported out of scope.
				NoPdbFixture.SharedCount = fixture.InstanceCount;
			}
		}

		// Locals here have no PDB names at all, so the debugger must produce them from decompiled IL.
		// After a rebuild shifts this method's token, a stale cached document has no debug info for the
		// new token and every local disappears - which is the regression this fixture exists to catch.
		static int Tick(int input) {
			int answer = input + 1;
			string label = "dgspy-no-pdb";
			Thread.Sleep(100);
			return answer + label.Length;
		}
	}
}
