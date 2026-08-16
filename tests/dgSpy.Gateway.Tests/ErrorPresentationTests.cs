using dgSpy.Gateway;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class ErrorPresentationTests {
	[Fact] public void StructuredErrorPreservesSourceAndAddsGuidance() {
		var error=new RpcError { Code="program_discovery_failed",Message="failed",Operation="list_programs",Stage="attach_provider",Provider="DotNet",NativeErrorCode=998,DiagnosticId="rpc-abc" };
		var guidance=ToolCatalog.ErrorGuidance(error.Code);
		var node=ErrorPresentation.Structured(error,guidance);
		Assert.Equal("list_programs",(string?)node["operation"]);
		Assert.Equal(998,(int?)node["native_error_code"]);
		Assert.Equal("list_programs",(string?)node["suggested_tool"]);
		Assert.Contains("process_ids",(string?)node["recovery_action"]);
	}

	[Fact] public void TextIncludesDiagnosticIdAndActionableRecovery() {
		var error=new RpcError { Code="program_discovery_failed",Message="failed",DiagnosticId="rpc-abc" };
		var text=ErrorPresentation.Text(error,ToolCatalog.ErrorGuidance(error.Code));
		Assert.Contains("rpc-abc",text);
		Assert.Contains("process_ids",text);
	}
}
