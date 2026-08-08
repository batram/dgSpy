using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The comparison is exercised as a pure function on purpose. The cases that matter -- one
/// skewed host among several, an unpackaged side, two commit formats that are equal, a dirty tree --
/// are all reachable here and none of them is reachable from a live registry on demand.</summary>
public sealed class GatewayBuildTests {
	static JsonNode Compare(string? gatewayCommit,DateTime? gatewayBuilt,params GatewayBuild.HostBuild[] hosts) =>
		JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GatewayBuild.CompareHostBuilds(gatewayCommit,gatewayBuilt,(IReadOnlyList<GatewayBuild.HostBuild>)hosts)))!;
	static readonly DateTime Noon=new(2026,8,8,12,22,0,DateTimeKind.Utc);
	static readonly DateTime Afternoon=new(2026,8,8,14,14,0,DateTimeKind.Utc);

	[Fact]
	public void Differing_commits_are_skew_and_the_older_side_is_named() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,new GatewayBuild.HostBuild("local","26ccdd25",Noon));
		Assert.True((bool?)result["skewed"]); Assert.True((bool?)result["known"]);
		Assert.Equal("differs",(string?)result["hosts"]![0]!["comparison"]);
		Assert.Equal("bf5ed03551ff",(string?)result["gateway_commit"]);
		Assert.Contains("newer",(string?)result["detail"]!);
		Assert.NotNull((string?)result["recovery"]);
	}
	/// <summary>The two sides do not format a commit alike: a host truncates to eight characters, the
	/// package manifest carries the full forty. Demanding equal strings would report skew on every
	/// call of a correctly matched pair -- a false alarm is as useless as no alarm.</summary>
	[Fact]
	public void A_short_host_commit_matches_the_full_gateway_commit() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Noon,new GatewayBuild.HostBuild("local","bf5ed035",Noon));
		Assert.False((bool?)result["skewed"]); Assert.Equal("match",(string?)result["hosts"]![0]!["comparison"]);
		Assert.Null((string?)result["recovery"]);
	}
	[Fact]
	public void A_dirty_tree_matches_but_says_the_commit_does_not_identify_it() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Noon,new GatewayBuild.HostBuild("local","bf5ed035-dirty",Noon));
		Assert.False((bool?)result["skewed"]); Assert.Equal("match_dirty",(string?)result["hosts"]![0]!["comparison"]);
		Assert.Contains("dirty",(string?)result["detail"]!);
	}
	/// <summary>An unpackaged build has no commit anyone can check out. Reporting that as skew would
	/// fire on every worktree build, which is how a warning stops being read.</summary>
	[Fact]
	public void An_unpackaged_gateway_is_unknown_rather_than_skewed() {
		var result=Compare(null,Noon,new GatewayBuild.HostBuild("local","26ccdd25",Noon));
		Assert.False((bool?)result["known"]); Assert.False((bool?)result["skewed"]);
		Assert.Equal("unknown",(string?)result["hosts"]![0]!["comparison"]);
	}
	[Fact]
	public void An_unpackaged_host_is_unknown_rather_than_skewed() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Noon,new GatewayBuild.HostBuild("local",null,null));
		Assert.True((bool?)result["known"]); Assert.False((bool?)result["skewed"]);
		Assert.Equal("unknown",(string?)result["hosts"]![0]!["comparison"]);
	}
	[Fact]
	public void One_skewed_host_among_several_is_still_skew_and_is_the_one_named() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,
			new GatewayBuild.HostBuild("matching","bf5ed035",Afternoon),
			new GatewayBuild.HostBuild("stale","26ccdd25",Noon));
		Assert.True((bool?)result["skewed"]);
		Assert.Equal("match",(string?)result["hosts"]![0]!["comparison"]);
		Assert.Equal("differs",(string?)result["hosts"]![1]!["comparison"]);
		Assert.Contains("stale at 26ccdd25",(string?)result["detail"]!);
		Assert.DoesNotContain("matching at",(string?)result["detail"]!);
	}
	[Fact]
	public void No_hosts_is_not_skew() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Noon);
		Assert.True((bool?)result["known"]); Assert.False((bool?)result["skewed"]);
	}

	static JsonNode Process(string? loaded,string? disk) =>
		JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GatewayBuild.CompareProcessToDisk(loaded,disk,@"C:\install\cli\bin\dgSpy.Gateway.dll")))!;

	/// <summary>The literal shape of the incident: dgSpy is reinstalled, the agent's MCP server keeps
	/// serving the descriptions it loaded at startup, and nothing says so.</summary>
	[Fact]
	public void A_gateway_installed_over_while_running_is_stale() {
		var result=Process("aa11","bb22");
		Assert.True((bool?)result["stale"]); Assert.True((bool?)result["known"]);
		Assert.Contains("reinstalled",(string?)result["detail"]!);
		Assert.Contains("restarted",(string?)result["recovery"]!);
	}
	[Fact]
	public void A_gateway_matching_its_own_path_is_not_stale() {
		var result=Process("aa11","AA11");
		Assert.True((bool?)result["known"]); Assert.False((bool?)result["stale"]);
	}
	[Fact]
	public void An_unhashable_assembly_is_unknown_rather_than_stale() {
		Assert.False((bool?)Process("unknown","bb22")["known"]);
		Assert.False((bool?)Process("unknown","bb22")["stale"]);
		Assert.False((bool?)Process(null,null)["known"]);
	}

	/// <summary>The Gateway must be able to name itself even when it was never packaged, because that
	/// is exactly the build a maintainer is most likely to be driving when something looks wrong.</summary>
	[Fact]
	public void The_gateway_describes_itself_without_a_package_manifest() {
		var described=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GatewayBuild.Describe()))!;
		Assert.False(string.IsNullOrWhiteSpace((string?)described["version"]));
		Assert.False(string.IsNullOrWhiteSpace((string?)described["build_label"]));
		Assert.Contains((string?)described["provenance"],new[]{"unpackaged","package_manifest"});
	}
}
