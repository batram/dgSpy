using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger.DotNet.Disassembly;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Mono;
using dnSpy.Contracts.Metadata;
using System.Text.Json.Nodes;

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
			var selected=processes.FirstOrDefault(p=>p.Id==processId);
			if (selected is not null) return selected;
			if (processId is not null) throw new RpcException("process_not_found",$"Process {processId} is not active.");
			// Zero and many are opposite problems and used to share one message. "More than one process
			// is active; pass process_id" sent a caller looking for the second process when the real
			// answer was that its target had exited and there was none -- a debugging cycle spent on a
			// process_id that could never have existed.
			if (processes.Length==0) throw new RpcException("no_active_process","No process is active in this session; the target exited or was detached.");
			throw new RpcException("ambiguous_target",$"{processes.Length} processes are active ({string.Join(", ",processes.Select(p=>p.Id))}); pass process_id.");
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
			long executionBefore;
			lock(sync) executionBefore=executionVersion;
			await OnDebuggerAsync(()=>{ CheckVersion(req); process=SelectProcess(req); process.Run(); return true; },token).ConfigureAwait(false);
			bool Applied() {
				long current; lock(sync) current=executionVersion;
				return !manager.IsDebugging || process is null || process.IsRunning || current!=executionBefore;
			}
			await WaitForDebuggerAsync(Applied,token).ConfigureAwait(false);
			var applied=await OnDebuggerAsync(Applied,token).ConfigureAwait(false);
			if (!applied) {
				// CorDebug can decline the first Run while it is unwinding an exception callback. Retrying is
				// safe only after the full bounded wait proves that no continued/stopped event occurred and the
				// selected process is still paused. Never repeat a mutation whose execution change was observed.
				await OnDebuggerAsync(()=>{ if (!Applied()) process!.Run(); return true; },token).ConfigureAwait(false);
				await WaitForDebuggerAsync(Applied,token).ConfigureAwait(false);
				applied=await OnDebuggerAsync(Applied,token).ConfigureAwait(false);
			}
			if (!applied) throw new RpcException("continue_timed_out","CorDebug did not resume the selected process after two bounded attempts. The target remains paused; read get_session_state before deciding whether to retry.");
			return await OnDebuggerAsync(State,token).ConfigureAwait(false);
		}

		static ulong UInt64Argument(RpcRequest req,string name) {
			var token=req.Arguments[name] ?? throw new RpcException("invalid_arguments",name+" is required.");
			if (token is JsonValue value) {
				if (value.TryGetValue<ulong>(out var unsigned)) return unsigned;
				if (value.TryGetValue<long>(out var signed) && signed>=0) return (ulong)signed;
				if (value.TryGetValue<string>(out var text) && ulong.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out unsigned)) return unsigned;
			}
			throw new RpcException("invalid_arguments",name+" must be an unsigned 64-bit integer.");
		}

		async Task<JsonObject> GetDisassemblyAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var mode=(string?)req.Arguments["mode"] ?? "native";
			if (mode=="managed") {
				var body=await GetIlAsync(req,cancellationToken).ConfigureAwait(false);
				return ProtocolJson.ToObject(new { mode="managed",capability="managed_il",body });
			}
			if (mode!="native") throw new RpcException("invalid_argument","mode must be native or managed.");
			var captured=await CaptureFrameAsync(req,cancellationToken).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>{
				var runtime=captured.Frame.Runtime.InternalRuntime as IDbgDotNetRuntime;
				if (runtime is null || (runtime.Features & DbgDotNetRuntimeFeatures.NativeMethodBodies)==0)
					throw new RpcException("capability_unsupported","native_disassembly is not supported by this runtime.");
				if (!runtime.TryGetNativeCode(captured.Frame,out var code)) throw new RpcException("capability_unavailable","The runtime supports native method bodies, but this frame has no JIT-compiled native body.");
				return ProtocolJson.ToObject(new { mode="native",capability="native_disassembly",kind=code.Kind.ToString(),optimization=code.Optimization.ToString(),method=code.MethodName,module=code.ModuleName,blocks=code.Blocks.Select(b=>new { kind=b.Kind.ToString(),address=b.Address,il_offset=b.ILOffset,data_base64=Convert.ToBase64String(b.Code.Array!,b.Code.Offset,b.Code.Count) }).ToArray() });
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<JsonObject> GetRegistersAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req); var captured=await CaptureFrameAsync(req,cancellationToken).ConfigureAwait(false);
			return await OnDebuggerAsync(()=>{
				var runtime=captured.Frame.Runtime.InternalRuntime as IDbgDotNetRuntime;
				var isCorDebug=runtime is not null && (runtime.Features & DbgDotNetRuntimeFeatures.NativeMethodBodies)!=0;
				var isMonoSystemThread=captured.Frame.Thread.TryGetData<DbgMonoThreadInfo>(out var mono) && mono.HasSystemThreadId;
				if (!isCorDebug && !isMonoSystemThread)
					throw new RpcException("capability_unsupported",mono is null ? "This runtime does not expose an OS thread context." : $"Mono soft-debugger protocol {mono.ProtocolMajor}.{mono.ProtocolMinor} does not expose system thread ids; version 2.3 or newer is required.");
				var context=NativeRegisterReader.ReadX64(captured.Frame.Thread.Id,captured.Frame.Thread.Process.Id);
				var (relation,note)=ClassifyContext(captured,runtime,context.Rip);
				return ProtocolJson.ToObject(new { architecture="x64",thread_id=ThreadId(captured.Frame.Thread),frame_index=captured.Info.FrameIndex,frame_relation=relation,note,registers=context.Registers });
			},cancellationToken).ConfigureAwait(false);
		}

		// What these registers actually describe.
		//
		// This is not the managed frame's context and usually cannot be. At a managed stop the runtime
		// suspends the debuggee thread inside its own stop machinery, so the OS context Windows preserved
		// is a wait deep in ntdll -- measured at a method-entry breakpoint on an instance method of a
		// /debug:full /optimize- x64 assembly: rip in ntdll, rcx a wait handle rather than `this`, rbp a
		// 0xFFFFFFFF sentinel rather than a frame base. Every one of those numbers is a true reading of
		// the machine and a wrong answer to "what were this frame's registers", and nothing in the values
		// themselves lets a caller tell the two apart. So the answer says which it is.
		//
		// The test is exact rather than heuristic: rip either falls inside the JIT-compiled body of the
		// selected frame's method or it does not. A non-leaf frame can never match -- rip belongs to the
		// leaf -- so it is answered without asking the engine anything.
		static (string relation,string note) ClassifyContext(CapturedFrame captured,IDbgDotNetRuntime? runtime,ulong rip) {
			const string Caveat="Registers describe the OS thread, not this managed frame: do not read `this` from rcx or a frame base from rbp. Use get_frame, evaluate or a value's `address` for frame data.";
			if (captured.Info.FrameIndex!=0)
				return ("unrelated",$"The OS thread context belongs to the leaf frame, and frame_index is {captured.Info.FrameIndex}. {Caveat}");
			if (runtime is null || (runtime.Features & DbgDotNetRuntimeFeatures.NativeMethodBodies)==0)
				return ("unknown",$"This runtime cannot report the frame's native code range, so whether the context belongs to this frame could not be established. {Caveat}");
			DbgDotNetNativeCode code;
			try { if (!runtime.TryGetNativeCode(captured.Frame,out code)) return ("unknown",$"The frame has no JIT-compiled native body to compare rip against. {Caveat}"); }
			catch { return ("unknown",$"The frame's native code range could not be read. {Caveat}"); }
			foreach (var block in code.Blocks) {
				if (rip>=block.Address && rip<block.Address+(ulong)block.Code.Count)
					return ("matched","rip is inside this frame's JIT-compiled body, so the context describes this frame.");
			}
			return ("unrelated",$"rip is outside this frame's JIT-compiled body: the runtime parked the thread elsewhere to report the stop, which is the normal case at a managed breakpoint. {Caveat}");
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
