using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The comparison is exercised as a pure function on purpose. The cases that matter -- one
/// skewed host among several, an unpackaged side, two commit formats that are equal, a dirty tree --
/// are all reachable here and none of them is reachable from a live registry on demand.</summary>
public sealed class GatewayBuildTests {
	const string ThisMachine="GATEWAY-BOX";
	static JsonNode Compare(string? gatewayCommit,DateTime? gatewayBuilt,params GatewayBuild.HostBuild[] hosts) =>
		JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(GatewayBuild.CompareHostBuilds(gatewayCommit,gatewayBuilt,(IReadOnlyList<GatewayBuild.HostBuild>)hosts,ThisMachine)))!;
	static readonly DateTime Noon=new(2026,8,8,12,22,0,DateTimeKind.Utc);
	static readonly DateTime Afternoon=new(2026,8,8,14,14,0,DateTimeKind.Utc);

	[Fact]
	public void Differing_commits_are_skew_and_the_older_side_is_named_for_a_host_on_this_machine() {
		var result=Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,new GatewayBuild.HostBuild("local","26ccdd25",Noon,ThisMachine));
		Assert.True((bool?)result["skewed"]); Assert.True((bool?)result["known"]);
		Assert.Equal("differs",(string?)result["hosts"]![0]!["comparison"]);
		Assert.Equal("bf5ed03551ff",(string?)result["gateway_commit"]);
		Assert.Contains("newer",(string?)result["detail"]!);
		Assert.NotNull((string?)result["recovery"]);
	}

	/// <summary>A build time is a file timestamp read on the machine holding the file, so ordering the
	/// Gateway's against a remote host's compares two clocks that nothing keeps in agreement.
	///
	/// Measured on a lab VM sitting four years in the past: a host carrying code built minutes earlier
	/// reported 2022, which the old comparison stated as "the Gateway is the newer build" - with a
	/// recovery line telling the operator to bring the host onto the Gateway's payload, exactly
	/// backwards, and produced precisely when someone is trying to work out which side is stale.</summary>
	[Fact]
	public void A_remote_hosts_build_time_is_never_ordered_against_the_gateways() {
		var hostInThePast=new GatewayBuild.HostBuild("winagain-iis","26ccdd25",new DateTime(2022,8,14,0,59,0,DateTimeKind.Utc),"WIN-12SS5R8D4UO");
		var detail=(string?)Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,hostInThePast)["detail"]!;
		Assert.DoesNotContain("the Gateway is the newer build",detail,StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("the Gateway is the older build",detail,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("another machine's clock",detail,StringComparison.Ordinal);
		Assert.Contains("compare commits, not times",detail,StringComparison.Ordinal);
		Assert.Contains("winagain-iis on WIN-12SS5R8D4UO",detail,StringComparison.Ordinal);
	}

	/// <summary>A host that never named its machine is treated as remote. Unknown is not "ours": the
	/// whole point is to claim an ordering only where one clock demonstrably produced both timestamps.</summary>
	[Fact]
	public void A_host_that_names_no_machine_is_not_assumed_local() {
		var detail=(string?)Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,new GatewayBuild.HostBuild("nameless","26ccdd25",Noon))["detail"]!;
		Assert.Contains("no two of these build times share a clock",detail,StringComparison.Ordinal);
		Assert.Contains("an unnamed machine",detail,StringComparison.Ordinal);
	}

	/// <summary>Mixed: the local host is ordered, the remote one is not, and both facts are reported
	/// rather than the weaker one silencing the stronger.</summary>
	[Fact]
	public void A_local_and_a_remote_host_are_reported_differently_in_one_answer() {
		var detail=(string?)Compare("bf5ed03551ff00112233445566778899aabbccdd",Afternoon,
			new GatewayBuild.HostBuild("local","26ccdd25",Noon,ThisMachine),
			new GatewayBuild.HostBuild("winagain-iis","77aabb99",new DateTime(2022,8,14,0,59,0,DateTimeKind.Utc),"WIN-12SS5R8D4UO"))["detail"]!;
		Assert.Contains("Among the hosts on this machine, the Gateway is the newer build.",detail,StringComparison.Ordinal);
		Assert.Contains("winagain-iis on WIN-12SS5R8D4UO is remote",detail,StringComparison.Ordinal);
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
