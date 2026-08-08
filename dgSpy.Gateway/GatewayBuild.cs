using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace dgSpy.Gateway;

/// <summary>Identity of the Gateway build that is answering, and whether it agrees with the builds
/// around it.
///
/// list_hosts already names the build behind every debugger answer. Nothing named the build behind
/// the tool descriptions, schemas and response shapes -- and those come from here, not from the
/// host. Four separate agent runs once reported the same defects against an installed CLI two hours
/// behind the tree; every report was accurate about the build being driven and wrong about the
/// repository, and establishing that took longer than the fixes had. A stale tool description reads
/// exactly like a current one, which is what makes this worth reporting rather than assuming.
///
/// Everything here degrades to "unknown" instead of throwing. Diagnostics must never be the thing
/// that breaks diagnostics.</summary>
public static class GatewayBuild {
	static readonly Assembly GatewayAssembly = typeof(GatewayBuild).Assembly;
	public static string AssemblyPath { get; } = SafeLocation();
	/// <summary>Hash of the assembly this process loaded, taken once at startup. Compared later
	/// against the same path on disk, it is the only thing that can tell "this Gateway is the
	/// installed one" from "this Gateway was installed over while it kept running".</summary>
	public static string LoadedSha256 { get; } = SafeHash(AssemblyPath);
	/// <summary>Assembly mtime rather than a compile-time constant, so it is still right for a
	/// worktree build that was never packaged -- and directly comparable with the host's
	/// build_time_utc, which is derived the same way.</summary>
	public static DateTime? BuildTimeUtc { get; } = SafeBuildTime(AssemblyPath);
	/// <summary>Commit from the package manifest, read once at process start. Reading it per call
	/// would be worse than not reading it: a reinstall under a running Gateway would hand this
	/// process the new manifest, and it would then attribute a fresh commit to the old code it is
	/// still executing -- the same silent mislabelling this class exists to expose.</summary>
	public static string? BuildCommit { get; } = ReadPackagedCommit(AssemblyPath);
	public static bool BuildDirty { get; } = BuildCommit?.EndsWith("-dirty",StringComparison.Ordinal)==true;
	public static string BuildLabel { get; } = FormatBuildLabel(BuildTimeUtc,BuildCommit);
	public static string Version => $"{InformationalVersion()}+{Shorten(LoadedSha256)}";

	/// <summary>What the Gateway can say about itself. Emitted by get_started and doctor so a
	/// transcript carries the build near the top and a later bug report is either reproducible
	/// against a known tree or visibly is not.</summary>
	public static object Describe() => new {
		version=Version,
		build_label=BuildLabel,
		build_commit=BuildCommit,
		build_dirty=BuildDirty,
		build_time_utc=BuildTimeUtc,
		assembly_sha256=LoadedSha256,
		assembly_path=AssemblyPath,
		// An unpackaged build honestly has no commit anyone can check out. Saying so is not the same
		// as failing to find one.
		provenance=BuildCommit is null ? "unpackaged" : "package_manifest",
	};

	/// <summary>A registered host's build, reduced to the two things a comparison needs.</summary>
	internal readonly record struct HostBuild(string HostId,string? Commit,DateTime? BuiltUtc);

	/// <summary>Both skew comparisons, plus one flag worth scanning for.</summary>
	public static object Skew(object[] hosts) {
		var parsed=new List<HostBuild>();
		foreach(var item in hosts) {
			// Per host, because a single malformed field must not take get_started down with it. This is a
			// diagnostic; it does not get to be the thing that breaks diagnostics.
			try {
				var node=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(item));
				var hostId=(string?)node?["host_id"] ?? "unknown";
				var info=node?["host"];
				DateTime? built=null; try { built=(DateTime?)info?["build_time_utc"]; } catch { }
				parsed.Add(new HostBuild(hostId,(string?)info?["build_commit"],built));
			}
			catch { parsed.Add(new HostBuild("unknown",null,null)); }
		}
		var against=CompareHostBuilds(BuildCommit,BuildTimeUtc,parsed);
		var process=CompareProcessToDisk(LoadedSha256,SafeHash(AssemblyPath),AssemblyPath);
		return new {
			skewed=Flag(against,"skewed") || Flag(process,"stale"),
			gateway_vs_hosts=against,
			gateway_process_vs_disk=process,
		};
	}
	static bool Flag(object value,string property) => (bool?)System.Text.Json.JsonSerializer.SerializeToNode(value)![property]==true;

	/// <summary>Gateway commit against every registered host's. Pure so the boundaries that matter --
	/// one skewed host among several, an unpackaged side, a dirty tree -- are testable without a
	/// registry.
	///
	/// The Gateway has no git and cannot order two commits, so it never claims one side is "behind".
	/// It says they differ, and where both build times are known it says which is older, which is
	/// what it can actually prove.</summary>
	internal static object CompareHostBuilds(string? gatewayCommit,DateTime? gatewayBuilt,IReadOnlyList<HostBuild> hosts) {
		var compared=hosts.Select(host=>new {
			host_id=host.HostId,
			build_commit=host.Commit,
			comparison=Compare(gatewayCommit,host.Commit),
		}).ToArray();
		var differing=compared.Where(host=>host.comparison=="differs").ToArray();
		if(gatewayCommit is null)
			return new { known=false,skewed=false,gateway_commit=(string?)null,hosts=compared,
				detail="This Gateway is an unpackaged build with no commit to compare, so skew against the hosts cannot be determined. Its own assembly hash and build time still identify it.",
				recovery=(string?)null };
		if(differing.Length==0) {
			// A matching commit does not pin the tree when either side was built dirty. Say so, but do
			// not call it skew: failing every worktree build is how a real warning gets ignored.
			var dirty=BuildDirty || compared.Any(host=>host.comparison=="match_dirty");
			return new { known=true,skewed=false,gateway_commit=(string?)Shorten(gatewayCommit),hosts=compared,
				detail=dirty
					? "The Gateway and every host that can name a commit agree on it, but at least one side was built from a dirty tree, so the commit does not identify the exact code running."
					: "The Gateway and every host that can name a commit agree on it.",
				recovery=(string?)null };
		}
		var names=string.Join(", ",differing.Select(host=>$"{host.host_id} at {host.build_commit}"));
		var older=Older(gatewayBuilt,hosts.Where(host=>differing.Any(d=>d.host_id==host.HostId)).Select(host=>host.BuiltUtc).ToArray());
		return new { known=true,skewed=true,gateway_commit=(string?)Shorten(gatewayCommit),hosts=compared,
			detail=$"The Gateway is running {Shorten(gatewayCommit)} but {names}. The tool descriptions, schemas and response shapes come from the Gateway; the debugger answers come from the host. {older} Treat anything either side reports as belonging to its own build, not to one tree.",
			recovery=(string?)"Redeploy so both sides come from one commit: call launch_local_host to bring the hosts onto the installed payload, and reinstall dgSpy if the Gateway is the older side. Record both commits in any bug report written before that happens." };
	}
	/// <summary>Commits from the two sides are not formatted alike: a host truncates to eight
	/// characters and appends "-dirty", the package manifest carries the full forty. Compare on the
	/// shorter of the two prefixes rather than demanding equal strings, or every comparison reports
	/// skew that is not there.</summary>
	static string Compare(string? gateway,string? host) {
		if(gateway is null || host is null) return "unknown";
		var (gatewaySha,gatewayDirty)=Split(gateway); var (hostSha,hostDirty)=Split(host);
		var length=Math.Min(gatewaySha.Length,hostSha.Length);
		if(length==0) return "unknown";
		if(!gatewaySha.AsSpan(0,length).Equals(hostSha.AsSpan(0,length),StringComparison.OrdinalIgnoreCase)) return "differs";
		return gatewayDirty||hostDirty ? "match_dirty" : "match";
	}
	static (string Sha,bool Dirty) Split(string commit) =>
		commit.EndsWith("-dirty",StringComparison.Ordinal) ? (commit[..^6],true) : (commit,false);
	static string Older(DateTime? gateway,IReadOnlyList<DateTime?> hosts) {
		if(gateway is null || hosts.Count==0 || hosts.Any(time=>time is null)) return "Neither side's build time is known on both ends, so which one is older cannot be said.";
		var newestHost=hosts.Max()!.Value; var oldestHost=hosts.Min()!.Value;
		if(gateway.Value<oldestHost) return "The Gateway is the older build.";
		if(gateway.Value>newestHost) return "The Gateway is the newer build.";
		return "The build times do not separate them cleanly.";
	}

	/// <summary>Whether this process is still the build that is installed. This is the case that cost
	/// the most: dgSpy is reinstalled, the agent's MCP server keeps running out of the tree it loaded
	/// at startup, and every tool description it serves stays two hours behind with nothing to say
	/// so.</summary>
	internal static object CompareProcessToDisk(string? loadedSha,string? diskSha,string path) {
		if(loadedSha is null || diskSha is null || loadedSha=="unknown" || diskSha=="unknown")
			return new { known=false,stale=false,assembly_path=path,loaded_sha256=loadedSha,on_disk_sha256=diskSha,
				detail="The Gateway assembly could not be hashed, so whether this process matches the installed build is unknown.",recovery=(string?)null };
		if(string.Equals(loadedSha,diskSha,StringComparison.OrdinalIgnoreCase))
			return new { known=true,stale=false,assembly_path=path,loaded_sha256=(string?)loadedSha,on_disk_sha256=(string?)diskSha,
				detail="This Gateway process is the build installed at its own path.",recovery=(string?)null };
		return new { known=true,stale=true,assembly_path=path,loaded_sha256=(string?)loadedSha,on_disk_sha256=(string?)diskSha,
			detail="dgSpy was reinstalled after this Gateway process started. It is still serving the tool descriptions, schemas and response shapes it loaded at startup, which are not the ones now installed.",
			recovery=(string?)"Restart the MCP client so it reconnects to the installed build; an agent that spawned 'dgspy mcp' must be restarted, because stopping the Gateway alone leaves that process running." };
	}

	static string SafeLocation() { try { return GatewayAssembly.Location ?? ""; } catch { return ""; } }
	static string SafeHash(string path) {
		if(string.IsNullOrEmpty(path)) return "unknown";
		try {
			using var stream=File.OpenRead(path);
			return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}
		catch { return "unknown"; }
	}
	static DateTime? SafeBuildTime(string path) {
		if(string.IsNullOrEmpty(path)) return null;
		try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
		catch { return null; }
	}
	/// <summary>The packaging manifest sits at the install root, two directories above cli\bin. Walking
	/// up instead of hardcoding that keeps this working if the layout shifts, and finding nothing is
	/// the normal case for a build run straight out of the repository. A manifest without a commit does
	/// not stop the walk: the remote-host bundle writes a manifest.json of its own shape, and stopping
	/// there would report "no commit" for a package that has one.</summary>
	static string? ReadPackagedCommit(string assemblyPath) {
		if(string.IsNullOrEmpty(assemblyPath)) return null;
		try {
			var dir=Path.GetDirectoryName(assemblyPath);
			for(var level=0;level<6 && !string.IsNullOrEmpty(dir);level++,dir=Path.GetDirectoryName(dir)) {
				var manifest=Path.Combine(dir!,"manifest.json");
				if(!File.Exists(manifest)) continue;
				JsonNode? root; try { root=JsonNode.Parse(File.ReadAllText(manifest)); } catch { continue; }
				var sha=(string?)root?["git_commit"];
				if(string.IsNullOrEmpty(sha)) continue;
				// A dirty build cannot be checked out again, so naming only the commit would name a tree
				// that never existed.
				return (bool?)root?["git_dirty"]==true ? sha+"-dirty" : sha;
			}
			return null;
		}
		catch { return null; }
	}
	static string Shorten(string commit) {
		var (sha,dirty)=Split(commit);
		var shortened=sha[..Math.Min(12,sha.Length)];
		return dirty ? shortened+"-dirty" : shortened;
	}
	// Local time on purpose: this is compared against "when did I hit build", which happened on this
	// machine's clock.
	static string FormatBuildLabel(DateTime? utc,string? commit) {
		var shortened=commit is null ? null : Shorten(commit);
		if(utc is null) return shortened ?? "unknown";
		var stamp=utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
		return shortened is null ? stamp : $"{stamp} ({shortened})";
	}
	static string InformationalVersion() =>
		GatewayAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? GatewayAssembly.GetName().Version?.ToString() ?? "unknown";
}
