using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace dgSpy.Protocol {
	public static class ProtocolJson {
		public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions {
			PropertyNameCaseInsensitive = false,
			UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode,
		};

		public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
		public static string Serialize<T>(T value, JsonSerializerOptions options) => JsonSerializer.Serialize(value, options);
		public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
		public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
		public static JsonObject ToObject<T>(T value) => ToNode(value)!.AsObject();
		public static T? FromNode<T>(JsonNode? value) => value is null ? default : value.Deserialize<T>(Options);
		public static JsonObject ParseObject(string json) => JsonNode.Parse(json)!.AsObject();
	}
}
