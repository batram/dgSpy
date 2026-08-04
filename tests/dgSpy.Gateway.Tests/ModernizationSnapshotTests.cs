using System;
using System.IO;
using dgSpy.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class ModernizationSnapshotTests {
	static readonly JsonSerializerSettings Settings = new() {
		NullValueHandling = NullValueHandling.Ignore,
		Formatting = Formatting.Indented,
	};

	[Fact]
	public void Mcp_tool_discovery_matches_the_modernization_baseline() =>
		AssertSnapshot("mcp-tools.json", new { tools = ToolCatalog.All });

	[Fact]
	public void Capability_contract_matches_the_modernization_baseline() =>
		AssertSnapshot("capabilities.json", CapabilityCatalog.Describe("snapshot"));

	static void AssertSnapshot(string name, object actual) {
		var outputDirectory = Path.Combine(AppContext.BaseDirectory, "Snapshots");
		var updateDirectory = Environment.GetEnvironmentVariable("DGSPY_SNAPSHOT_DIR");
		var directory = Environment.GetEnvironmentVariable("DGSPY_UPDATE_SNAPSHOTS") == "1" && !string.IsNullOrWhiteSpace(updateDirectory)
			? updateDirectory
			: outputDirectory;
		var path = Path.Combine(directory, name);
		var json = JsonConvert.SerializeObject(actual, Settings) + Environment.NewLine;
		if (Environment.GetEnvironmentVariable("DGSPY_UPDATE_SNAPSHOTS") == "1") {
			Directory.CreateDirectory(directory);
			File.WriteAllText(path, json);
			return;
		}

		Assert.True(File.Exists(path), $"Missing modernization snapshot: {path}");
		var expected = JToken.Parse(File.ReadAllText(path));
		var observed = JToken.Parse(json);
		Assert.True(JToken.DeepEquals(expected, observed),
			$"{name} drifted. Review behavior separately, then run tests/run-modernization-gate.ps1 -UpdateSnapshots only for an accepted wire-contract change.");
	}
}
