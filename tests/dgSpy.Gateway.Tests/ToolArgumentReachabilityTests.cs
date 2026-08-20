using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>An argument the extension reads and the Gateway never declares is unreachable: no MCP client
/// can send it, and the feature it belongs to is dead while every test still passes.
///
/// <para>This has happened twice, and both times the feature was reported done first. <c>payload</c> was
/// unreachable because <c>action_kind</c> is hard-enumerated. <c>app_domain_id</c> was unreachable for
/// the whole of the target environment contract's subslice 6, steps 1 to 3, and surfaced only when the feature was finally used
/// against a live IIS worker - the sheet recording it says plainly: "No test could catch it: nothing
/// drives the MCP schema against the extension's argument reading."</para>
///
/// <para>This is that test. It compares the exact declared surface - the schema the Gateway serves to
/// tools/list, not a copy of it - against every argument name read out of a request in the extension.
/// It deliberately does not attribute names to individual tools: matching a read to the operation it
/// belongs to needs real flow analysis, while the defect class is simply "declared nowhere", and the
/// weaker question catches both historical instances with no false alarms.</para></summary>
public sealed class ToolArgumentReachabilityTests {
	static string RepoRoot {
		get {
			for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
				if(File.Exists(Path.Combine(directory.FullName,"AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName,"dgSpy.Gateway"))) return directory.FullName;
			throw new InvalidOperationException("Could not locate the dgSpy repository root.");
		}
	}

	/// <summary>Names the extension reads out of a request it composed itself, rather than out of a
	/// caller's. They are internal wiring between two halves of one operation and are correctly absent
	/// from the MCP surface. Every entry needs a reason, because the alternative reading of "this name is
	/// not in the schema" is the defect this test exists to catch.</summary>
	static readonly Dictionary<string,string> InternalOnly=new(StringComparer.Ordinal) {
		["run_all_threads"]=
			"Written by the HookLab path into the run_atomic_action request it composes for itself "+
			"(RpcHost.HookLab.cs) and read by the evaluation handler. Whether a payload evaluation needs "+
			"every thread running is decided by the operation, not offered to the caller: a caller who "+
			"chose wrongly would deadlock or corrupt the payload handshake rather than tune anything.",
	};

	[Fact]
	public void Every_request_argument_the_extension_reads_is_declared_by_some_tool() {
		var declared=DeclaredArgumentNames();
		var undeclared=ExtensionArgumentReads()
			.Where(read=>!declared.Contains(read.Key) && !InternalOnly.ContainsKey(read.Key))
			.OrderBy(read=>read.Key,StringComparer.Ordinal)
			.ToArray();

		Assert.True(undeclared.Length==0,
			"These argument names are read by the extension and declared by no tool, so no MCP client can send them:\n"+
			String.Join("\n",undeclared.Select(read=>"  "+read.Key+"  read at "+String.Join(", ",read.Value)))+
			"\nDeclare them in ToolCatalog, or record them in InternalOnly with the reason they are not caller-supplied.");

		// The historical instance, pinned by name after the general check so that its own failure reads
		// as the actionable message above rather than as a bare missing-item assertion.
		Assert.Contains("app_domain_id",declared);
	}

	/// <summary>The allowlist is a list of exceptions, so an entry that stopped being an exception has to
	/// be noticed. A stale one would silently excuse a future real gap under the same name.</summary>
	[Fact]
	public void The_internal_only_allowlist_carries_no_stale_entries() {
		var reads=ExtensionArgumentReads();
		var declared=DeclaredArgumentNames();
		foreach(var entry in InternalOnly) {
			Assert.True(reads.ContainsKey(entry.Key),"InternalOnly names '"+entry.Key+"', which the extension no longer reads.");
			Assert.False(declared.Contains(entry.Key),"InternalOnly names '"+entry.Key+"', which is now declared by a tool and needs no exception.");
		}
	}

	/// <summary>Every property of every tool's input schema, taken from the object the Gateway actually
	/// serves rather than from a second copy of the list.</summary>
	static HashSet<string> DeclaredArgumentNames() {
		var names=new HashSet<string>(StringComparer.Ordinal);
		foreach(var tool in ProtocolJson.ToNode(ToolCatalog.All)!.AsArray()) {
			var properties=tool?["inputSchema"]?["properties"]?.AsObject();
			if(properties is null) continue;
			foreach(var property in properties) names.Add(property.Key);
		}
		Assert.True(names.Count>20,"The declared surface came back nearly empty, so this test would pass by measuring nothing.");
		return names;
	}

	/// <summary>Argument names read from a request in the extension, with the files they were read in so a
	/// failure names somewhere to look. Reads only: an assignment into an Arguments object is the
	/// extension composing a request, which is the opposite direction.</summary>
	static Dictionary<string,List<string>> ExtensionArgumentReads() {
		// Three shapes, and the third is why the self-checks below exist: the first version of this test
		// knew about the indexer and the helper family, and silently measured nothing for app_domain_id,
		// which is read through TryGetPropertyValue. A scraper that misses a shape passes.
		var indexer=new Regex("Arguments *\\[ *\"(?<name>[a-z_0-9]+)\" *\\] *(?<assignment>=[^=])?",RegexOptions.Compiled);
		var helper=new Regex("Arguments *, *\"(?<name>[a-z_0-9]+)\"",RegexOptions.Compiled);
		var probe=new Regex("Arguments *\\. *[A-Za-z]+ *\\( *\"(?<name>[a-z_0-9]+)\"",RegexOptions.Compiled);
		var reads=new Dictionary<string,List<string>>(StringComparer.Ordinal);
		var root=Path.Combine(RepoRoot,"Extensions","dgSpy.Extension");
		var sources=Directory.EnumerateFiles(root,"*.cs",SearchOption.AllDirectories)
			.Where(path=>!path.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar,StringComparison.Ordinal)
				&& !path.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar,StringComparison.Ordinal))
			.ToArray();
		Assert.True(sources.Length>10,"No extension sources were scanned, so this test would pass by measuring nothing.");
		foreach(var path in sources) {
			var text=File.ReadAllText(path);
			foreach(Match match in indexer.Matches(text))
				if(!match.Groups["assignment"].Success) Add(reads,match.Groups["name"].Value,path);
			foreach(Match match in helper.Matches(text)) Add(reads,match.Groups["name"].Value,path);
			foreach(Match match in probe.Matches(text)) Add(reads,match.Groups["name"].Value,path);
		}
		// Two names read through two different shapes, so a scraper that quietly stops matching one of
		// them fails here rather than reporting a clean surface it never looked at.
		Assert.Contains("app_domain_id",reads.Keys);
		Assert.Contains("session_id",reads.Keys);
		return reads;
	}

	static void Add(Dictionary<string,List<string>> reads,string name,string path) {
		if(!reads.TryGetValue(name,out var files)) reads[name]=files=new List<string>();
		var relative=Path.GetFileName(path);
		if(!files.Contains(relative)) files.Add(relative);
	}
}
