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
}
