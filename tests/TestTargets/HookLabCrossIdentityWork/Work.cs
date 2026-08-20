using System.Runtime.CompilerServices;

namespace Fixture {
	/// <summary>The code a hook is installed on, in its own assembly so it can be loaded into a secondary
	/// application domain the way an ASP.NET application's own assemblies are - and, crucially, so it is
	/// loaded into <b>only</b> that domain. A resident placed in the default domain cannot see this type
	/// at all, which is exactly the discriminator the 2026-08-19 incident turned on.</summary>
	public static class Work {
		/// <summary>Deliberately trivial and never inlined, so the smoke can assert on the exact value a
		/// hook changes and on the exact IL digest that guards it.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static int Tick(int value) => value + 1;
	}
}
