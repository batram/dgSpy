using System.Reflection;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

/// <summary>The gateway used to carry its own hardcoded per-tool deadline table, which is how a
/// successful 10 s attach once got abandoned by an 8 s caller deadline. The extension now advertises its
/// bounds and the gateway derives deadlines from them; these tests are what keeps the two from drifting
/// apart again.</summary>
public sealed class ToolCatalogTests {
	static string Name(object tool) => (string)tool.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance)!.GetValue(tool)!;
	public static TheoryData<string> ToolNames {
		get { var data=new TheoryData<string>(); foreach (var tool in ToolCatalog.All) data.Add(Name(tool)); return data; }
	}

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_advertised_tool_is_an_operation_the_extension_implements(string tool) =>
		Assert.True(CapabilityCatalog.IsKnownOperation(tool), $"tools/list advertises '{tool}', which is not in CapabilityCatalog.Operations.");

	[Theory]
	[MemberData(nameof(ToolNames))]
	public void Every_gateway_deadline_outlasts_the_extension_bound(string tool) {
		var bound=CapabilityCatalog.BoundMs(tool);

		Assert.True(ToolCatalog.DeadlineSeconds(tool)*1000 > bound, $"'{tool}' deadline {ToolCatalog.DeadlineSeconds(tool)}s does not outlast its {bound}ms bound.");
	}

	[Fact]
	public void An_unknown_tool_still_gets_a_usable_deadline() => Assert.Equal(8, ToolCatalog.DeadlineSeconds("not_a_tool"));

	[Fact]
	public void The_slowest_operation_keeps_its_full_connection_timeout() =>
		Assert.True(ToolCatalog.DeadlineSeconds("attach_endpoint")*1000 > CapabilityCatalog.Limits.MaxConnectionTimeoutMs,
			"attach_endpoint must outlast the largest connection timeout a caller can ask for.");
}
