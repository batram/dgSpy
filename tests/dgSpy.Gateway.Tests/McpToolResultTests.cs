using System.Text.Json.Nodes;
using dgSpy.Gateway;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class McpToolResultTests {
	[Fact]
	public void Object_result_keeps_its_existing_shape() {
		var structured=McpToolResult.StructuredContent(new { session_id="session-a" });

		Assert.Equal("session-a",(string?)structured["session_id"]);
		Assert.False(structured.ContainsKey("result"));
	}

	[Fact]
	public void Array_result_is_wrapped_in_an_object() {
		var structured=McpToolResult.StructuredContent(new[] { new { program_id="program-a" } });

		var result=Assert.IsType<JsonArray>(structured["result"]);
		Assert.Equal("program-a",(string?)result[0]?["program_id"]);
	}

	[Fact]
	public void Empty_array_result_is_wrapped_in_an_object() {
		var structured=McpToolResult.StructuredContent(System.Array.Empty<object>());

		Assert.Empty(Assert.IsType<JsonArray>(structured["result"]));
	}

	[Theory]
	[InlineData("ready")]
	[InlineData(42)]
	public void Scalar_result_is_wrapped_in_an_object(object value) {
		var structured=McpToolResult.StructuredContent(value);

		Assert.NotNull(structured["result"]);
		Assert.Equal(value.ToString(),structured["result"]!.ToString());
	}

	[Fact]
	public void Null_result_is_still_an_object() {
		var structured=McpToolResult.StructuredContent(null);

		Assert.True(structured.ContainsKey("result"));
		Assert.Null(structured["result"]);
	}
}
