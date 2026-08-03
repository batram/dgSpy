using System;

namespace dgSpy.Extension {
	static class ProgramIdentity {
		public static string Create(int processId,Guid runtimeGuid,string runtimeName) =>
			$"{processId}:{runtimeGuid:N}:{runtimeName}";
	}
}
