using HookLab.Injector;

namespace dgSpy.Extension {
	/// <summary>
	/// Autonomous HookLab initialization: get the native bootstrap into the target, and say exactly
	/// why if the target's loader refuses it.
	///
	/// This used to carry its own near line-for-line copy of the injector in HookLab.Injector,
	/// including the defect both shared: a remote thread started directly at LoadLibraryW, whose exit
	/// code is a truncated HMODULE, so a denied path and a missing dependency produced the same
	/// message. Both now call one implementation, which lives with the injector because that is where
	/// it belongs. The extension compiles that file as a link rather than referencing the project:
	/// HookLab.Injector targets net10.0-windows only, while this extension still multi-targets net48,
	/// which is why the intended consumption never happened. Resolving that is Road 1 subslice 3.
	/// </summary>
	static class NativeHookLabInitializer {
		public static void Load(int processId,string libraryPath)=>RemoteLibraryLoader.Load(processId,libraryPath);
	}
}
