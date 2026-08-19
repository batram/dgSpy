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

	// An opaque internal_error with only a diagnostic ID is a dead end when the host that recorded it is
	// a remote machine, so the exception identity must survive into the text content.
	[Fact] public void TextNamesTheExceptionBehindAnInternalError() {
		var error=new RpcError { Code="internal_error",Message="initialize_hooklab failed unexpectedly.",Operation="initialize_hooklab",ExceptionType="System.InvalidOperationException",HResult=-2146233079,DiagnosticId="rpc-abc" };
		var text=ErrorPresentation.Text(error,ToolCatalog.ErrorGuidance(error.Code));
		Assert.Contains("System.InvalidOperationException",text);
		Assert.Contains("0x80131509",text);
		Assert.Contains("rpc-abc",text);
	}

	[Fact] public void TextPrefersTheWin32CodeAndStaysQuietWithoutAnException() {
		var native=ErrorPresentation.Text(new RpcError { Code="internal_error",Message="m",ExceptionType="System.ComponentModel.Win32Exception",NativeErrorCode=5,HResult=-2147467259 },ToolCatalog.ErrorGuidance("internal_error"));
		Assert.Contains("Win32 5",native);
		Assert.DoesNotContain("HRESULT",native);
		Assert.DoesNotContain("Exception:",ErrorPresentation.Text(new RpcError { Code="stale_state",Message="m" },ToolCatalog.ErrorGuidance("stale_state")));
	}
}
