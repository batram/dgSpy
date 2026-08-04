/*
	dgSpy addition, not part of upstream dnSpy.

	dnSpy brings its main window to the foreground every time the debugger stops: SetForegroundWindow
	plus Window.Activate, from both DebuggerImpl.ActivateWindow_UI and
	WpfCurrentStatementUpdater.ActivateMainWindow_UI. That is right for interactive use and wrong when
	dnSpy is being driven headlessly as dgSpy's debugger host — it takes focus away from whatever the
	user is typing into, on every breakpoint hit. Nothing outside the process can prevent it: a window
	style or an external hide loop is always racing code inside the app that owns the window.

	Read from the process command line rather than through IAppCommandLineArgs. That interface is
	*not* a MEF export — nothing in dnSpy imports it, it is threaded through App by hand — so adding
	it to an [ImportingConstructor] silently fails to compose the part. Doing that to DebuggerImpl
	takes the whole Debug menu with it, since every one of those commands imports Debugger, and MEF
	reports nothing: the menu entries are simply absent. This static costs one array scan at startup
	and cannot break composition.

	Only the foreground grab is suppressed. The window still opens, still shows source, and keeps
	every command, so a user can take over an automated session by clicking on it.
*/

using System;

namespace dnSpy.Debugger.DbgUI {
	static class DgSpyWindowActivation {
		/// <summary>The switch dgSpy passes when it starts dnSpy as a headless host.</summary>
		public const string CommandLineSwitch = "--dgspy-no-window-activation";

		/// <summary>True when dnSpy must never pull itself to the foreground on its own.</summary>
		public static bool Suppressed { get; } = HasSwitch();

		static bool HasSwitch() {
			try {
				foreach (var arg in Environment.GetCommandLineArgs()) {
					if (StringComparer.OrdinalIgnoreCase.Equals(arg, CommandLineSwitch))
						return true;
				}
			}
			catch (Exception) {
				// A host that will not hand out its command line is not a reason to fail startup.
			}
			return false;
		}
	}
}
