using dgSpy.Gateway;
using Xunit;

namespace dgSpy.Gateway.Tests;

public class McpProtocolTests {
	[Theory]
	[InlineData("2025-11-25")]
	[InlineData("2025-06-18")]
	[InlineData("2025-03-26")]
	public void Supported_client_version_is_echoed(string version) =>
		Assert.Equal(version,McpProtocol.Negotiate(version));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("2024-11-05")]
	public void Missing_or_unsupported_initialize_version_gets_current(string? version) =>
		Assert.Equal(McpProtocol.Current,McpProtocol.Negotiate(version));

	[Fact]
	public void Missing_subsequent_header_uses_the_legacy_default() =>
		Assert.True(McpProtocol.IsValidRequestVersion(null));

	[Fact]
	public void Unsupported_subsequent_header_is_rejected() =>
		Assert.False(McpProtocol.IsValidRequestVersion("tomorrow"));
}
