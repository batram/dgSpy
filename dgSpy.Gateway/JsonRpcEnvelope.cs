using System.Text.Json.Nodes;

internal static class JsonRpcEnvelope {
	public static object? MaterializeId(JsonNode? id) {
		if (id is not JsonValue value) return null;
		if (value.TryGetValue<long>(out var integer)) return integer;
		if (value.TryGetValue<string>(out var text)) return text;
		if (value.TryGetValue<double>(out var number)) return number;
		return null;
	}
}
