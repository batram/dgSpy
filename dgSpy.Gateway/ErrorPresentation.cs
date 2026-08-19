using System.Globalization;
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

	/// <summary>Most MCP clients render only this text and never open structuredContent, so the exception
	/// identity has to be here. Without it an <c>internal_error</c> reaches the caller as nothing but a
	/// diagnostic ID pointing at a dnSpy Activity window that, for a remote host, is on someone else's
	/// machine - a dead end. The exception *type* and its numeric codes are safe to name; the raw message
	/// stays behind in LocalDiagnostic, which is the boundary RpcFailure deliberately draws.</summary>
	public static string Text(RpcError error,(string Cause,string Recovery,string Tool) guidance) =>
		error.Code+": "+error.Message+Identity(error)+(error.DiagnosticId is null ? "" : " Diagnostic ID: "+error.DiagnosticId+".")+" Recovery: "+guidance.Recovery;

	static string Identity(RpcError error) {
		if (error.ExceptionType is null) return "";
		var text=" Exception: "+error.ExceptionType;
		if (error.NativeErrorCode is not null) text+=" (Win32 "+error.NativeErrorCode.Value.ToString(CultureInfo.InvariantCulture)+")";
		else if (error.HResult is not null && error.HResult.Value!=0) text+=" (HRESULT 0x"+error.HResult.Value.ToString("X8",CultureInfo.InvariantCulture)+")";
		return text+".";
	}
}
