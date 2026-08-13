using System.Runtime.CompilerServices;

namespace HookLabPowerShellFixture {
	public static class Target {
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static int Calculate(int value) => value + 1;

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static T Identity<T>(T value) => value;
	}

	public sealed class InstanceTarget {
		public InstanceTarget(int offset) { Offset = offset; }
		public int Offset;

		[MethodImpl(MethodImplOptions.NoInlining)]
		public int Calculate(int value) => value + Offset;
	}
}
