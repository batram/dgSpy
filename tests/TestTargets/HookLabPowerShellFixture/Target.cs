using System.Runtime.CompilerServices;

namespace HookLabPowerShellFixture {
	public static class Target {
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static int Calculate(int value) => value + 1;
	}
}
