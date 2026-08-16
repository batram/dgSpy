using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	sealed class ProgramDiscoveryException : Exception {
		public string? Provider { get; }
		public ProgramDiscoveryException(string? provider,Exception inner) : base("Attach provider discovery failed.",inner) => Provider=provider;
	}

	static class RpcFailure {
		public static RpcResponse FromException(RpcRequest request,Exception exception) {
			if (exception is RpcException rpc) return RpcResponse.Failure(request.RequestId,rpc.Code,rpc.Message);
			var discovery=exception as ProgramDiscoveryException;
			var source=discovery?.InnerException ?? exception;
			var code=discovery is null ? "internal_error" : "program_discovery_failed";
			var stage=discovery is null ? "dispatch" : "attach_provider";
			var message=discovery is null ? request.Operation+" failed unexpectedly." : "The selected attach provider could not enumerate programs.";
			var native=source as Win32Exception;
			var com=source as COMException;
			var error=new RpcError {
				Code=code,Message=message,Operation=request.Operation,Stage=stage,Provider=discovery?.Provider,
				ExceptionType=source.GetType().FullName,NativeErrorCode=native?.NativeErrorCode,
				HResult=com?.ErrorCode ?? source.HResult,Transient=source is Win32Exception || source is InvalidOperationException,
				DiagnosticId="rpc-"+request.RequestId,LocalDiagnostic=exception.ToString(),
			};
			return new RpcResponse { RequestId=request.RequestId,Error=error };
		}
	}
}
