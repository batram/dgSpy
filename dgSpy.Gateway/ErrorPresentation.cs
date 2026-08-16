using System.Text.Json.Nodes;
using dgSpy.Protocol;

namespace dgSpy.Gateway;

public static class ErrorPresentation {
	public static JsonObject Structured(RpcError error,(string Cause,string Recovery,string Tool) guidance) => new JsonObject {
		["code"]=error.Code,["message"]=error.Message,["operation"]=error.Operation,["stage"]=error.Stage,
		["provider"]=error.Provider,["exception_type"]=error.ExceptionType,["native_error_code"]=error.NativeErrorCode,
		["hresult"]=error.HResult,["transient"]=error.Transient,["diagnostic_id"]=error.DiagnosticId,
		["likely_cause"]=guidance.Cause,["recovery_action"]=guidance.Recovery,["suggested_tool"]=guidance.Tool,
	};

	public static string Text(RpcError error,(string Cause,string Recovery,string Tool) guidance) =>
		error.Code+": "+error.Message+(error.DiagnosticId is null ? "" : " Diagnostic ID: "+error.DiagnosticId+".")+" Recovery: "+guidance.Recovery;
}
