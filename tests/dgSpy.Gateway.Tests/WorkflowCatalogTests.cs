using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class WorkflowCatalogTests {
	static readonly string[] RequiredTopics={ "setup","local_deployment","remote_deployment","attach","discovery","stepping","recovery","shutdown","safe_evaluation","raw_memory","object_ids","static_analysis","breakpoint_configuration","cleanup" };
	static string ToolName(object tool) => (string)tool.GetType().GetProperty("name",BindingFlags.Public|BindingFlags.Instance)!.GetValue(tool)!;

	[Fact]
	public void Topic_keys_are_unique_and_required_topics_exist() {
		Assert.Equal(WorkflowCatalog.Topics.Count,WorkflowCatalog.Topics.Select(topic=>topic.Topic).Distinct(StringComparer.Ordinal).Count());
		foreach(var required in RequiredTopics) Assert.Contains(WorkflowCatalog.Topics,topic=>topic.Topic==required);
	}

	[Fact]
	public void Every_recommended_tool_exists_in_tool_catalog() {
		var toolNames=ToolCatalog.All.Select(ToolName).ToHashSet(StringComparer.Ordinal);
		foreach(var topic in WorkflowCatalog.Topics)
			foreach(var tool in topic.Tools) Assert.Contains(tool,toolNames);
	}

	[Fact]
	public void Focused_lookup_and_compact_rendering_share_definition_instances() {
		var compact=WorkflowCatalog.RenderCompactCatalog();
		Assert.Same(WorkflowCatalog.Topics,compact.Topics);
		foreach(var topic in compact.Topics) Assert.Same(topic,WorkflowCatalog.Lookup(topic.Topic));
		var json=JsonSerializer.Serialize(compact);
		Assert.Contains("\"topic\":\"safe_evaluation\"",json,StringComparison.Ordinal);
		Assert.Contains("\"tools\":[\"list_threads\",\"get_callstack\",\"evaluate\"",json,StringComparison.Ordinal);
	}

	[Fact]
	public void Unknown_and_omitted_topics_deliberately_return_setup() {
		var setup=WorkflowCatalog.Lookup("setup");
		Assert.Same(setup,WorkflowCatalog.Lookup(null));
		Assert.Same(setup,WorkflowCatalog.Lookup("not_a_topic"));
	}

	[Fact]
	public void Critical_detach_and_cursor_ownership_guidance_remains_reachable() {
		Assert.Contains("caller owns the event cursor",WorkflowCatalog.Lookup("stepping").Guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("retry safe detach without closing dnSpy",WorkflowCatalog.Lookup("shutdown").Guidance,StringComparison.Ordinal);
		Assert.Contains("terminate destroys the target",WorkflowCatalog.Lookup("cleanup").Guidance,StringComparison.Ordinal);
	}

	[Fact]
	public void Remote_deployment_guidance_does_not_invent_endpoint_questions() {
		var guidance=WorkflowCatalog.Lookup("remote_deployment").Guidance;
		Assert.Contains("ask only for values not already supplied",guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("never includes a URL scheme or port",guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("disconnected or unavailable",guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("does not block packaging",guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("dnSpy.exe",guidance,StringComparison.Ordinal);
		Assert.Contains("connects automatically",guidance,StringComparison.OrdinalIgnoreCase);
		Assert.Contains("optional",guidance,StringComparison.OrdinalIgnoreCase);
	}
}
