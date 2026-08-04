using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Metadata;
using Newtonsoft.Json.Linq;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		const int MaxMemoryBytes=65536;

		async Task<MemoryResult> ReadMemoryAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var address=UInt64Argument(req,"address");
			var length=(int?)req.Arguments["length"] ?? throw new RpcException("invalid_arguments","length is required.");
			if (length<1 || length>MaxMemoryBytes) throw new RpcException("invalid_arguments",$"length must be between 1 and {MaxMemoryBytes}.");
			return await OnDebuggerAsync(()=>{
				var process=SelectProcess(req);
				var bytes=process.ReadMemory(address,length);
				return new MemoryResult { Address=address,Length=bytes.Length,DataBase64=Convert.ToBase64String(bytes),Capability="memory_access" };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<MemoryResult> WriteMemoryAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var address=UInt64Argument(req,"address");
			var encoded=(string?)req.Arguments["data_base64"];
			if (string.IsNullOrEmpty(encoded)) throw new RpcException("invalid_arguments","data_base64 is required.");
			byte[] bytes; try { bytes=Convert.FromBase64String(encoded!); } catch(FormatException) { throw new RpcException("invalid_arguments","data_base64 is not valid base64."); }
			if (bytes.Length<1 || bytes.Length>MaxMemoryBytes) throw new RpcException("invalid_arguments",$"data must contain between 1 and {MaxMemoryBytes} bytes.");
			AuditMutation(req.Operation,$"address=0x{address:X} length={bytes.Length}");
			return await OnDebuggerAsync(()=>{
				SelectProcess(req).WriteMemory(address,bytes);
				return new MemoryResult { Address=address,Length=bytes.Length,Written=true,CausesSideEffects=true,Capability="memory_access" };
			},cancellationToken).ConfigureAwait(false);
		}

		dnSpy.Contracts.Debugger.DbgProcess SelectProcess(RpcRequest req) {
			var processId=(int?)req.Arguments["process_id"];
			var processes=manager.Processes.ToArray();
			if (processId is null && processes.Length==1) return processes[0];
			return processes.FirstOrDefault(p=>p.Id==processId)
				?? throw new RpcException(processId is null ? "ambiguous_target" : "process_not_found",processId is null ? "More than one process is active; pass process_id." : $"Process {processId} is not active.");
		}

		async Task<SessionState> PauseProcessAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req);
			await targetControl.WaitAsync(token).ConfigureAwait(false);
			dnSpy.Contracts.Debugger.DbgProcess? process=null;
			var restoreBreakAll=false;
			try {
				await OnDebuggerAsync(()=>{
					CheckVersion(req);
					process=SelectProcess(req);
					// dnSpy's UI default intentionally cascades a break to every process. An explicit
					// process_id has the opposite contract, so suppress that setting for this request.
					if (manager.Processes.Length>1 && debuggerSettings.BreakAllProcesses) {
						restoreBreakAll=true;
						debuggerSettings.BreakAllProcesses=false;
					}
					process.Break();
					return true;
				},token).ConfigureAwait(false);
				await WaitForDebuggerAsync(()=>!manager.IsDebugging || process is null || !process.IsRunning,token).ConfigureAwait(false);
				return await OnDebuggerAsync(State,token).ConfigureAwait(false);
			}
			finally {
				try {
					if (restoreBreakAll) await OnDebuggerAsync(()=>{ debuggerSettings.BreakAllProcesses=true; return true; }).ConfigureAwait(false);
				}
				finally { targetControl.Release(); }
			}
		}

		async Task<SessionState> ContinueProcessAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req);
			dnSpy.Contracts.Debugger.DbgProcess? process=null;
			await OnDebuggerAsync(()=>{ CheckVersion(req); process=SelectProcess(req); process.Run(); return true; },token).ConfigureAwait(false);
			await WaitForDebuggerAsync(()=>!manager.IsDebugging || process is null || process.IsRunning,token).ConfigureAwait(false);
			return await OnDebuggerAsync(State,token).ConfigureAwait(false);
		}

		static ulong UInt64Argument(RpcRequest req,string name) {
			var token=req.Arguments[name] ?? throw new RpcException("invalid_arguments",name+" is required.");
			try { return Convert.ToUInt64(((JValue)token).Value,CultureInfo.InvariantCulture); }
			catch(Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException) {
				throw new RpcException("invalid_arguments",name+" must be an unsigned 64-bit integer.");
			}
		}

		async Task<JObject> GetDisassemblyAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var mode=(string?)req.Arguments["mode"] ?? "native";
			if (mode=="managed") {
				var body=await GetIlAsync(req,cancellationToken).ConfigureAwait(false);
				return JObject.FromObject(new { mode="managed",capability="managed_il",body });
			}
			if (mode!="native") throw new RpcException("invalid_argument","mode must be native or managed.");
			var captured=await CaptureFrameAsync(req,cancellationToken).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>{
				var runtime=captured.Frame.Runtime.InternalRuntime as IDbgDotNetRuntime;
				if (runtime is null || (runtime.Features & DbgDotNetRuntimeFeatures.NativeMethodBodies)==0)
					throw new RpcException("capability_unsupported","native_disassembly is not supported by this runtime.");
				if (!runtime.TryGetNativeCode(captured.Frame,out var code)) throw new RpcException("capability_unavailable","The runtime supports native method bodies, but this frame has no JIT-compiled native body.");
				return JObject.FromObject(new { mode="native",capability="native_disassembly",kind=code.Kind.ToString(),optimization=code.Optimization.ToString(),method=code.MethodName,module=code.ModuleName,blocks=code.Blocks.Select(b=>new { kind=b.Kind.ToString(),address=b.Address,il_offset=b.ILOffset,data_base64=Convert.ToBase64String(b.Code.Array!,b.Code.Offset,b.Code.Count) }).ToArray() });
			},cancellationToken).ConfigureAwait(false);
		}

		Task<JObject> GetRegistersAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			throw new RpcException("capability_unsupported","registers are not exposed by dnSpy's public debugger contracts on this host.");
		}

		async Task<MutationResult> SetInstructionPointerAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var module=(string?)req.Arguments["module"];
			var token=(uint?)req.Arguments["method_token"] ?? throw new RpcException("invalid_arguments","method_token is required.");
			var offset=(uint?)req.Arguments["il_offset"] ?? throw new RpcException("invalid_arguments","il_offset is required.");
			if (string.IsNullOrWhiteSpace(module)) throw new RpcException("invalid_arguments","module is required.");
			var captured=await CaptureFrameAsync(req,cancellationToken).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>{
				if (!string.Equals(captured.Frame.Module?.Filename,module,StringComparison.OrdinalIgnoreCase) || captured.Frame.FunctionToken!=token)
					throw new RpcException("invalid_location","The target must be in the selected frame's current method. Refresh get_frame and pass that module and method_token.");
				var location=locations.Create(ModuleId.Create(module!),token,offset);
				if (!captured.Frame.Thread.CanSetIP(location)) { location.Close(); throw new RpcException("capability_unavailable","The engine rejected this instruction-pointer location for the selected frame."); }
				var auditId=AuditMutation(req.Operation,$"module={module} token=0x{token:X8} il_offset=0x{offset:X}");
				captured.Frame.Thread.SetIP(location);
				location.Close();
				return new MutationResult { Completed=true,CausesSideEffects=true,AuditId=auditId,Capability="set_instruction_pointer" };
			},cancellationToken).ConfigureAwait(false);
		}
	}
}
