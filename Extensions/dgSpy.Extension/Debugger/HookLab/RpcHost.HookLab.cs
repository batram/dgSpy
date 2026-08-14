using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Extension.Debugger.AtomicActions;
using dgSpy.Extension.ToolWindows;
using dgSpy.Protocol;
using HookLab.Contracts;
using HookLab.Host.Transport;
using dnlib.DotNet;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		readonly HookLabService hookLab=new HookLabService();

		Task<object> InitializeHookLabAsync(RpcRequest req,CancellationToken token) => hookLab.InitializeAsync(this,req,token);
		object GetHookLabStatus(RpcRequest req) { CheckSession(req); return hookLab.Status(RequiredSession(req),(int?)req.Arguments["process_id"]); }
		async Task<object> GetHookTemplateAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req);
			var template=(string?)req.Arguments["template"] ?? "PrefixPostfix";
			if(template!="Prefix"&&template!="Postfix"&&template!="PrefixPostfix") throw new RpcException("invalid_arguments","template must be Prefix, Postfix, or PrefixPostfix.");
			return await OnDebuggerAsync(()=>{
				var loaded=FindModule(req,null);
				var metadata=TryMetadata(loaded) ?? throw new RpcException("metadata_unavailable","The selected module has no readable metadata.");
				var tokenValue=(int?)req.Arguments["method_token"] ?? throw new RpcException("invalid_arguments","method_token is required.");
				var method=metadata.ResolveToken(unchecked((uint)tokenValue)) as MethodDef ?? throw new RpcException("method_not_found",$"Token 0x{tokenValue:X8} is not a method in the selected module.");
				var target=TemplateTarget(method);
				return new { template,source=HookSourceTemplate.Generate(target,template),suggested_revision=1,target=new { module_id=ModuleIdOf(loaded),method_token=tokenValue,declaring_type=method.DeclaringType.FullName,method=method.Name.ToString(),signature=MethodSignature(method),module_mvid=method.Module.Mvid?.ToString("D")??"",il_sha256=MethodIlSha256(method) } };
			},token).ConfigureAwait(false);
		}
		Task<object> InstallHookAsync(RpcRequest req,CancellationToken token) => hookLab.InstallAsync(this,req,token);
		Task<object> CreateHookAsync(RpcRequest req,CancellationToken token) => hookLab.CreateAsync(this,req,token);
		Task<object> UpdateHookAsync(RpcRequest req,CancellationToken token) => hookLab.UpdateAsync(this,req,token);
		object ListHooks(RpcRequest req) { CheckSession(req); var session=(string?)req.Arguments["session_id"] ?? throw new RpcException("invalid_arguments","session_id is required."); return hookLab.List(session,(int?)req.Arguments["process_id"]); }
		Task<object> GetHookEventsAsync(RpcRequest req,CancellationToken token) { CheckSession(req); return hookLab.ReadEventsAsync(this,req,token); }
		Task<object> EnableHookAsync(RpcRequest req,CancellationToken token) => hookLab.SetEnabledAsync(this,req,true,token);
		Task<object> DisableHookAsync(RpcRequest req,CancellationToken token) => hookLab.SetEnabledAsync(this,req,false,token);
		Task<object> RemoveHookAsync(RpcRequest req,CancellationToken token) => hookLab.RemoveAsync(this,req,token);
		Task<object> RemoveAllHooksAsync(RpcRequest req,CancellationToken token) => hookLab.RemoveAllAsync(this,req,token);
		static string RequiredSession(RpcRequest req)=>(string?)req.Arguments["session_id"] ?? throw new RpcException("invalid_arguments","session_id is required.");
		async Task<object> InitializeHookLabFromUiAsync(CancellationToken token) {
			await EnsureUiSessionAsync(token).ConfigureAwait(false);
			string session; lock(sync) session=sessionId!;
			var process=await OnDebuggerAsync(()=>manager.Processes.Length==1 ? manager.Processes[0].Id : throw new RpcException("ambiguous_target","HookLab initialization from the GUI requires exactly one active process."),token).ConfigureAwait(false);
			return await hookLab.InitializeAsync(this,new RpcRequest { Operation="initialize_hooklab",Arguments=new JsonObject { ["session_id"]=session,["process_id"]=process } },token).ConfigureAwait(false);
		}
		async Task EnsureUiSessionAsync(CancellationToken token) {
			await OnDebuggerAsync(()=>{
				if(!manager.IsDebugging || manager.Processes.Length==0) throw new RpcException("session_not_found","Attach to a target before using HookLab.");
				lock(sync) if(sessionId is null) { sessionId=Guid.NewGuid().ToString("N"); attachedProgramId="ui"; sessionKind="ui"; lifecycleVersion++; stateVersion++; }
				return true;
			},token).ConfigureAwait(false);
		}

		string GenerateSelectedHookTemplate(MethodDef selected,string template)=>HookSourceTemplate.Generate(TemplateTarget(selected),template);
		Task<object> InstallSelectedHookAsync(MethodDef selected,string hookId,string kind,int maximumEventsPerSecond,int maximumStringLength,CancellationToken token) =>
			InstallSelectedHookAsync(selected,hookId,kind,maximumEventsPerSecond,maximumStringLength,null,0,token);
		async Task<object> InstallSelectedHookAsync(MethodDef selected,string hookId,string kind,int maximumEventsPerSecond,int maximumStringLength,string? source,int revision,CancellationToken token) {
			await EnsureUiSessionAsync(token).ConfigureAwait(false);
			string currentSession,currentStop; long currentExecution;
			lock(sync) { currentSession=sessionId!; currentStop=stopId ?? ""; currentExecution=executionVersion; }
			var match=await OnDebuggerAsync(()=> {
				var candidates=manager.Processes.SelectMany(process=>process.Runtimes).SelectMany(runtime=>runtime.Modules).Select(module=>new { Module=module,Metadata=TryMetadata(module) }).Where(value=>value.Metadata?.Mvid==selected.Module.Mvid).ToArray();
				if(candidates.Length!=1) throw new RpcException("module_ambiguous",$"The selected MVID matches {candidates.Length} loaded module instances; select the exact method through MCP.");
				return candidates[0];
			},token).ConfigureAwait(false);
			var body=selected.Body ?? throw new RpcException("invalid_arguments","The selected method has no managed body.");
			var moduleId=ModuleIdOf(match.Module);
			await hookLab.InitializeAsync(this,new RpcRequest { Operation="initialize_hooklab",Arguments=new JsonObject { ["session_id"]=currentSession,["process_id"]=match.Module.Process.Id } },token).ConfigureAwait(false);
			var request=new JsonObject {
				["session_id"]=currentSession,["process_id"]=match.Module.Process.Id,["expected_execution_version"]=currentExecution,["expected_stop_id"]=currentStop,["hook_id"]=hookId,["kind"]=kind,
				["module_id"]=moduleId,["assembly"]=selected.Module.Assembly?.Name?.ToString() ?? selected.Module.Name?.ToString() ?? "",["declaring_type"]=selected.DeclaringType.FullName,["method"]=selected.Name.ToString(),["method_token"]=(int)selected.MDToken.Raw,
				["signature"]=MethodSignature(selected),["module_mvid"]=selected.Module.Mvid?.ToString("D") ?? "",["il_sha256"]=MethodIlSha256(selected),["arrival_module_id"]=moduleId,["arrival_method_token"]=(int)selected.MDToken.Raw,
				["arrival_il_offset"]=(int)(body.Instructions.FirstOrDefault()?.Offset ?? 0),["nearby_offsets"]=new JsonArray(body.Instructions.Select(instruction=>(JsonNode)(int)instruction.Offset).ToArray())
				,["maximum_events_per_second"]=maximumEventsPerSecond,["maximum_string_length"]=maximumStringLength
			};
			if(source is not null) { request["source"]=source; request["revision"]=revision; }
			return await hookLab.InstallAsync(this,new RpcRequest { Operation="install_hook",Arguments=request },token).ConfigureAwait(false);
		}
		ModuleDef? TryMetadata(dnSpy.Contracts.Debugger.DbgModule module) { try { return metadataService.TryGetMetadata(module); } catch(Exception) { return null; } }
		static string MethodSignature(MethodDef method)=>method.MethodSig.RetType.FullName+" "+method.Name+"("+String.Join(",",method.MethodSig.Params.Select(parameter=>parameter.FullName))+")";
		static HookTemplateTarget TemplateTarget(MethodDef method) {
			if(method.GenericParameters.Count!=0||method.DeclaringType.GenericParameters.Count!=0) throw new RpcException("unsupported_hook_target","Generic methods and methods on generic types do not have generated hook templates yet.");
			var parameters=method.MethodSig.Params.Select((type,index)=>{
				var definition=method.ParamDefs.FirstOrDefault(parameter=>parameter.Sequence==index+1);
				var name=definition is null||String.IsNullOrWhiteSpace(definition.Name?.ToString())?"__"+index.ToString(CultureInfo.InvariantCulture):definition.Name!.ToString();
				// Harmony exposes every CLR by-reference argument to a patch as ref. Emitting out would
				// make an empty no-op Prefix fail C# definite-assignment checks.
				var modifier=type is ByRefSig ? "ref" : "";
				return new HookTemplateParameter(name,CSharpHookType(type),modifier);
			}).ToArray();
			return new HookTemplateTarget(CSharpHookType(method.DeclaringType.ToTypeSig()),method.IsStatic,CSharpHookType(method.MethodSig.RetType),parameters);
		}
		static string CSharpHookType(TypeSig type) {
			var name=type.FullName;
			if(type is ByRefSig) name=name.TrimEnd('&');
			if(name.IndexOf('`')>=0||name.IndexOf('!')>=0||name.IndexOf('*')>=0||name.IndexOf('<')>=0) throw new RpcException("unsupported_hook_target","Generic, pointer, and function-pointer types do not have generated hook templates yet.");
			return name.Replace('/','.');
		}
		static string MethodIlSha256(MethodDef method) {
			if(method.Module is not ModuleDefMD module || method.Body is null) throw new RpcException("metadata_unavailable","The selected method's original IL bytes are unavailable.");
			var reader=module.Metadata.PEImage.CreateReader(method.RVA+(uint)method.Body.HeaderSize); var bytes=reader.ReadBytes(method.Body.Instructions.Sum(instruction=>instruction.GetSize()));
			using var sha=SHA256.Create(); return String.Concat(sha.ComputeHash(bytes).Select(value=>value.ToString("x2",CultureInfo.InvariantCulture)));
		}

		sealed class HookLabService {
			readonly object gate=new object();
			readonly Dictionary<string,HookRecord> hooks=new Dictionary<string,HookRecord>(StringComparer.Ordinal);
			readonly Dictionary<string,RuntimeRecord> runtimes=new Dictionary<string,RuntimeRecord>(StringComparer.Ordinal);
			readonly List<HookEventRecord> events=new List<HookEventRecord>();
			readonly SemaphoreSlim initialization=new SemaphoreSlim(1,1);
			long cursor;

			public async Task<object> InitializeAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				BindUi(host);
				var session=Required(source.Arguments,"session_id");
				var processId=RequiredInt(source.Arguments,"process_id");
				RuntimeRecord existing;
				lock(gate) if(runtimes.TryGetValue(RuntimeKey(session,processId),out existing)) return Initialized(existing,false);
				await initialization.WaitAsync(token).ConfigureAwait(false);
				try {
					lock(gate) if(runtimes.TryGetValue(RuntimeKey(session,processId),out existing)) return Initialized(existing,false);

				var wasRunning=await host.OnDebuggerAsync(()=>{ host.CheckVersion(source); return host.SelectProcess(source).IsRunning; },token).ConfigureAwait(false);
				var completion=Path.Combine(Path.GetTempPath(),"dgspy-hooklab-init-"+Guid.NewGuid().ToString("N")+".completion");
				ProbeConnection? connection=null;
				try {
					var identity=TargetIdentity(source.Arguments,processId,completion);
					await ResumeAsync(host,source,token).ConfigureAwait(false);
					var pipe=await InitializeAutonomouslyAsync(processId,identity,token).ConfigureAwait(false);

					var completionReport=await ReadCompletionAsync(completion,token).ConfigureAwait(false);
					if(!String.Equals(completionReport.TryGetValue("status",out var completedStatus)?completedStatus:null,"ok",StringComparison.Ordinal))
						throw new RpcException("hooklab_initialization_failed","HookLab worker reported: "+String.Join("; ",completionReport.Select(pair=>pair.Key+"="+pair.Value)));
					connection=new ProbeConnection(pipe,Array.Empty<byte>(),Array.Empty<byte>(),authenticationEnabled:false);
					var runtime=new RuntimeRecord(session,processId,connection,Long(completionReport,"hooks_version"));
					var status=await SendAsync(runtime,"status","{}",token).ConfigureAwait(false);
					lock(gate) runtimes.Add(RuntimeKey(session,processId),runtime);
					connection=null;
					StartEventPump(runtime);
					return Initialized(runtime,true,status);
				}
				finally {
					connection?.Dispose();
					TryDelete(completion);
					if(wasRunning) await ResumeAsync(host,source,CancellationToken.None).ConfigureAwait(false);
					else await EnsurePausedAsync(host,source,CancellationToken.None).ConfigureAwait(false);
				}
				}
				finally { initialization.Release(); }
			}

			static async Task<string> InitializeAutonomouslyAsync(int processId,JsonObject parameters,CancellationToken token) {
				var staging=Path.Combine(Path.GetTempPath(),"dgspy-hooklab-native-"+processId.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(staging);
				try {
					using var payload=dgSpy.Extension.PayloadDelivery.HookLabPayloadResolver.Open();
					var nativeSource=Path.Combine(payload.HostRoot,"hooklab","HookLab.NativeBootstrap.x64.dll");
					if(!File.Exists(nativeSource)) throw new RpcException("hooklab_native_initializer_missing","The installed host does not contain the HookLab x64 initializer. Rebuild or reinstall dgSpy.");
					var nativePath=Path.Combine(staging,"HookLab.NativeBootstrap.x64.dll");
					File.Copy(nativeSource,nativePath,false);
					File.Copy(payload.PayloadPath,Path.Combine(staging,"HookLab.Bootstrap.dll"),false);
					File.WriteAllText(Path.Combine(staging,"initialize.params"),ParameterText(parameters),new System.Text.UTF8Encoding(false));
					NativeHookLabInitializer.Load(processId,nativePath);
					var completion=Required(parameters,"completion_path");
					var report=await ReadCompletionAsync(completion,token).ConfigureAwait(false);
					if(!String.Equals(report.TryGetValue("status",out var status)?status:null,"ok",StringComparison.Ordinal))
						throw new RpcException("hooklab_initialization_failed","HookLab worker reported: "+String.Join("; ",report.Select(pair=>pair.Key+"="+pair.Value)));
					return Required(report,"pipe_name","HookLab initialization did not report its control pipe.");
				}
				finally { TryDeleteDirectory(staging); }
			}

			public object Status(string sessionId,int? processId) {
				lock(gate) {
					var selected=runtimes.Values.Where(value=>value.SessionId==sessionId && (processId is null || value.ProcessId==processId.Value)).ToArray();
					return new { initialized=selected.Length!=0,runtimes=selected.Select(value=>new { process_id=value.ProcessId,state="ready",hooks_version=value.HooksVersion }).ToArray() };
				}
			}

			public Task<object> InstallAsync(RpcHost host,RpcRequest source,CancellationToken token) => InstallAsync(host,source,null,token);
			public Task<object> CreateAsync(RpcHost host,RpcRequest source,CancellationToken token) => InstallAsync(host,source,"create",token);
			public Task<object> UpdateAsync(RpcHost host,RpcRequest source,CancellationToken token) => InstallAsync(host,source,"update",token);

			async Task<object> InstallAsync(RpcHost host,RpcRequest source,string? lifecycle,CancellationToken token) {
				host.CheckSession(source);
				BindUi(host);
				var definition=HookDefinition.Parse(source.Arguments);
				if(lifecycle is not null && definition.Source is null) throw new RpcException("invalid_arguments",lifecycle+"_hook requires source and revision.");
				if(lifecycle=="create" && definition.Revision!=1) throw new RpcException("invalid_arguments","create_hook requires revision 1.");
				bool initialized; lock(gate) initialized=runtimes.ContainsKey(RuntimeKey(definition.SessionId,definition.ProcessId));
				if(!initialized) await InitializeAsync(host,source,token).ConfigureAwait(false);
				var compiled=definition.Source is not null; var enabled=true;
				lock(gate) {
					if(hooks.TryGetValue(definition.Key,out var existing)) {
						if(lifecycle=="create") throw new RpcException("hook_exists","Hook ID '"+definition.HookId+"' is already installed in this process. Use update_hook to replace its compiled source.");
						enabled=existing.Enabled;
						if(!compiled && existing.Definition.Equivalent(definition)) return Installed(existing,false);
						if(compiled && !existing.Definition.SameTarget(definition)) throw new RpcException("hook_exists","Hook ID '"+definition.HookId+"' already names a different target in this process.");
						if(compiled && definition.Revision<=existing.Definition.Revision) throw new RpcException("invalid_arguments","revision must be greater than the installed revision.");
						if(!compiled) throw new RpcException("hook_exists","Hook ID '"+definition.HookId+"' already names a different installed hook in this process.");
					}
					else if(lifecycle=="update") throw new RpcException("hook_not_found","Hook ID '"+definition.HookId+"' is not installed in this process. Use create_hook for its first revision.");
				}

				var parameters=definition.Parameters("unused");
				var runtime=ForOperation(source.Arguments);
				var report=await SendAsync(runtime,compiled?"install_compiled_prefix":"install",ParameterText(parameters),token).ConfigureAwait(false);
				MethodDef? markerMethod=null;
				try { markerMethod=await host.OnDebuggerAsync(()=>{ var module=host.FindModule(source,null); return host.TryMetadata(module)?.ResolveToken(unchecked((uint)definition.MethodToken)) as MethodDef; },token).ConfigureAwait(false); }
				catch(Exception) { }
				var installedRecord=new HookRecord(definition,Required(report,"patch_id","The probe installed a hook without reporting its patch ID.")) { MethodDefinition=markerMethod,Enabled=enabled };
				lock(gate) hooks[definition.Key]=installedRecord; HookLabUiBridge.PublishHook(definition.SessionId,definition.ProcessId,definition.HookId,definition.Kind,definition.DeclaringType+"."+definition.Method,installedRecord.PatchId,definition.Revision,compiled,definition.Source,enabled,markerMethod);
				return Installed(installedRecord,true);
			}

			public async Task<object> SetEnabledAsync(RpcHost host,RpcRequest source,bool enabled,CancellationToken token) {
				host.CheckSession(source);
				var session=Required(source.Arguments,"session_id"); var process=RequiredInt(source.Arguments,"process_id"); var hookId=Required(source.Arguments,"hook_id");
				HookRecord record; lock(gate) if(!hooks.TryGetValue(Key(session,process,hookId),out record!)) throw new RpcException("hook_not_found","Hook ID '"+hookId+"' is not installed in this process.");
				if(record.Enabled==enabled) return new { hook=View(record),changed=false };
				var runtime=ForOperation(source.Arguments);
				await SendAsync(runtime,enabled?"enable":"disable",record.PatchId,token).ConfigureAwait(false);
				lock(gate) record.Enabled=enabled;
				HookLabUiBridge.PublishHook(session,process,hookId,record.Definition.Kind,record.Definition.DeclaringType+"."+record.Definition.Method,record.PatchId,record.Definition.Revision,record.Definition.Source is not null,record.Definition.Source,enabled,record.MethodDefinition);
				return new { hook=View(record),changed=true };
			}

			public object List(string sessionId,int? processId) {
				lock(gate) return new { hooks=hooks.Values.Where(value=>value.Definition.SessionId==sessionId && (processId is null || value.Definition.ProcessId==processId.Value)).Select(View).ToArray() };
			}

			public async Task<object> ReadEventsAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				var runtime=ForOperation(source.Arguments);
				var maximum=Math.Min(256,Math.Max(1,(int?)source.Arguments["max_events"] ?? 64));
				var dropped=await DrainIntoHostAsync(runtime,maximum,token).ConfigureAwait(false);
				lock(gate) {
					var after=(long?)source.Arguments["after_cursor"] ?? 0;
					return new { events=events.Where(value=>value.Cursor>after).Take(maximum).Select(value=>new { cursor=value.Cursor,sequence=value.Sequence,patch_id=value.PatchId,payload_json=value.PayloadJson,dropped=value.Dropped }).ToArray(),next_cursor=cursor,dropped };
				}
			}

			async Task<long> DrainIntoHostAsync(RuntimeRecord runtime,int maximum,CancellationToken token) {
				var report=await SendAsync(runtime,"drain",maximum.ToString(CultureInfo.InvariantCulture),token).ConfigureAwait(false); var dropped=Long(report,"dropped"); var added=new List<HookEventRecord>();
				for(var index=0;;index++) { if(!report.TryGetValue("event_"+index.ToString(CultureInfo.InvariantCulture),out var line)) break; var first=line.IndexOf('|'); var second=first<0 ? -1 : line.IndexOf('|',first+1); if(first<1 || second<first+2) continue; added.Add(new HookEventRecord(Interlocked.Increment(ref cursor),line.Substring(0,first),line.Substring(first+1,second-first-1),line.Substring(second+1),dropped)); }
				lock(gate) { events.AddRange(added); if(events.Count>2048) events.RemoveRange(0,events.Count-2048); }
				foreach(var item in added) HookLabUiBridge.PublishEvent(item.Cursor,item.PatchId,item.PayloadJson,item.Dropped); return dropped;
			}

			void StartEventPump(RuntimeRecord runtime) { runtime.Pump=Task.Run(async ()=>{ try { while(!runtime.Cancellation.IsCancellationRequested) { await Task.Delay(500,runtime.Cancellation.Token).ConfigureAwait(false); await DrainIntoHostAsync(runtime,256,runtime.Cancellation.Token).ConfigureAwait(false); } } catch(OperationCanceledException) { } catch(Exception) { } }); }

			public async Task<object> RemoveAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				var session=Required(source.Arguments,"session_id"); var process=RequiredInt(source.Arguments,"process_id"); var hookId=Required(source.Arguments,"hook_id");
				HookRecord record;
				lock(gate) if(!hooks.TryGetValue(Key(session,process,hookId),out record!)) return new { hook_id=hookId,removed=false };
				var runtime=ForOperation(source.Arguments);
				await SendAsync(runtime,"uninstall",record.PatchId,token).ConfigureAwait(false);
				// Events queued before the unpatch are not evidence of calls after removal. Drain that bounded
				// backlog before returning so the response is the cursor boundary after which every event would
				// necessarily have been produced by a still-live patch.
				await DrainRemovalBacklogAsync(runtime,token).ConfigureAwait(false);
				lock(gate) hooks.Remove(record.Definition.Key); HookLabUiBridge.RemoveHook(session,process,hookId);
				return new { hook_id=hookId,patch_id=record.PatchId,removed=true };
			}

			public async Task<object> RemoveAllAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				var session=Required(source.Arguments,"session_id"); var process=RequiredInt(source.Arguments,"process_id");
				HookRecord[] selected; lock(gate) selected=hooks.Values.Where(value=>value.Definition.SessionId==session && value.Definition.ProcessId==process).ToArray();
				var removed=new List<string>();
				var runtime=selected.Length==0 ? null : ForOperation(source.Arguments);
				foreach(var record in selected) {
					await SendAsync(runtime!,"uninstall",record.PatchId,token).ConfigureAwait(false); removed.Add(record.Definition.HookId);
					lock(gate) hooks.Remove(record.Definition.Key); HookLabUiBridge.RemoveHook(session,process,record.Definition.HookId);
				}
				if(runtime is not null) await DrainRemovalBacklogAsync(runtime,token).ConfigureAwait(false);
				return new { removed=removed.ToArray(),removed_count=removed.Count };
			}

			public void BindUi(RpcHost host) => HookLabUiBridge.Bind(
				()=>host.InitializeHookLabFromUiAsync(CancellationToken.None),
				(method,id,kind,rate,stringLength)=>host.InstallSelectedHookAsync(method,id,kind,rate,stringLength,CancellationToken.None),
				(method,template)=>host.GenerateSelectedHookTemplate(method,template),
				(method,id,kind,source,revision)=>host.InstallSelectedHookAsync(method,id,kind,100,1024,source,revision,CancellationToken.None),
				async (session,process,hookId,enabled)=>{ var args=new JsonObject { ["session_id"]=session,["process_id"]=process,["hook_id"]=hookId }; await SetEnabledAsync(host,new RpcRequest { Operation=enabled?"enable_hook":"disable_hook",Arguments=args },enabled,CancellationToken.None).ConfigureAwait(false); },
				async (session,process,hookId)=>{ var args=new JsonObject { ["session_id"]=session,["process_id"]=process,["hook_id"]=hookId }; await RemoveAsync(host,new RpcRequest { Operation="remove_hook",Arguments=args },CancellationToken.None).ConfigureAwait(false); },
				async (session,process)=>{ var args=new JsonObject { ["session_id"]=session,["process_id"]=process }; await RemoveAllAsync(host,new RpcRequest { Operation="remove_all_hooks",Arguments=args },CancellationToken.None).ConfigureAwait(false); });

			RuntimeRecord ForOperation(JsonObject arguments) {
				var session=Required(arguments,"session_id"); var process=RequiredInt(arguments,"process_id");
				lock(gate) {
					if(!runtimes.TryGetValue(RuntimeKey(session,process),out var runtime)) throw new RpcException("hooklab_not_started","HookLab has not been started in this process.");
					return runtime;
				}
			}

			async Task<Dictionary<string,string>> SendAsync(RuntimeRecord runtime,string operation,string payload,CancellationToken token) {
				await runtime.OperationGate.WaitAsync(token).ConfigureAwait(false);
				try {
					var request=new ProbeMessage(1,ProbeMessageKind.Request,Guid.NewGuid().ToString("N"),operation,payload,runtime.HooksVersion);
					var response=await Task.Run(()=>runtime.Connection.Send(request),token).ConfigureAwait(false);
					if(response.Operation=="error") throw new RpcException("hook_operation_failed","HookLab "+operation+" failed: "+response.PayloadJson);
					if(response.ExpectedHooksVersion.HasValue) runtime.HooksVersion=response.ExpectedHooksVersion.Value;
					return ParseReport(response.PayloadJson);
				}
				finally { runtime.OperationGate.Release(); }
			}

			async Task DrainRemovalBacklogAsync(RuntimeRecord runtime,CancellationToken token) {
				for(var page=1;page<=HookLabDrainPolicy.MaximumPages;page++) {
					var report=await SendAsync(runtime,"drain",HookLabDrainPolicy.PageSize.ToString(CultureInfo.InvariantCulture),token).ConfigureAwait(false);
					if(!HookLabDrainPolicy.NeedsAnotherPage(report,page)) return;
				}
				throw new RpcException("hook_cleanup_ambiguous","HookLab removed the patch but could not drain its bounded pre-removal event backlog completely.");
			}

			public void ClearAll() {
				RuntimeRecord[] active;
				lock(gate) { active=runtimes.Values.ToArray(); runtimes.Clear(); hooks.Clear(); events.Clear(); cursor=0; }
				foreach(var runtime in active) runtime.Dispose();
				HookLabUiBridge.Clear();
			}

			async Task<Dictionary<string,string>> EvaluatePayloadAsync(RpcHost host,RpcRequest source,PayloadOperation operation,JsonObject? parameters,CancellationToken token) {
				var threads=await host.ListThreadsAsync(new RpcRequest { Operation="list_threads",Arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["include_evaluability"]=true } },token).ConfigureAwait(false);
				var thread=threads.FirstOrDefault(value=>value.ProcessId==RequiredInt(source.Arguments,"process_id") && value.CanEvaluate==true)
					?? throw new RpcException("hooklab_no_evaluable_thread","HookLab could not find an evaluable managed thread after pausing the target.");
				if(operation==PayloadOperation.prepare) parameters!["appdomain_id"]=await AppDomainIdAsync(host,thread.ThreadId,token).ConfigureAwait(false);
				var request=new RpcRequest { Operation="initialize_hooklab",Arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["thread_id"]=thread.ThreadId,["frame_index"]=0,["timeout_ms"]=60000 } };
				string reportText;
				if(operation==PayloadOperation.prepare) {
					var scanned=await InvokeTextAsync(host,request,PayloadExpressions.ResidentGenerationScan(),token).ConfigureAwait(false);
					var generations=PayloadGenerationScan.Parse(scanned);
					if(!generations.Complete || generations.GenerationCount!=0) throw new RpcException("hooklab_generation_conflict",generations.Refusal ?? "A HookLab generation is already resident but is not owned by this host session.");
					using var payload=new HostHookLabPayloadSource().Open();
					reportText=await InvokeTextAsync(host,request,PayloadExpressions.Prepare(payload.Path,ParameterText(parameters!)),token).ConfigureAwait(false);
				}
				else {
					var scanned=PayloadGenerationScan.Parse(await InvokeTextAsync(host,request,PayloadExpressions.ResidentGenerationScan(),token).ConfigureAwait(false));
					if(!scanned.Complete || scanned.GenerationCount!=1) throw new RpcException("hooklab_generation_conflict",scanned.Refusal ?? "HookLab expected exactly one resident generation.");
					reportText=await InvokeTextAsync(host,request,PayloadExpressions.Commit(scanned.FirstGenerationIndex),token).ConfigureAwait(false);
				}
				var report=ParseReport(reportText);
				if(!String.Equals(report.TryGetValue("status",out var status)?status:null,"ok",StringComparison.Ordinal)) throw new RpcException("hooklab_initialization_failed",String.Join("; ",report.Select(pair=>pair.Key+"="+pair.Value)));
				return report;
			}

			async Task<Dictionary<string,string>> ExecuteInitializationOperationAsync(RpcHost host,RpcRequest source,PayloadOperation operation,JsonObject? parameters,CancellationToken token) {
				if(await TryReachEvaluablePauseAsync(host,source,token).ConfigureAwait(false)) return await EvaluatePayloadAsync(host,source,operation,parameters,token).ConfigureAwait(false);
				var carriers=await SelectInternalCarriersAsync(host,source,token).ConfigureAwait(false);
				AtomicActionResult? lastResult=null;
				foreach(var carrier in carriers) {
					if(parameters is not null) parameters["appdomain_id"]=carrier.AppDomainId;
					var result=await RunInitializationPayloadAsync(host,source,carrier,operation,parameters,token).ConfigureAwait(false);
					if(result.Status.ActionOutcome==HookLab.Contracts.ActionOutcome.completed) return Report(result);
					lastResult=result;
					if(!HookCarrierSelectionPolicy.ShouldTryAnotherCarrier(result.Status.ActionOutcome,result.Status.ActionMayHaveExecuted)) break;
				}
				EnsureCompleted(lastResult!,operation.ToString());
				throw new InvalidOperationException("Unreachable HookLab carrier result.");
			}

			static Task<IReadOnlyList<HookCarrier>> SelectInternalCarriersAsync(RpcHost host,RpcRequest source,CancellationToken token)=>host.OnDebuggerAsync<IReadOnlyList<HookCarrier>>(()=>{
				var process=host.SelectProcess(source);
				var candidates=new List<(int Score,HookCarrier Carrier)>();
				foreach(var thread in process.Threads) {
					var walker=thread.CreateStackWalker();
					try {
						var frames=walker.GetNextStackFrames(32);
						try {
							for(var frameIndex=0;frameIndex<frames.Length;frameIndex++) {
								var frame=frames[frameIndex]; if(frame.Module is null || frame.FunctionToken==0) continue;
								if(!HookCarrierSelectionPolicy.TryNormalizeLocation((ulong)frame.FunctionToken,(ulong)frame.FunctionOffset,out var methodToken,out var ilOffset)) continue;
								var metadata=host.TryMetadata(frame.Module!); var method=metadata?.ResolveToken((uint)frame.FunctionToken) as MethodDef;
								if(method?.Body is null || method.Body.Instructions.Count==0) continue;
								var moduleId=host.ModuleIdOf(frame.Module!);
								var filename=frame.Module!.Filename ?? ""; var isProcessImage=false;
								try { isProcessImage=String.Equals(Path.GetFullPath(filename),process.Filename,StringComparison.OrdinalIgnoreCase); } catch { }
								var isFramework=filename.IndexOf("\\Windows\\",StringComparison.OrdinalIgnoreCase)>=0 || filename.IndexOf("\\Microsoft.NET\\",StringComparison.OrdinalIgnoreCase)>=0;
								var score=HookCarrierSelectionPolicy.Score(frameIndex,isProcessImage,isFramework);
								candidates.Add((score,new HookCarrier(moduleId,methodToken,ilOffset,method.Body.Instructions.Select(value=>(int)value.Offset).ToArray(),frame.Module.AppDomain?.Id.ToString(CultureInfo.InvariantCulture) ?? "default")));
							}
						}
						finally { host.manager.Close(frames); }
					}
					finally { walker.Close(); }
				}
				if(candidates.Count!=0) return candidates.OrderByDescending(value=>value.Score).Select(value=>value.Carrier).GroupBy(value=>(value.ModuleId,value.MethodToken,value.IlOffset)).Select(value=>value.First()).Take(8).ToArray();
				throw new RpcException("hooklab_no_carrier","HookLab found neither an evaluable managed thread nor a managed stack frame it could use as an internal arrival carrier.");
			},token);

			async Task<AtomicActionResult> RunInitializationPayloadAsync(RpcHost host,RpcRequest source,HookCarrier carrier,PayloadOperation operation,JsonObject? parameters,CancellationToken token) {
				var arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["process_id"]=RequiredInt(source.Arguments,"process_id"),["module_id"]=carrier.ModuleId,["operation_version"]=1,["action_kind"]="payload",["payload_operation"]=operation.ToString(),["timeout_ms"]=60000 };
				if(parameters is not null) arguments["payload_parameters"]=parameters;
				arguments["request"]=new JsonObject { ["schema_version"]=1,["action_id"]=Guid.NewGuid().ToString("N"),["action_name"]="HookLab initialize "+operation,["process_id"]=RequiredInt(source.Arguments,"process_id"),["runtime_id"]="",["app_domain_id"]=carrier.AppDomainId,["module"]=carrier.ModuleId,["method_token"]=carrier.MethodToken,["il_offset"]=carrier.IlOffset,["nearby_offsets"]=new JsonArray(carrier.NearbyOffsets.Select(value=>(JsonNode)value).ToArray()),["resume_policy"]="resume" };
				return await host.RunAtomicActionAsync(new RpcRequest { Operation="initialize_hooklab",RequestId=source.RequestId,Arguments=arguments,DeadlineUtc=source.DeadlineUtc },token).ConfigureAwait(false);
			}

			static Task<string> AppDomainIdAsync(RpcHost host,string threadId,CancellationToken token)=>host.OnDebuggerAsync(()=>{
				var thread=host.manager.Processes.SelectMany(process=>process.Threads).FirstOrDefault(value=>ThreadId(value)==threadId)
					?? throw new RpcException("thread_not_found","The selected HookLab initialization thread exited before evaluation.");
				var walker=thread.CreateStackWalker();
				try {
					var frames=walker.GetNextStackFrames(1);
					try { return frames.Length==0 ? "default" : frames[0].Module?.AppDomain?.Id.ToString(CultureInfo.InvariantCulture) ?? "default"; }
					finally { host.manager.Close(frames); }
				}
				finally { walker.Close(); }
			},token);

			static async Task<string> InvokeTextAsync(RpcHost host,RpcRequest request,string expression,CancellationToken token) {
				request.Arguments["expression"]=expression;
				var result=await host.InvokeExpressionAsync(request,"hooklab_initialization",token).ConfigureAwait(false);
				if(!result.Completed || result.Value?.Value is not string text) throw new RpcException("hooklab_evaluation_failed",result.Error ?? "HookLab initialization returned no report.");
				return text;
			}

			static JsonObject TargetIdentity(JsonObject source,int processId,string completion) {
				try { using var process=Process.GetProcessById(processId); return new JsonObject { ["host_id"]="dgspy-hooklab",["image_path"]=process.MainModule?.FileName ?? process.ProcessName,["process_id"]=processId.ToString(CultureInfo.InvariantCulture),["process_creation_utc_ticks"]=process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),["architecture"]="x64",["runtime_id"]="v4.0.30319",["appdomain_id"]="1",["endpoint"]="pipe",["completion_path"]=completion }; }
				catch(Exception ex) { throw new RpcException("target_unavailable","Could not read target process identity: "+ex.Message); }
			}

			static async Task EnsurePausedAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				if(!await host.OnDebuggerAsync(()=>host.SelectProcess(source).IsRunning,token).ConfigureAwait(false)) return;
				await host.PauseProcessAsync(ControlRequest(source),token).ConfigureAwait(false);
			}
			static async Task<bool> TryReachEvaluablePauseAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				var session=Required(source.Arguments,"session_id"); var process=RequiredInt(source.Arguments,"process_id");
				for(var attempt=1;attempt<=20;attempt++) {
					await EnsurePausedAsync(host,source,token).ConfigureAwait(false);
					var threads=await host.ListThreadsAsync(new RpcRequest { Operation="list_threads",Arguments=new JsonObject { ["session_id"]=session,["include_evaluability"]=true } },token).ConfigureAwait(false);
					if(threads.Any(value=>value.ProcessId==process && value.CanEvaluate==true)) return true;
					if(attempt==20) break;
					await ResumeAsync(host,source,token).ConfigureAwait(false);
					await Task.Delay(25,token).ConfigureAwait(false);
				}
				return false;
			}
			static async Task ResumeAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				if(await host.OnDebuggerAsync(()=>host.SelectProcess(source).IsRunning,token).ConfigureAwait(false)) return;
				await host.ContinueProcessAsync(ControlRequest(source),token).ConfigureAwait(false);
			}
			static RpcRequest ControlRequest(RpcRequest source)=>new RpcRequest { Operation=source.Operation,Arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["process_id"]=RequiredInt(source.Arguments,"process_id") } };
			static object Initialized(RuntimeRecord runtime,bool changed,Dictionary<string,string>? status=null)=>new { initialized=true,changed,process_id=runtime.ProcessId,state="ready",hooks_version=runtime.HooksVersion,probe_instance_id=status is null?null:(status.TryGetValue("probe_instance_id",out var value)?value:null) };
			static object Installed(HookRecord record,bool changed) => new { hook=View(record),installed=changed };
			static object View(HookRecord record) => new { hook_id=record.Definition.HookId,patch_id=record.PatchId,kind=record.Definition.Kind,revision=record.Definition.Revision,compiled=record.Definition.Source is not null,source=record.Definition.Source,diagnostics=Array.Empty<string>(),enabled=record.Enabled,process_id=record.Definition.ProcessId,module_id=record.Definition.ModuleId,method_token=record.Definition.MethodToken,declaring_type=record.Definition.DeclaringType,method=record.Definition.Method,state=record.Enabled?"enabled":"disabled" };
			static void EnsureCompleted(AtomicActionResult result,string operation) { if(result.Status.ActionOutcome!=HookLab.Contracts.ActionOutcome.completed) throw new RpcException("hook_operation_failed",HookLabInstallPresentation.Failure(operation,result.Status.ActionOutcome,result.Status.InterruptionReason,result.Error)); }
			static Dictionary<string,string> Report(AtomicActionResult result) { var node=JsonNode.Parse(result.VerificationEvidence ?? "{}") as JsonObject; return ParseReport((string?)node?["report"] ?? ""); }
			static Dictionary<string,string> ParseReport(string text) { var values=new Dictionary<string,string>(StringComparer.Ordinal); foreach(var line in text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)) { var separator=line.IndexOf('='); if(separator>0) values[line.Substring(0,separator)]=line.Substring(separator+1); } return values; }
			static async Task<Dictionary<string,string>> ReadCompletionAsync(string path,CancellationToken token) {
				var deadline=DateTime.UtcNow.AddSeconds(20);
				while(DateTime.UtcNow<deadline) {
					token.ThrowIfCancellationRequested();
					if(File.Exists(path)) {
						var report=ParseReport(File.ReadAllText(path));
						// The target writes the report incrementally. File existence, and even its first
						// status line, do not mean the ready record has been fully published yet.
						if(report.TryGetValue("status",out var status) && (!String.Equals(status,"ok",StringComparison.Ordinal) || report.ContainsKey("pipe_name"))) return report;
					}
					await Task.Delay(50,token).ConfigureAwait(false);
				}
				throw new RpcException("hook_operation_timed_out","HookLab worker did not publish a complete ready record within 20 seconds.");
			}
			static void TryDelete(string path) { try { File.Delete(path); } catch { } }
			static void TryDeleteDirectory(string path) { try { Directory.Delete(path,true); } catch { } }
			static string Required(Dictionary<string,string> values,string key,string message) => values.TryGetValue(key,out var value) && !String.IsNullOrWhiteSpace(value) ? value : throw new RpcException("hook_operation_failed",message);
			static long Long(Dictionary<string,string> values,string key) => values.TryGetValue(key,out var value) && Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed) ? parsed : 0;
			static string ParameterText(JsonObject values) => String.Join("\n",values.Select(pair=>pair.Key+"="+(string?)pair.Value))+"\n";
			static string Required(JsonObject values,string key) => (string?)values[key] is string value && !String.IsNullOrWhiteSpace(value) ? value : throw new RpcException("invalid_arguments",key+" is required.");
			static int RequiredInt(JsonObject values,string key) => (int?)values[key] ?? throw new RpcException("invalid_arguments",key+" is required.");
			static string Key(string session,int process,string hookId)=>session+"\n"+process.ToString(CultureInfo.InvariantCulture)+"\n"+hookId;
			static string RuntimeKey(string session,int process)=>session+"\n"+process.ToString(CultureInfo.InvariantCulture);

			sealed class HookRecord { public HookRecord(HookDefinition definition,string patchId) { Definition=definition; PatchId=patchId; } public HookDefinition Definition { get; } public string PatchId { get; } public bool Enabled=true; public MethodDef? MethodDefinition; }
			sealed class HookCarrier { public HookCarrier(string moduleId,int methodToken,int ilOffset,int[] nearbyOffsets,string appDomainId) { ModuleId=moduleId; MethodToken=methodToken; IlOffset=ilOffset; NearbyOffsets=nearbyOffsets; AppDomainId=appDomainId; } public string ModuleId { get; } public int MethodToken { get; } public int IlOffset { get; } public int[] NearbyOffsets { get; } public string AppDomainId { get; } }
			sealed class RuntimeRecord : IDisposable { public RuntimeRecord(string sessionId,int processId,ProbeConnection connection,long hooksVersion) { SessionId=sessionId; ProcessId=processId; Connection=connection; HooksVersion=hooksVersion; } public string SessionId { get; } public int ProcessId { get; } public ProbeConnection Connection { get; } public long HooksVersion; public SemaphoreSlim OperationGate { get; }=new SemaphoreSlim(1,1); public CancellationTokenSource Cancellation { get; }=new CancellationTokenSource(); public Task? Pump; public void Dispose() { Cancellation.Cancel(); Connection.Dispose(); OperationGate.Dispose(); Cancellation.Dispose(); } }
			sealed class HookEventRecord { public HookEventRecord(long cursor,string sequence,string patchId,string payloadJson,long dropped) { Cursor=cursor; Sequence=sequence; PatchId=patchId; PayloadJson=payloadJson; Dropped=dropped; } public long Cursor { get; } public string Sequence { get; } public string PatchId { get; } public string PayloadJson { get; } public long Dropped { get; } }

			sealed class HookDefinition {
				public string SessionId="",HookId="",Kind="",ModuleId="",Assembly="",DeclaringType="",Method="",Signature="",Mvid="",IlSha256="",ArrivalModuleId="",ImagePath="",RuntimeId="v4.0.30319"; public string? Source;
				public int ProcessId,MethodToken,ArrivalMethodToken,ArrivalIlOffset,MaximumEventsPerSecond=100,MaximumStringLength=1024,Revision; public int[] NearbyOffsets=Array.Empty<int>(); public long ProcessCreationTicks;
				public string Key=>HookLabService.Key(SessionId,ProcessId,HookId);
				public static HookDefinition Parse(JsonObject values) {
					var value=new HookDefinition { SessionId=Required(values,"session_id"),ProcessId=RequiredInt(values,"process_id"),HookId=Required(values,"hook_id"),Kind=Required(values,"kind"),ModuleId=Required(values,"module_id"),Assembly=Required(values,"assembly"),DeclaringType=Required(values,"declaring_type"),Method=Required(values,"method"),Signature=Required(values,"signature"),Mvid=Required(values,"module_mvid"),IlSha256=Required(values,"il_sha256"),MethodToken=RequiredInt(values,"method_token") };
					if(value.Kind!="Prefix" && value.Kind!="Postfix" && value.Kind!="Finalizer") throw new RpcException("invalid_arguments","kind must be Prefix, Postfix, or Finalizer.");
					value.Source=(string?)values["source"];
					if(value.Source is not null) { if(value.Kind!="Prefix"&&value.Kind!="Postfix") throw new RpcException("invalid_arguments","Custom source currently supports Prefix and Postfix only."); value.Revision=RequiredInt(values,"revision"); if(value.Revision<=0) throw new RpcException("invalid_arguments","revision must be positive."); }
					value.ArrivalModuleId=(string?)values["arrival_module_id"] ?? value.ModuleId; value.ArrivalMethodToken=(int?)values["arrival_method_token"] ?? value.MethodToken; value.ArrivalIlOffset=(int?)values["arrival_il_offset"] ?? 0;
					value.MaximumEventsPerSecond=Positive(values,"maximum_events_per_second",100); value.MaximumStringLength=Positive(values,"maximum_string_length",1024);
					value.NearbyOffsets=ProtocolJson.FromNode<int[]>(values["nearby_offsets"]) ?? Array.Empty<int>();
					try { using var process=Process.GetProcessById(value.ProcessId); value.ImagePath=process.MainModule?.FileName ?? process.ProcessName; value.ProcessCreationTicks=process.StartTime.ToUniversalTime().Ticks; }
					catch(Exception ex) { throw new RpcException("target_unavailable","Could not read target process identity: "+ex.Message); }
					return value;
				}
				public bool Equivalent(HookDefinition other)=>Kind==other.Kind && ModuleId==other.ModuleId && MethodToken==other.MethodToken && Mvid==other.Mvid && Signature==other.Signature && IlSha256==other.IlSha256;
				// The phase is compiled-hook behavior, not target identity. A higher revision may
				// replace Prefix with Postfix on the same exactly guarded method.
				public bool SameTarget(HookDefinition other)=>ModuleId==other.ModuleId && MethodToken==other.MethodToken && Mvid==other.Mvid && Signature==other.Signature && IlSha256==other.IlSha256;
				static int Positive(JsonObject values,string name,int fallback) { var value=(int?)values[name] ?? fallback; return value>0 ? value : throw new RpcException("invalid_arguments",name+" must be positive."); }
				public JsonObject Parameters(string completion) { var values=new JsonObject { ["host_id"]="dgspy-hooklab",["image_path"]=ImagePath,["process_id"]=ProcessId.ToString(CultureInfo.InvariantCulture),["process_creation_utc_ticks"]=ProcessCreationTicks.ToString(CultureInfo.InvariantCulture),["architecture"]="x64",["runtime_id"]=RuntimeId,["appdomain_id"]="1",["endpoint"]="pipe",["completion_path"]=completion,["hook_id"]=HookId,["hook_kind"]=Kind,["hook_assembly"]=Assembly,["hook_type"]=DeclaringType,["hook_method"]=Method,["hook_module_mvid"]=Mvid,["hook_metadata_token"]=MethodToken.ToString(CultureInfo.InvariantCulture),["hook_declaring_type"]=DeclaringType,["hook_method_signature"]=Signature,["hook_il_sha256"]=IlSha256,["maximum_events_per_second"]=MaximumEventsPerSecond.ToString(CultureInfo.InvariantCulture),["maximum_string_length"]=MaximumStringLength.ToString(CultureInfo.InvariantCulture) }; if(Source is not null) { values["hook_source_base64"]=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Source)); values["hook_revision"]=Revision.ToString(CultureInfo.InvariantCulture); } return values; }
			}
		}
	}
}
