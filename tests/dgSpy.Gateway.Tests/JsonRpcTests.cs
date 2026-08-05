using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class JsonRpcTests {
	[Theory]
	[InlineData("0", "0")]
	[InlineData("1", "1")]
	[InlineData("\"request-1\"", "\"request-1\"")]
	[InlineData("null", "null")]
	public void Request_ids_survive_the_gateway_serializer_boundary(string requestId,string expectedResponseId) {
		var materialized=JsonRpcEnvelope.MaterializeId(JsonNode.Parse(requestId));
		var response=JsonSerializer.Serialize(new { jsonrpc="2.0",id=materialized,result=new {} });
		using var document=JsonDocument.Parse(response);

		Assert.Equal(expectedResponseId,document.RootElement.GetProperty("id").GetRawText());
	}

	[Fact]
	public async Task Accepted_notifications_have_no_json_body() {
		var context=new DefaultHttpContext();
		context.Response.Body=new MemoryStream();
		context.RequestServices=new ServiceCollection().AddLogging().BuildServiceProvider();

		await Results.Accepted().ExecuteAsync(context);

		Assert.Equal(StatusCodes.Status202Accepted,context.Response.StatusCode);
		Assert.Equal(0,context.Response.Body.Length);
	}
}
