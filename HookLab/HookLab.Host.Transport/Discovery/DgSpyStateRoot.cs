using System;
using System.IO;

namespace HookLab.Host.Transport.Discovery {
	public static class DgSpyStateRoot {
		public static string Resolve(string? configuredStateRoot = null) {
			var configured = configuredStateRoot ?? Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT");
			var root = string.IsNullOrWhiteSpace(configured)
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dgSpy")
				: configured;
			return Path.GetFullPath(root);
		}
		public static string HookLabDiscovery(string? configuredStateRoot = null) => Path.Combine(Resolve(configuredStateRoot), "hooklab", "discovery");
	}
}
