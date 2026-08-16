using System.ComponentModel;
using dgSpy.Protocol;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class RpcFailureTests {
	[Fact] public void DiscoveryFailurePreservesSafeTypedProvenanceAndLocalStack() {
		var request=new RpcRequest { RequestId="abc",Operation="list_programs" };
		var response=RpcFailure.FromException(request,new ProgramDiscoveryException("DotNet",new Win32Exception(998)));
		var error=Assert.IsType<RpcError>(response.Error);
		Assert.Equal("program_discovery_failed",error.Code);
		Assert.Equal("list_programs",error.Operation);
		Assert.Equal("attach_provider",error.Stage);
		Assert.Equal("DotNet",error.Provider);
		Assert.Equal(typeof(Win32Exception).FullName,error.ExceptionType);
		Assert.Equal(998,error.NativeErrorCode);
		Assert.True(error.Transient);
		Assert.Equal("rpc-abc",error.DiagnosticId);
		Assert.Contains("Win32Exception",error.LocalDiagnostic);
		Assert.DoesNotContain("Invalid access",error.Message);
	}

	[Fact] public void UnknownFailureDoesNotExposeRawExceptionMessage() {
		var request=new RpcRequest { RequestId="def",Operation="evaluate" };
		var response=RpcFailure.FromException(request,new Exception("secret raw detail"));
		Assert.Equal("internal_error",response.Error!.Code);
		Assert.Equal("evaluate failed unexpectedly.",response.Error.Message);
		Assert.DoesNotContain("secret raw detail",response.Error.Message);
		Assert.Contains("secret raw detail",response.Error.LocalDiagnostic);
	}

	[Fact] public void ExpectedRpcExceptionKeepsItsStableContract() {
		var response=RpcFailure.FromException(new RpcRequest { RequestId="ghi",Operation="attach" },new RpcException("stale_program","refresh"));
		Assert.Equal("stale_program",response.Error!.Code);
		Assert.Equal("refresh",response.Error.Message);
		Assert.Null(response.Error.DiagnosticId);
	}
}
