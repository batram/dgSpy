using System;
using System.IO;

namespace HookLab.Host.Transport.Discovery {
	public static class DgSpyStateRoot {
		public const string ResidentHostId="hooklab-resident";
		public static string Resolve(string? configuredStateRoot = null) {
			var configured = configuredStateRoot ?? Environment.GetEnvironmentVariable("DGSPY_STATE_ROOT");
			var root = string.IsNullOrWhiteSpace(configured)
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dgSpy")
				: configured;
			return Path.GetFullPath(root);
		}
		public static string HookLabDiscovery(string? configuredStateRoot = null) => Path.Combine(Resolve(configuredStateRoot), "hooklab", "discovery");
		public static string SharedResidentRoot() { var configured=Environment.GetEnvironmentVariable("HOOKLAB_RESIDENT_STATE_ROOT"); return Path.GetFullPath(String.IsNullOrWhiteSpace(configured)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab"):configured); }
	}
}
