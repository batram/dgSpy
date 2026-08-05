using System;
using System.IO;
using dgSpy.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class ModernizationSnapshotTests {
	static readonly JsonSerializerOptions Settings = new(ProtocolJson.Options) { WriteIndented=true };

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
		var json = ProtocolJson.Serialize(actual, Settings) + Environment.NewLine;
		if (Environment.GetEnvironmentVariable("DGSPY_UPDATE_SNAPSHOTS") == "1") {
			Directory.CreateDirectory(directory);
			File.WriteAllText(path, json);
			return;
		}

		Assert.True(File.Exists(path), $"Missing modernization snapshot: {path}");
		var expected = JsonNode.Parse(File.ReadAllText(path));
		var observed = JsonNode.Parse(json);
		Assert.True(JsonNode.DeepEquals(expected, observed),
			$"{name} drifted. Review behavior separately, then run tests/run-modernization-gate.ps1 -UpdateSnapshots only for an accepted wire-contract change.");
	}
}
