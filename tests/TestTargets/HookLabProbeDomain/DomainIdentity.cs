using System;
using System.IO;

namespace HookLabProbeDomain {
	/// <summary>A path-loadable identity witness for the Mono AppDomain reload fixture.</summary>
	public sealed class DomainIdentity : MarshalByRefObject {
		public int Id => AppDomain.CurrentDomain.Id;

		public void ArmUnloadWitness(string path) {
			AppDomain.CurrentDomain.DomainUnload += (sender, arguments) => File.WriteAllText(path, "unloaded\n");
		}
	}
}
