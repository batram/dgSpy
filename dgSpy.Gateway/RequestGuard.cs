using System.Net;

namespace dgSpy.Gateway;

/// <summary>
/// Decides whether an MCP request may run. Loopback binding is not a trust boundary for an HTTP
/// endpoint: a browser can reach 127.0.0.1, and a simple cross-origin POST is not preflighted, so a
/// web page the user visits could otherwise drive the debugger. Two independent checks stop that —
/// the request must not come from a foreign origin, and it must carry the local secret.
/// </summary>
public static class RequestGuard {
	public const string TokenHeader = "X-dgSpy-Token";

	/// <summary>Returns null when the request may proceed, otherwise the reason to reject it.</summary>
	public static string? Reject(string? origin, string? presentedToken, string expectedToken, IPAddress? remoteAddress) {
		if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
			return $"dgSpy only serves loopback clients; refused {remoteAddress}.";
		// A browser always sends Origin on cross-origin requests. Non-browser clients send none, which
		// is allowed — they cannot be a drive-by page. Anything else must be loopback.
		if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
			return $"Origin '{origin}' is not a loopback origin.";
		if (string.IsNullOrEmpty(expectedToken))
			return "The gateway has no token configured; refusing to serve.";
		if (presentedToken != expectedToken)
			return $"Missing or invalid {TokenHeader} header.";
		return null;
	}

	public static bool IsLoopbackOrigin(string origin) {
		if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
			return false;
		if (uri.HostNameType == UriHostNameType.Dns)
			return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
		return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
	}
}
