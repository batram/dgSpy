namespace dgSpy.Gateway;

/// <summary>Pure Streamable HTTP version policy, kept outside Program.cs so compatibility cannot
/// silently regress when the transport changes. dgSpy is stateless at the MCP transport layer, so it
/// deliberately does not mint MCP-Session-Id values; debugger ownership remains in session_id tool
/// arguments.</summary>
public static class McpProtocol {
	public const string Current = "2025-11-25";
	public const string LegacyDefault = "2025-03-26";
	public const string VersionHeader = "MCP-Protocol-Version";
	public static readonly string[] Supported = { Current, "2025-06-18", LegacyDefault };

	public static string Negotiate(string? requested) =>
		Supported.Contains(requested, StringComparer.Ordinal) ? requested! : Current;

	public static bool IsValidRequestVersion(string? supplied) =>
		string.IsNullOrEmpty(supplied) || Supported.Contains(supplied, StringComparer.Ordinal);
}
