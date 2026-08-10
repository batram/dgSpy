using System;

namespace HookLab.Bootstrap.Tests {
	/// <summary>A disposable that counts its disposals and fails the first <c>failures</c> of them.
	///
	/// The cleanup contract is about what happens when one handle's Dispose throws, and the only honest way
	/// to ask that of the real probe would be to break it. These stand in for the two shared handles
	/// instead, through the seam on ProbeStartup.</summary>
	public sealed class CountingHandle : IDisposable {
		readonly string label;
		int failuresRemaining;

		internal CountingHandle(string label, int failures) { this.label = label; failuresRemaining = failures; }

		internal int Disposals { get; private set; }

		public void Dispose() {
			Disposals++;
			if (failuresRemaining <= 0) return;
			failuresRemaining--;
			throw new InvalidOperationException(label + "-dispose-failed");
		}
	}

	/// <summary>Installs the counting handles in place of the live ones, inside the child AppDomain.</summary>
	public static class FakeHandles {
		internal static CountingHandle Runtime = new CountingHandle("runtime", 0);
		internal static CountingHandle Server = new CountingHandle("server", 0);

		internal static void Install(string mode) {
			var never = int.MaxValue;
			switch (mode) {
				case "runtime-fails-always": Runtime = new CountingHandle("runtime", never); Server = new CountingHandle("server", 0); break;
				case "runtime-fails-once": Runtime = new CountingHandle("runtime", 1); Server = new CountingHandle("server", 0); break;
				case "server-fails-once": Runtime = new CountingHandle("runtime", 0); Server = new CountingHandle("server", 1); break;
				case "both-fail-always": Runtime = new CountingHandle("runtime", never); Server = new CountingHandle("server", never); break;
				case "both-fail-once": Runtime = new CountingHandle("runtime", 1); Server = new CountingHandle("server", 1); break;
				default: throw new ArgumentException("Unknown handle mode: " + mode, nameof(mode));
			}
			ProbeStartup.ExchangeHandlesForTest(Runtime, Server, out var liveRuntime, out var liveServer);
			// The real probe and the real listener are displaced, not abandoned: leaving a live named-pipe
			// listener behind would outlast the test and make the next case's failure look like this one's.
			try { (liveRuntime as IDisposable)?.Dispose(); } catch (Exception) { }
			try { liveServer?.Dispose(); } catch (Exception) { }
		}

		internal static string State() => "runtime_disposals=" + Runtime.Disposals + ";server_disposals=" + Server.Disposals;
	}

	/// <summary>Rigs the *startup rollback* so that both disposals fail, and keep failing.
	///
	/// FakeHandles cannot cover this: it swaps the shared handles after a successful start, which leaves
	/// the bootstrap in its started state - and started state is exactly what the defect this rigs for did
	/// not have. A start that publishes both handles and then fails has no started state, so a Shutdown
	/// gated on it retried nothing and a second Start overwrote the retained pair.
	///
	/// The server double disposes the real pipe once before throwing. That is deliberate on both counts: a
	/// teardown that did some of its work and then threw is the case a caller cannot distinguish from one
	/// that did none, so it must still be treated as possibly live; and leaving a real named-pipe listener
	/// running would outlive the test and make the next case's failure look like this one's.</summary>
	public static class RollbackFaults {
		static int runtimeDisposals;
		static int serverDisposals;
		static IDisposable? realServer;
		static bool realServerDisposed;

		internal static void InstallAlwaysFailing() {
			runtimeDisposals = 0;
			serverDisposals = 0;
			realServer = null;
			realServerDisposed = false;
			ProbeStartup.StartupRollbackFaultForTest = () => {
				runtimeDisposals++;
				throw new InvalidOperationException("unpatch-failed-on-purpose");
			};
			ProbeStartup.StartupRollbackServerFaultForTest = real => { realServer = real; return new AlwaysFailingServer(); };
		}

		internal static void Uninstall() {
			ProbeStartup.StartupRollbackFaultForTest = null;
			ProbeStartup.StartupRollbackServerFaultForTest = null;
		}

		internal static string State() => "runtime_disposals=" + runtimeDisposals + ";server_disposals=" + serverDisposals;

		sealed class AlwaysFailingServer : IDisposable {
			public void Dispose() {
				serverDisposals++;
				if (!realServerDisposed) {
					realServerDisposed = true;
					try { realServer?.Dispose(); } catch (Exception) { }
				}
				throw new InvalidOperationException("endpoint-teardown-failed-on-purpose");
			}
		}
	}
}
