using System.Text.Json.Nodes;
using dgSpy.Extension.ToolWindows;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class McpActivityLogTests {
	static RpcRequest Request(string operation,JsonObject? arguments=null) =>
		new RpcRequest { Operation=operation, RequestId="req-1", Arguments=arguments ?? new JsonObject() };

	[Fact]
	public void Records_the_operation_its_arguments_and_its_result() {
		var log=new McpActivityLog();
		var request=Request("set_il_breakpoint",new JsonObject { ["module"]="Target.exe", ["method_token"]=100663297 });

		log.Record(request,RpcResponse.Success("req-1",new { breakpoint_id="bp-1" }),TimeSpan.FromMilliseconds(12.5));

		var entry=Assert.Single(log.Snapshot());
		Assert.Equal(1,entry.Sequence);
		Assert.Equal("set_il_breakpoint",entry.Operation);
		Assert.Equal("req-1",entry.RequestId);
		Assert.Equal("ok",entry.Status);
		Assert.False(entry.Failed);
		Assert.Equal(12.5,entry.DurationMs,3);
		Assert.Contains("Target.exe",entry.Arguments);
		Assert.Contains("100663297",entry.Arguments);
		Assert.Contains("bp-1",entry.Result);
	}

	[Fact]
	public void Records_the_error_code_and_message_and_no_result_on_failure() {
		var log=new McpActivityLog();

		log.Record(Request("evaluate"),RpcResponse.Failure("req-1","invalid_frame","Frame snapshot is stale."),TimeSpan.Zero);

		var entry=Assert.Single(log.Snapshot());
		Assert.True(entry.Failed);
		Assert.Equal("invalid_frame",entry.Status);
		Assert.Equal("Frame snapshot is stale.",entry.ErrorMessage);
		Assert.Equal("",entry.Result);
	}

	[Fact]
	public void Drops_the_oldest_entries_once_capacity_is_reached() {
		var log=new McpActivityLog(capacity:3);

		for(var index=0;index<5;index++)
			log.Record(Request("get_session_state"),RpcResponse.Success("req-1",new { }),TimeSpan.Zero);

		var entries=log.Snapshot();
		Assert.Equal(3,entries.Length);
		// Sequence numbers keep counting, so a truncated view still says which calls it is showing.
		Assert.Equal(new long[] { 3,4,5 },entries.Select(e => e.Sequence).ToArray());
	}

	[Fact]
	public void Truncates_a_result_too_large_to_be_worth_showing() {
		var log=new McpActivityLog();
		var huge=new string('a',200*1024);

		log.Record(Request("get_raw_module"),RpcResponse.Success("req-1",new { data=huge }),TimeSpan.Zero);

		var entry=Assert.Single(log.Snapshot());
		Assert.Contains("(truncated",entry.Result);
		// The point of the cap is that the entry costs a bounded amount, not that it is merely shorter.
		Assert.True(entry.Result.Length<32*1024,$"Result was {entry.Result.Length} chars.");
	}

	[Theory]
	[InlineData("gateway_heartbeat")]
	[InlineData("ping")]
	public void Ignores_transport_chatter_that_would_otherwise_flood_the_buffer(string operation) {
		var log=new McpActivityLog();

		log.Record(Request(operation),RpcResponse.Success("req-1",new { }),TimeSpan.Zero);

		Assert.Empty(log.Snapshot());
	}

	[Fact]
	public void Clear_empties_the_log_and_raises_its_event() {
		var log=new McpActivityLog();
		log.Record(Request("get_session_state"),RpcResponse.Success("req-1",new { }),TimeSpan.Zero);
		var cleared=false;
		log.Cleared+=() => cleared=true;

		log.Clear();

		Assert.Empty(log.Snapshot());
		Assert.True(cleared);
	}

	[Fact]
	public void Recorded_fires_with_the_entry_that_was_appended() {
		var log=new McpActivityLog();
		McpActivityEntry? seen=null;
		log.Recorded+=entry => seen=entry;

		log.Record(Request("continue"),RpcResponse.Success("req-1",new { }),TimeSpan.Zero);

		Assert.NotNull(seen);
		Assert.Equal("continue",seen!.Operation);
	}
}
