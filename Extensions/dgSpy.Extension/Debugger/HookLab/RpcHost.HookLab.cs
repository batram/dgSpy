using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Extension.Debugger.AtomicActions;
using dgSpy.Extension.ToolWindows;
using dgSpy.Protocol;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;
using HookLab.Packaging;
using HookLab.Probe.CorDebug.Transport;
using dnlib.DotNet;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		readonly HookLabService hookLab=new HookLabService();

		Task<object> InitializeHookLabAsync(RpcRequest req,CancellationToken token) => hookLab.InitializeAsync(this,req,token);
		Task<object> GetHookLabReadinessAsync(RpcRequest req,CancellationToken token) => hookLab.ReadinessAsync(this,req,token);
		object GetHookLabStatus(RpcRequest req) { CheckSession(req); return hookLab.Status(RequiredSession(req),(int?)req.Arguments["process_id"]); }
		async Task<object> GetHookTemplateAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req);
			var template=(string?)req.Arguments["template"] ?? "PrefixPostfix";
			if(template!="Prefix"&&template!="Postfix"&&template!="PrefixPostfix"&&template!="Finalizer"&&template!="Transpiler") throw new RpcException("invalid_arguments","template must be Prefix, Postfix, PrefixPostfix, Finalizer, or Transpiler.");
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
		object ExportHookPackage(RpcRequest req) { CheckSession(req); return hookLab.Export(this,req); }
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
			readonly Dictionary<string,ResidentHookRecord> residentHooks=new Dictionary<string,ResidentHookRecord>(StringComparer.Ordinal);
			readonly Dictionary<string,RuntimeRecord> runtimes=new Dictionary<string,RuntimeRecord>(StringComparer.Ordinal);
			readonly List<HookEventRecord> events=new List<HookEventRecord>();
			readonly SemaphoreSlim initialization=new SemaphoreSlim(1,1);
			long cursor;

			/// <summary>Everything the contract needs, read from the target and the payload and deciding
			/// nothing. Both the acting path and the read-only probe call this, which is what stops a
			/// preflight from becoming a second implementation that drifts from the path it gates.
			///
			/// <para><b>Boundary.</b> This reads: debugger-published process facts, the target's SID, the
			/// prospective exchange plan - which creates nothing, by construction - and the shipped payload,
			/// which it opens and closes. It performs no remote allocation, starts no remote thread, stages
			/// nothing, creates no endpoint, mutates no ACL, writes nothing into the target's address
			/// space, and makes no filesystem change on this machine. <see cref="ReadinessAsync"/> depends
			/// on that and asserts it in test.</para></summary>
			async Task<(HookLabPreconditions.Facts Facts,HookLab.Injector.IdentityPair? Identities,HookLab.Injector.ExchangeAreaPlan? Exchange,(string? Name,string IdentityId) Domain)>
				GatherFactsAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				var processId=RequiredInt(source.Arguments,"process_id");
				var observed=await host.OnDebuggerAsync(()=>{
					var process=host.SelectProcess(source);
					var runtimes=process.Runtimes.Select(runtime=>new HookLabRuntimeIdentity(runtime.Guid,runtime.Name)).ToArray();
					var domains=process.Runtimes.SelectMany(runtime=>runtime.AppDomains).Select(domain=>(domain.Id,domain.Name)).ToArray();
					return (process.Bitness,Architecture:process.Architecture.ToString(),Runtimes:runtimes,Domains:domains);
				},token).ConfigureAwait(false);

				var facts=new HookLabPreconditions.Facts {
					ProcessId=processId,
					Bitness=observed.Bitness,
					Architecture=observed.Architecture,
					Runtimes=observed.Runtimes,
					ApplicationDomains=observed.Domains,
					RequestedApplicationDomain=RequestedApplicationDomain(source),
				};

				HookLab.Injector.IdentityPair? identities=null;
				try {
					identities=HookLab.Injector.IdentityPair.For(processId);
					facts.TargetIdentityKnown=identities.Target is not null;
					facts.CrossIdentity=identities.CrossIdentity;
					if(identities.Target is null) facts.IdentityFailureDetail="The target process identity could not be read, so no authority relationship with it can be derived.";
				}
				catch(InvalidOperationException ex) { facts.IdentityFailureDetail=ex.Message; }

				HookLab.Injector.ExchangeAreaPlan? exchange=null;
				if(identities is not null && identities.Target is not null) {
					// Planning only. ExchangeAreaTests asserts that this creates nothing, which is what lets
					// the probe answer both exchange rows without a filesystem change.
					exchange=HookLab.Injector.ExchangeAreaPlan.For(identities,"dgspy-hooklab-"+processId.ToString(CultureInfo.InvariantCulture));
					facts.ExchangePlanned=true;
					facts.ExchangeCreation=Outcome(exchange.CreationAuthorized);
					facts.ExchangeCreationDetail=exchange.CreationDetail;
				}

				try { using var payload=dgSpy.Extension.PayloadDelivery.HookLabPayloadResolver.Open(); facts.PayloadVerified=true; }
				catch(dgSpy.Extension.PayloadDelivery.HookLabPayloadRefusedException ex) { facts.PayloadFailureDetail=ex.Message; }
				catch(Exception ex) { facts.PayloadFailureDetail=ex.GetType().Name+": "+ex.Message; }

				var domain=facts.RequestedApplicationDomain is int requested && observed.Domains.Length>1 && observed.Domains.Any(value=>value.Id==requested)
					?(Name:(string?)observed.Domains.First(value=>value.Id==requested).Name,IdentityId:requested.ToString(CultureInfo.InvariantCulture))
					:(Name:(string?)null,IdentityId:DefaultApplicationDomainId);
				return (facts,identities,exchange,domain);
			}

			/// <summary>The injector's verdict in the contract's vocabulary. One place, and it throws on a
			/// value it does not recognize rather than mapping it to something plausible: the two enums
			/// exist separately only because the injector's assembly cannot be referenced from where the
			/// contract has to be testable.</summary>
			static PreconditionOutcome Outcome(HookLab.Injector.PreconditionResult result) => result switch {
				HookLab.Injector.PreconditionResult.Satisfied=>PreconditionOutcome.Satisfied,
				HookLab.Injector.PreconditionResult.Failed=>PreconditionOutcome.Failed,
				HookLab.Injector.PreconditionResult.NotProvablePreflight=>PreconditionOutcome.NotProvablePreflight,
				_=>throw new RpcException("internal_error","Unrecognized precondition result from the exchange area: "+result+"."),
			};

			/// <summary>The same computation the acting path is gated on, exposed read-only.
			///
			/// <para>It never reports that the payload will load. At its strongest it reports
			/// <c>no known incompatibility</c>, and every precondition it could not decide says so by
			/// name instead of being counted as a pass.</para></summary>
			public async Task<object> ReadinessAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				var gathered=await GatherFactsAsync(host,source,token).ConfigureAwait(false);
				var report=HookLabPreconditions.Evaluate(gathered.Facts);
				return new {
					process_id=gathered.Facts.ProcessId,
					refuses=report.Refuses,
					summary=report.Summary,
					refusal=report.FirstFailure is null?null:new { precondition=report.FirstFailure.Name,code=report.FirstFailure.RefusalCode,detail=report.FirstFailure.Detail },
					preconditions=report.Preconditions.Select(precondition=>new {
						name=precondition.Name,
						result=Wire(precondition.Result),
						detail=precondition.Detail,
						refusal_code=precondition.RefusalCode,
						// Stated for every row, including the rows that say no: "we could not tell, so we
						// continued" is a policy, and a policy nobody wrote down is how a refusal quietly
						// becomes a pass.
						refuses_when_unprovable=precondition.RefusesWhenUnprovable,
					}).ToArray(),
					application_domains=gathered.Facts.ApplicationDomains.Select(domain=>new { id=domain.Id,name=domain.Name }).ToArray(),
					// Named for what it is: where an area WOULD go. It does not exist, and it is not even
					// stable - each call plans a fresh one - so calling it exchange_area invites a reader
					// to believe the probe created something. Live on w3wp 6904 two consecutive calls
					// returned two different non-existent paths.
					planned_exchange_area=gathered.Exchange?.Root,
					target_unmodified=true,
				};
			}

			static string Wire(PreconditionOutcome outcome) => outcome switch {
				PreconditionOutcome.Satisfied=>"satisfied",
				PreconditionOutcome.Failed=>"failed",
				PreconditionOutcome.NotProvablePreflight=>"not_provable_preflight",
				_=>throw new RpcException("internal_error","Unrecognized precondition outcome: "+outcome+"."),
			};

			public async Task<object> InitializeAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				BindUi(host);
				var session=Required(source.Arguments,"session_id");
				var processId=RequiredInt(source.Arguments,"process_id");
				RuntimeRecord existing;
				lock(gate) if(runtimes.TryGetValue(RuntimeKey(session,processId),out existing)) { RefuseDomainMismatch(source,existing); HookLabUiBridge.SetInitialized(); return Initialized(existing,false); }
				await initialization.WaitAsync(token).ConfigureAwait(false);
				// Set when initialization fails and its staged files are kept. It is named in the error
				// rather than left for someone to find, because a preserved directory nobody is told
				// about is the same as a deleted one.
				string? preservedExchange=null;
				try {
					lock(gate) if(runtimes.TryGetValue(RuntimeKey(session,processId),out existing)) { RefuseDomainMismatch(source,existing); HookLabUiBridge.SetInitialized(); return Initialized(existing,false); }

				// The contract, evaluated before anything is created, injected or paused, and the mutation
				// below gated on its result. Same gathering and same evaluation as get_hooklab_readiness,
				// deliberately: a preflight that is a second implementation drifts from the path it gates,
				// which is a worse defect than the one it prevents.
				await host.OnDebuggerAsync(()=>{ host.CheckVersion(source); return 0; },token).ConfigureAwait(false);
				var gathered=await GatherFactsAsync(host,source,token).ConfigureAwait(false);
				var contract=HookLabPreconditions.Evaluate(gathered.Facts);
				if(contract.FirstFailure is HookLabPreconditions.Precondition refused)
					throw new RpcException(refused.RefusalCode ?? "unsupported_hooklab_target",refused.Detail);

				var target=await host.OnDebuggerAsync(()=>{
					var process=host.SelectProcess(source);
					// Guaranteed by the architecture and runtime preconditions above; a null here would mean
					// the contract and this path disagree about the same facts.
					var backend=HookLabBackends.Select(process.Bitness,process.Architecture.ToString(),gathered.Facts.Runtimes)
						?? throw new RpcException("unsupported_hooklab_target",HookLabBackends.UnsupportedReason(process.Bitness,process.Architecture.ToString(),gathered.Facts.Runtimes) ?? "The target became unsupported after its preconditions were evaluated.");
					// The backend says whether its runtime has one version or many. This used to be
					// "CoreCLR means read it from the process", restated here as a conditional.
					var runtimeId=backend.FixedRuntimeId ?? CoreClrRuntimeId(process.Id);
					if(String.IsNullOrWhiteSpace(runtimeId)) throw new RpcException("hooklab_runtime_identity_unavailable","The debugger did not publish the exact CoreCLR runtime version.");
					// Spell the application domain in the runtime's own terms, not the debugger's. The
					// resident asserts AppDomain.CurrentDomain.Id about itself and the guard compares the
					// two, so a value only the debugger understands is a guard that can only fail - which
					// is exactly what a Mono target did, refusing with "Expected '1', actual '0'".
					var domain=gathered.Domain;
					if(domain.IdentityId==DefaultApplicationDomainId) domain=(domain.Name,backend.RootApplicationDomainIdentity);
					else if(backend.DebuggerNumbersApplicationDomainsItself)
						throw new RpcException("hooklab_application_domain_unsupported",
							"This debugger engine numbers "+backend.Name+" application domains itself rather than reporting the runtime's own ids, "+
							"so a specific domain cannot be named for HookLab on it. Omit app_domain_id; a single-domain target needs no selection.");
					return (WasRunning:process.IsRunning,Backend:backend,RuntimeId:runtimeId,Domain:domain);
				},token).ConfigureAwait(false);
				var wasRunning=target.WasRunning;
				// One location derived from BOTH identities, rather than Path.GetTempPath() answering
				// "the debugger's" and saying nothing about the target. Against a target running as
				// another account the debugger's temp directory is unreadable, which is defect 3 of the
				// live incident: the staged payload was there, and the target could not see it.
				//
				// Read ONCE, here, and used for both the exchange area and the endpoint DACL below. Those
				// two decisions used to compute "who is the controller" independently, from two different
				// call sites, which is two chances to disagree about one fact.
				// Both come from the gathering the contract was evaluated over. Reading the identity again
				// here, or planning a second area, would be the same fact computed twice from two call
				// sites - which is the defect 8402ead17 removed from this very method. Non-null is
				// guaranteed: identity.target_readable failed above if the target's SID was unreadable, and
				// an area is planned whenever it was readable.
				var identities=gathered.Identities!;
				var exchange=gathered.Exchange!.Materialize();
				var completion=Path.Combine(exchange.Path,"completion.txt");
				var initializationSucceeded=false;
				ProbeConnection? connection=null; byte[]? endpointSecret=null;
				try {
					var runtimeId=target.RuntimeId;
					var identity=TargetIdentity(source.Arguments,processId,completion,runtimeId,target.Domain.Name,target.Domain.IdentityId);
					// SameTarget here, with the domain: adoption must not hand back a resident living in a
					// different application domain than the one this operation is for.
					var live=LiveTarget(processId,DgSpyStateRoot.ResidentHostId,runtimeId,target.Domain.IdentityId); var store=new ProbeDiscoveryStore(DgSpyStateRoot.SharedResidentRoot()); var discovered=store.DiscoverStrict(new ExtensionLiveTargets(runtimeId),DateTime.UtcNow).Where(value=>SameTarget(value.Target,live)).ToArray();
					if(discovered.Length>1) throw new RpcException("hooklab_resident_ambiguous","Multiple authenticated HookLab residents name this exact target.");
					Dictionary<string,string> completionReport;
					if(discovered.Length==1) {
						var health=store.VerifyHealthAndRefresh(discovered[0],2000); var adopted=discovered[0];
						connection=new ProbeConnection(adopted.PipeName,adopted.Secret,adopted.EndpointNonce);
						var adoptedRuntime=new RuntimeRecord(session,processId,connection,health.Status.ExpectedHooksVersion??0,adopted.ProbeInstanceId,true,target.Domain.IdentityId); connection=null; Inventory(adoptedRuntime,health.Status.PayloadJson);
						lock(gate) runtimes.Add(RuntimeKey(session,processId),adoptedRuntime); StartEventPump(adoptedRuntime); HookLabUiBridge.SetInitialized(); return Initialized(adoptedRuntime,true,adopted:true);
					}
					endpointSecret=ProbeAuthentication.CreateSecret(); identity["endpoint_secret_base64"]=Convert.ToBase64String(endpointSecret);
					// Who will be opening the control pipe, from the same identity pair that placed the
					// exchange area. The probe protects that pipe's DACL and names only its own SID, so a
					// target running as another account builds an endpoint this host cannot open; named
					// here, it is granted. Null when the accounts match, which the probe would ignore
					// anyway, so the same-user DACL is exactly what it always was.
					if(identities.ControllerSidForEndpoint is string controllerSid) identity["controller_sid"]=controllerSid;
					// The block is complete here, and this is the last point before it is staged where a key
					// the payload cannot parse is still cheap to refuse.
					dgSpy.Extension.Debugger.AtomicActions.PayloadActionRequest.EnsureKnownParameterKeys(identity.Select(pair=>pair.Key));
					try {
						// Dispatch on the backend's declared arrival mode. The mechanisms genuinely differ and
						// both need host services, so this stays one switch in one place rather than being
						// hidden behind indirection - but it is the only place left that knows which is which.
						if(target.Backend.Arrival==HookLabArrival.DebuggerEvaluation) {
							completionReport=await ExecuteInitializationOperationAsync(host,source,PayloadOperation.initialize,identity,token,true,
								carrierDomainIsRuntimeDomain:!target.Backend.DebuggerNumbersApplicationDomainsItself).ConfigureAwait(false);
							// The report is written by a worker thread INSIDE the target, so the target has
							// to be running to produce it. Waiting for it while the process is stopped is a
							// deadlock that the deadline breaks rather than a slow operation.
							//
							// Measured on the cross-identity CoreCLR fixture, which failed roughly two runs
							// in five: the target's own loop stopped for 22.73 s while passing runs paused
							// 1.8 s, and the resident reported worker_queue_ms just over 20 s with
							// status=ok - it was scheduled the moment the host gave up and resumed. The
							// CLR v4 branch below has always resumed before its wait; this one did not.
							//
							// Preserve the ordinary manager-owned continue path when its state is honest, then
							// reconcile the CorDebug engine in case func-eval left the two states split. Both
							// operations are idempotent against an already-running target.
							await ResumeAsync(host,source,token).ConfigureAwait(false);
							await ReconcileCoreClrRunAsync(host,source,token).ConfigureAwait(false);
							completionReport=await ReadCompletionAsync(completion,token).ConfigureAwait(false);
						}
						else { await ResumeAsync(host,source,token).ConfigureAwait(false); completionReport=await InitializeAutonomouslyAsync(processId,identity,exchange.Path,token).ConfigureAwait(false); }
					}
					finally { }
					if(!String.Equals(completionReport.TryGetValue("status",out var completedStatus)?completedStatus:null,"ok",StringComparison.Ordinal))
						throw new RpcException("hooklab_initialization_failed","HookLab worker reported: "+String.Join("; ",completionReport.Select(pair=>pair.Key+"="+pair.Value)));
					if(target.Backend.SynchronizesAfterArrival) await SynchronizeCoreClrAsync(host,source,token).ConfigureAwait(false);
					var pipe=Required(completionReport,"pipe_name","HookLab initialization did not report its control pipe."); var nonce=Convert.FromBase64String(Required(completionReport,"pipe_nonce_base64","HookLab initialization did not report its endpoint nonce.")); var probe=Required(completionReport,"probe_instance_id","HookLab initialization did not report its probe identity.");
					// Recorded the moment the resident's endpoint identity is known, and before anything is
					// attempted against it. This used to sit after the connection succeeded, which meant any
					// failure between here and there - a refused authentication, a pipe that vanished - left
					// a resident that was up, authenticated and listening, with the only credential to it
					// zeroed in the finally below and no record anywhere. Unreachable by adoption, by a later
					// initialize_hooklab, by anything, until the target exited.
					//
					// Writing it first cannot describe a resident that does not exist: the report this reads
					// is the resident's own, published after its listener was up. The worst case is a record
					// for a resident that later dies, which is exactly what DiscoverStrict and the record's
					// expiry already handle.
					store.Write(new ProbeDiscoveryRecord(live,probe,pipe,nonce,endpointSecret,1,DateTime.UtcNow.Add(ProbeDiscoveryStore.DiscoveryRecordLifetime)));
					connection=new ProbeConnection(pipe,endpointSecret,nonce);
					var runtime=new RuntimeRecord(session,processId,connection,Long(completionReport,"hooks_version"),probe,false,target.Domain.IdentityId);
					var status=await SendRawAsync(runtime,"status","{}",token).ConfigureAwait(false); Inventory(runtime,status);
					lock(gate) runtimes.Add(RuntimeKey(session,processId),runtime);
					connection=null;
					StartEventPump(runtime);
					HookLabUiBridge.SetInitialized();
					initializationSucceeded=true;
					return Initialized(runtime,true,adopted:false);
				}
				finally {
					if(endpointSecret is not null) Array.Clear(endpointSecret,0,endpointSecret.Length);
					connection?.Dispose();
					// Preserve on ambiguity. This used to delete unconditionally, which destroyed the
					// staged files every time initialization failed - precisely when they were the only
					// record of what the target had been offered, and precisely what the live
					// investigation needed and did not have.
					if(!initializationSucceeded) { exchange.Preserve(); preservedExchange=exchange.Path; }
					exchange.Dispose();
					if(wasRunning) await ResumeAsync(host,source,CancellationToken.None).ConfigureAwait(false);
					else await EnsurePausedAsync(host,source,CancellationToken.None).ConfigureAwait(false);
				}
				}
				catch(RpcException ex) { throw preservedExchange is null?ex:new RpcException(ex.Code,ex.Message+" The staged files were preserved at "+preservedExchange+"."); }
				catch(Exception ex) { throw new RpcException("hooklab_initialization_failed",ex.GetType().Name+": "+ex.Message+(preservedExchange is null?"":" The staged files were preserved at "+preservedExchange+".")); }
				finally { initialization.Release(); }
			}

			// Stages into the exchange area rather than into a second directory of its own, so the payload
			// the target must read and the completion report it must write share one location with one
			// authored DACL. It does not own that directory and does not remove it; the caller does.
			static async Task<Dictionary<string,string>> InitializeAutonomouslyAsync(int processId,JsonObject parameters,string staging,CancellationToken token) {
				Directory.CreateDirectory(staging);
				{
					using var payload=dgSpy.Extension.PayloadDelivery.HookLabPayloadResolver.Open();
					var nativeSource=Path.Combine(payload.HostRoot,"hooklab","HookLab.NativeBootstrap.x64.dll");
					if(!File.Exists(nativeSource)) throw new RpcException("hooklab_native_initializer_missing","The installed host does not contain the HookLab x64 initializer. Rebuild or reinstall dgSpy.");
					var nativePath=Path.Combine(staging,"HookLab.NativeBootstrap.x64.dll");
					File.Copy(nativeSource,nativePath,false);
					File.Copy(payload.PayloadPath,Path.Combine(staging,"HookLab.Bootstrap.dll"),false);
					File.WriteAllText(Path.Combine(staging,"initialize.params"),ParameterText(parameters),new System.Text.UTF8Encoding(false));
					// Fully qualified rather than a using: HookLab.Injector also declares a HookDefinition,
					// and this file resolves that name against HookLab.Contracts. Importing the namespace
					// here would make every HookDefinition in the file ambiguous.
					HookLab.Injector.RemoteLibraryLoader.Load(processId,nativePath);
					var completion=Required(parameters,"completion_path");
					var report=await ReadCompletionAsync(completion,token).ConfigureAwait(false);
					if(!String.Equals(report.TryGetValue("status",out var status)?status:null,"ok",StringComparison.Ordinal))
						throw new RpcException("hooklab_initialization_failed","HookLab worker reported: "+String.Join("; ",report.Select(pair=>pair.Key+"="+pair.Value)));
					return report;
				}
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

				var parameters=definition.Parameters("unused",HookOwnership.Qualify(HookOwnership.DgSpyController,definition.HookId));
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
				HookRecord? record; ResidentHookRecord? adopted; lock(gate) { hooks.TryGetValue(Key(session,process,hookId),out record); adopted=record is null?OwnedResident(session,process,hookId):null; }
				if(record is null&&adopted is null) throw new RpcException("hook_not_owned","Hook ID '"+hookId+"' is absent or owned by another controller.");
				if(record is null) { if(adopted!.Enabled==enabled) return new { hook=View(adopted),changed=false }; var adoptedRuntime=ForOperation(source.Arguments); await SendAsync(adoptedRuntime,enabled?"enable":"disable",adopted.PatchId,token).ConfigureAwait(false); Inventory(adoptedRuntime,await SendRawAsync(adoptedRuntime,"status","{}",token).ConfigureAwait(false)); return new { hook=View(OwnedResident(session,process,hookId)!),changed=true }; }
				if(record.Enabled==enabled) return new { hook=View(record),changed=false };
				var runtime=ForOperation(source.Arguments);
				await SendAsync(runtime,enabled?"enable":"disable",record.PatchId,token).ConfigureAwait(false);
				lock(gate) record.Enabled=enabled;
				HookLabUiBridge.PublishHook(session,process,hookId,record.Definition.Kind,record.Definition.DeclaringType+"."+record.Definition.Method,record.PatchId,record.Definition.Revision,record.Definition.Source is not null,record.Definition.Source,enabled,record.MethodDefinition);
				return new { hook=View(record),changed=true };
			}

			public object List(string sessionId,int? processId) {
				lock(gate) {
					var selected=hooks.Values.Where(value=>value.Definition.SessionId==sessionId && (processId is null || value.Definition.ProcessId==processId.Value)).ToArray(); var known=new HashSet<string>(selected.Select(value=>value.PatchId),StringComparer.Ordinal); var local=selected.Select(value=>(object)View(value)); var resident=residentHooks.Values.Where(value=>value.SessionId==sessionId&&(processId is null||value.ProcessId==processId.Value)&&!known.Contains(value.PatchId)).Select(value=>(object)View(value));
					// From the last status round trip rather than a fresh one: list_hooks is the cheap
					// read, and get_hook_events refreshes this exactly when its emptiness raises the
					// question. Reported here too because this is where someone looks next.
					var shadowed=runtimes.Values.Where(value=>value.SessionId==sessionId&&(processId is null||value.ProcessId==processId.Value)).SelectMany(ShadowedView).ToArray();
					return new { hooks=local.Concat(resident).ToArray(),shadowed_hooks=shadowed };
				}
			}

			public object Export(RpcHost host,RpcRequest source) {
				var session=Required(source.Arguments,"session_id"); var processId=RequiredInt(source.Arguments,"process_id"); var hookId=Required(source.Arguments,"hook_id");
				HookRecord record; lock(gate) if(!hooks.TryGetValue(Key(session,processId,hookId),out record!)) throw new RpcException("hook_not_found","Hook ID '"+hookId+"' is not installed in this process.");
				var definition=record.Definition; if(definition.Source is null) throw new RpcException("hook_not_exportable","Only compiled HookLab hooks with retained source can be exported.");
				try { using var process=Process.GetProcessById(processId); if(process.StartTime.ToUniversalTime().Ticks!=definition.ProcessCreationTicks) throw new RpcException("target_identity_mismatch","The HookLab target PID was reused."); var image=process.MainModule?.FileName??process.ProcessName; if(!String.Equals(Path.GetFullPath(image),Path.GetFullPath(definition.ImagePath),StringComparison.OrdinalIgnoreCase)) throw new RpcException("target_identity_mismatch","The HookLab target image changed."); }
				catch(RpcException) { throw; } catch(Exception ex) { throw new RpcException("target_unavailable","Could not verify the live HookLab target: "+ex.Message); }
				var configured=Environment.GetEnvironmentVariable("DGSPY_EXPORT_ROOT"); if(String.IsNullOrWhiteSpace(configured)) throw new RpcException("capability_unavailable","Set DGSPY_EXPORT_ROOT to enable HookLab package export.");
				var root=Path.GetFullPath(configured!); var requested=Required(source.Arguments,"output_path"); var target=Path.GetFullPath(Path.IsPathRooted(requested)?requested:Path.Combine(root,requested)); var prefix=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
				if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new RpcException("path_not_allowed","The HookLab export path is outside DGSPY_EXPORT_ROOT."); RejectReparsePath(root,Path.GetDirectoryName(target)!); if(Directory.Exists(target)&&(File.GetAttributes(target)&System.IO.FileAttributes.ReparsePoint)!=0) throw new RpcException("path_not_allowed","The HookLab export path is a reparse point.");
				var overwrite=(bool?)source.Arguments["overwrite"]??false; if((Directory.Exists(target)||File.Exists(target))&&!overwrite) throw new RpcException("file_exists","HookLab export already exists: "+target);
				HookPackageExportResult result;
				try { result=HookPackageExporter.Export(target,new HookPackageExportRequest { PackageId=Required(source.Arguments,"package_id"),ProfileId=Required(source.Arguments,"profile_id"),DisplayName=(string?)source.Arguments["display_name"]??hookId,PackageRevision=definition.Revision,ProcessFileName=Path.GetFileName(definition.ImagePath),PermittedExecutablePath=Path.GetFullPath(definition.ImagePath),Assembly=definition.Assembly,ModuleMvid=definition.Mvid,DeclaringType=definition.DeclaringType,Method=definition.Method,MetadataToken=definition.MethodToken,Signature=definition.Signature,IlSha256=definition.IlSha256,HookId=definition.HookId,HookKind=definition.Kind,HookRevision=definition.Revision,HookEnabled=record.Enabled,Source=definition.Source,MaximumEventsPerSecond=definition.MaximumEventsPerSecond,MaximumStringLength=definition.MaximumStringLength,NotificationPolicy=(string?)source.Arguments["notification_policy"]??"errors",ClrReadinessTimeoutMs=(int?)source.Arguments["clr_readiness_timeout_ms"]??5000,InitializationTimeoutMs=(int?)source.Arguments["initialization_timeout_ms"]??10000 },overwrite,ProtectExportTree); }
				catch(InvalidDataException ex) { throw new RpcException("invalid_arguments",ex.Message); }
				var audit=host.AuditMutation(source.Operation,"hook_id="+hookId+" package_id="+Required(source.Arguments,"package_id")+" path="+result.DeploymentPath+" digest="+result.PackageDigest);
				return new { hook_id=hookId,package_id=Required(source.Arguments,"package_id"),profile_id=Required(source.Arguments,"profile_id"),deployment_path=result.DeploymentPath,package_path=result.PackagePath,profile_path=result.ProfilePath,package_digest=result.PackageDigest,profile_enabled=false,audit_id=audit };
			}
			static void ProtectExportTree(string root) {
				var identities=new IdentityReference[]{WindowsIdentity.GetCurrent().User??throw new UnauthorizedAccessException("Current Windows identity has no SID."),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)};
				var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false); foreach(var identity in identities) security.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow)); SetDirectorySecurity(root,security);
				foreach(var directory in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories)) { var value=new DirectorySecurity(); value.SetAccessRuleProtection(false,false); SetDirectorySecurity(directory,value); }
				foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) { var value=new FileSecurity(); value.SetAccessRuleProtection(false,false); SetFileSecurity(file,value); }
			}
			static void SetDirectorySecurity(string path,DirectorySecurity security) {
#if NETFRAMEWORK
				new DirectoryInfo(path).SetAccessControl(security);
#else
				FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path),security);
#endif
			}
			static void SetFileSecurity(string path,FileSecurity security) {
#if NETFRAMEWORK
				new FileInfo(path).SetAccessControl(security);
#else
				FileSystemAclExtensions.SetAccessControl(new FileInfo(path),security);
#endif
			}

			public async Task<object> ReadEventsAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				var runtime=ForOperation(source.Arguments);
				var maximum=Math.Min(256,Math.Max(1,(int?)source.Arguments["max_events"] ?? 64));
				var dropped=await DrainIntoHostAsync(runtime,maximum,token).ConfigureAwait(false);
				object[] page; long next;
				lock(gate) {
					var after=(long?)source.Arguments["after_cursor"] ?? 0;
					page=events.Where(value=>value.Cursor>after).Take(maximum).Select(value=>(object)new { cursor=value.Cursor,sequence=value.Sequence,patch_id=value.PatchId,payload_json=value.PayloadJson,dropped=value.Dropped }).ToArray();
					next=cursor;
				}
				// An empty page is the symptom a shadowed hook produces, and it is indistinguishable from a
				// method that is simply not being called - so this is the moment to ask the resident what it
				// knows, and the one call where a round trip is worth its cost. Twice on 2026-08-20 an
				// enabled hook with every guard valid observed nothing because ASP.NET had recompiled the
				// page and loaded a second assembly beside the one it patched.
				if(page.Length==0) {
					try { var status=await SendRawAsync(runtime,"status","{}",token).ConfigureAwait(false); Inventory(runtime,status); }
					// Best effort: a status round trip that fails must not turn a successful, empty drain
					// into an error. The events are the answer; this is commentary on their absence.
					catch(Exception) { }
				}
				return new { events=page,next_cursor=next,dropped,shadowed_hooks=ShadowedView(runtime) };
			}

			/// <summary>What the resident last reported about hooks whose target assembly has been
			/// superseded. Names the hook, the type, and the assembly that took it over, because "no
			/// events" plus a valid-looking hook is a dead end without all three.</summary>
			static object[] ShadowedView(RuntimeRecord runtime) {
				lock(runtime.Shadowed) return runtime.Shadowed.OrderBy(entry=>entry.Key,StringComparer.Ordinal).Select(entry=>(object)new {
					patch_id=entry.Key,
					declaring_type=entry.Value.DeclaringType,
					shadowing_assembly=entry.Value.ShadowingAssembly,
					detail="This hook is installed on a method in an assembly that has since been superseded: '"+entry.Value.DeclaringType+
						"' is now also defined by "+entry.Value.ShadowingAssembly+", so calls reach that copy and this hook observes nothing. "+
						"Its guards are still valid - it patches code nothing enters any more. Install against the newer module.",
				}).ToArray();
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
				HookRecord? record; ResidentHookRecord? adopted; lock(gate) { hooks.TryGetValue(Key(session,process,hookId),out record); adopted=record is null?OwnedResident(session,process,hookId):null; }
				if(record is null&&adopted is null) return new { hook_id=hookId,removed=false,reason="not_owned" };
				var runtime=ForOperation(source.Arguments);
				var patchId=record?.PatchId??adopted!.PatchId; await SendAsync(runtime,"uninstall",patchId,token).ConfigureAwait(false);
				// Events queued before the unpatch are not evidence of calls after removal. Drain that bounded
				// backlog before returning so the response is the cursor boundary after which every event would
				// necessarily have been produced by a still-live patch.
				await DrainRemovalBacklogAsync(runtime,token).ConfigureAwait(false);
				lock(gate) { if(record is not null) hooks.Remove(record.Definition.Key); if(adopted is not null) residentHooks.Remove(adopted.Key); } HookLabUiBridge.RemoveHook(session,process,hookId);
				return new { hook_id=hookId,patch_id=patchId,removed=true };
			}

			public async Task<object> RemoveAllAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				host.CheckSession(source);
				var session=Required(source.Arguments,"session_id"); var process=RequiredInt(source.Arguments,"process_id");
				HookRecord[] selected; ResidentHookRecord[] adopted; lock(gate) { selected=hooks.Values.Where(value=>value.Definition.SessionId==session && value.Definition.ProcessId==process).ToArray(); adopted=residentHooks.Values.Where(value=>value.SessionId==session&&value.ProcessId==process&&value.Controller==HookOwnership.DgSpyController&&!selected.Any(local=>local.PatchId==value.PatchId)).ToArray(); }
				var removed=new List<string>();
				var runtime=selected.Length+adopted.Length==0 ? null : ForOperation(source.Arguments);
				foreach(var record in selected) {
					await SendAsync(runtime!,"uninstall",record.PatchId,token).ConfigureAwait(false); removed.Add(record.Definition.HookId);
					lock(gate) hooks.Remove(record.Definition.Key); HookLabUiBridge.RemoveHook(session,process,record.Definition.HookId);
				}
				foreach(var record in adopted) { await SendAsync(runtime!,"uninstall",record.PatchId,token).ConfigureAwait(false); removed.Add(record.HookId); lock(gate) residentHooks.Remove(record.Key); }
				if(runtime is not null) await DrainRemovalBacklogAsync(runtime,token).ConfigureAwait(false);
				return new { removed=removed.ToArray(),removed_count=removed.Count };
			}
			ResidentHookRecord? OwnedResident(string session,int process,string hookId)=>residentHooks.Values.SingleOrDefault(value=>value.SessionId==session&&value.ProcessId==process&&value.Controller==HookOwnership.DgSpyController&&value.HookId==hookId);

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
			async Task<string> SendRawAsync(RuntimeRecord runtime,string operation,string payload,CancellationToken token) {
				await runtime.OperationGate.WaitAsync(token).ConfigureAwait(false);
				try { var response=await Task.Run(()=>runtime.Connection.Send(new ProbeMessage(1,ProbeMessageKind.Request,Guid.NewGuid().ToString("N"),operation,payload,runtime.HooksVersion)),token).ConfigureAwait(false); if(response.Operation=="error") throw new RpcException("hook_operation_failed","HookLab "+operation+" failed: "+response.PayloadJson); if(response.ExpectedHooksVersion.HasValue) runtime.HooksVersion=response.ExpectedHooksVersion.Value; return response.PayloadJson; }
				finally { runtime.OperationGate.Release(); }
			}
			void Inventory(RuntimeRecord runtime,string payload) {
				using(var document=JsonDocument.Parse(payload)) { var root=document.RootElement; if(root.GetProperty("probe_instance_id").GetString()!=runtime.ProbeInstanceId) throw new RpcException("hooklab_resident_identity_mismatch","Authenticated resident reported a different probe identity."); runtime.HooksVersion=root.GetProperty("hooks_version").GetInt64(); var values=new List<ResidentHookRecord>(); foreach(var item in root.GetProperty("compiled_hooks").EnumerateArray()) { var patch=item.GetProperty("patch_id").GetString()!; HookOwnership.TryParse(runtime.ProbeInstanceId,patch,out var controller,out var hookId); values.Add(new ResidentHookRecord(runtime.SessionId,runtime.ProcessId,patch,controller,hookId,item.TryGetProperty("assembly_simple_name",out var assembly)?assembly.GetString()!:String.Empty,item.GetProperty("kind").GetString()!,item.GetProperty("module_mvid").GetString()!,item.GetProperty("metadata_token").GetInt32(),item.GetProperty("declaring_type").GetString()!,item.GetProperty("signature").GetString()!,item.GetProperty("il_sha256").GetString()!,item.GetProperty("source_sha256").GetString()!,item.GetProperty("revision").GetInt32(),item.GetProperty("enabled").GetBoolean())); } lock(gate) { foreach(var key in residentHooks.Where(value=>value.Value.SessionId==runtime.SessionId&&value.Value.ProcessId==runtime.ProcessId).Select(value=>value.Key).ToArray()) residentHooks.Remove(key); foreach(var value in values) residentHooks[value.Key]=value; }
					// TryGetProperty, not GetProperty: a resident from before this existed reports no such
					// field, and an adopted one is exactly the case where the two builds can differ.
					runtime.Shadowed.Clear();
					if(root.TryGetProperty("shadowed_hooks",out var shadowedHooks) && shadowedHooks.ValueKind==JsonValueKind.Array)
						foreach(var item in shadowedHooks.EnumerateArray())
							runtime.Shadowed[item.GetProperty("patch_id").GetString()!]=(item.GetProperty("declaring_type").GetString()!,item.GetProperty("shadowing_assembly").GetString()!);
				}
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
				lock(gate) { active=runtimes.Values.ToArray(); runtimes.Clear(); hooks.Clear(); residentHooks.Clear(); events.Clear(); cursor=0; }
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

			/// <param name="carrierDomainIsRuntimeDomain">True when the debugger's application domain ids
			/// are the runtime's own, so the carrier's domain may be handed to the resident as an identity.
			/// False for an engine that numbers domains itself, where the identity already computed from
			/// the backend is the only value the target can verify - overwriting it with the debugger's
			/// ordinal is what made a Mono arrival refuse its own guard.</param>
			async Task<Dictionary<string,string>> ExecuteInitializationOperationAsync(RpcHost host,RpcRequest source,PayloadOperation operation,JsonObject? parameters,CancellationToken token,bool requireCarrier=false,bool carrierDomainIsRuntimeDomain=true) {
				if(!requireCarrier && await TryReachEvaluablePauseAsync(host,source,token).ConfigureAwait(false)) return await EvaluatePayloadAsync(host,source,operation,parameters,token).ConfigureAwait(false);
				await EnsurePausedAsync(host,source,token).ConfigureAwait(false);
				var carriers=await SelectInternalCarriersAsync(host,source,token).ConfigureAwait(false);
				AtomicActionResult? lastResult=null;
				foreach(var carrier in carriers) {
					if(parameters is not null && carrierDomainIsRuntimeDomain) parameters["appdomain_id"]=carrier.AppDomainId;
					var result=await RunInitializationPayloadAsync(host,source,carrier,operation,parameters,token,requireCarrier).ConfigureAwait(false);
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

			async Task<AtomicActionResult> RunInitializationPayloadAsync(RpcHost host,RpcRequest source,HookCarrier carrier,PayloadOperation operation,JsonObject? parameters,CancellationToken token,bool runAllThreads) {
				var arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["process_id"]=RequiredInt(source.Arguments,"process_id"),["module_id"]=carrier.ModuleId,["operation_version"]=1,["action_kind"]="payload",["payload_operation"]=operation.ToString(),["timeout_ms"]=60000,["run_all_threads"]=runAllThreads };
				if(parameters is not null) arguments["payload_parameters"]=parameters.DeepClone();
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
				request.Arguments["run_all_threads"]=true;
				var result=await host.InvokeExpressionAsync(request,"hooklab_initialization",token).ConfigureAwait(false);
				if(!result.Completed || result.Value?.Value is not string text) throw new RpcException("hooklab_evaluation_failed",result.Error ?? "HookLab initialization returned no report.");
				return text;
			}

			static JsonObject TargetIdentity(JsonObject source,int processId,string completion,string runtimeId,string? applicationDomainName=null,string applicationDomainId=DefaultApplicationDomainId) {
				try {
					using var process=Process.GetProcessById(processId);
					var identity=new JsonObject { ["host_id"]=DgSpyStateRoot.ResidentHostId,["image_path"]=process.MainModule?.FileName ?? process.ProcessName,["process_id"]=processId.ToString(CultureInfo.InvariantCulture),["process_creation_utc_ticks"]=process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),["architecture"]="x64",["runtime_id"]=runtimeId,["appdomain_id"]=applicationDomainId,["endpoint"]="pipe",["completion_path"]=completion };
					// Which application domain the native bootstrap should enter, by friendly name.
					//
					// Name and not id, because the native selector can only read a name: measured, the
					// COM _AppDomain interface has no get_Id - it is the .NET 1.x class interface and
					// AppDomain.Id arrived in 2.0. dnSpy's DbgAppDomain has both, so the translation
					// happens here, where both are available, rather than reflectively in native code.
					//
					// Absent means the default domain, which is what ExecuteInDefaultAppDomain already
					// does, so a single-domain target produces byte-identical parameters to before.
					//
					// appdomain_id above carries the domain actually chosen, and keeps its historical "1"
					// only when none was - see ResolveApplicationDomain. It has to be the real one: the
					// resident measures its own domain with AppDomain.CurrentDomain.Id and MethodGuards
					// compares the two, so a hardcoded "1" would refuse every hook installed from a
					// secondary domain. The model is still one resident per process; this only chooses
					// where it lives.
					if(!String.IsNullOrEmpty(applicationDomainName)) identity["appdomain_name"]=applicationDomainName;
					return identity;
				}
				catch(RpcException) { throw; }
				catch(Exception ex) { throw new RpcException("target_unavailable","Could not read target process identity: "+ex.Message); }
			}

			/// <summary>Which application domain holds the code the caller intends to hook.
			///
			/// Returns null for "the default domain", which is the shipped behaviour and needs no
			/// selection. Returns a friendly name when a specific domain must be entered.
			///
			/// The refusal in the middle is the point of the whole road. A process with several
			/// application domains and no domain named is exactly the live IIS case: defaulting silently
			/// puts the resident in the default domain, where the application's assemblies are not, and
			/// the failure surfaces much later as "No loaded assembly matches hook_assembly". Naming the
			/// domains here turns that into one sentence, before anything is injected.</summary>
			/// <summary>The application domain the caller asked for, or null when they named none. Parsed
			/// without touching the debugger so the already-initialized fast path can use it too.</summary>
			static int? RequestedApplicationDomain(RpcRequest source) {
				if(!source.Arguments.TryGetPropertyValue("app_domain_id",out var node)||node is null) return null;
				if(!Int32.TryParse(node.ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var value))
					throw new RpcException("invalid_arguments","app_domain_id must be the numeric application domain id that list_modules reports for the module you intend to hook.");
				return value;
			}

			/// <summary>The domain to enter, and the value that identifies it in the recorded target
			/// identity.
			///
			/// <para>The identity keeps its historical <c>"1"</c> whenever no specific domain was chosen,
			/// which is every target that works today. Identity is used for matching rather than for
			/// display, so what matters is that the recorded value and the live value are computed by the
			/// same rule - and holding the old rule for the old case means no existing record stops
			/// matching.</para></summary>
			static (string? Name,string IdentityId) ResolveApplicationDomain(RpcRequest source,dnSpy.Contracts.Debugger.DbgProcess process) {
				var domains=process.Runtimes.SelectMany(runtime=>runtime.AppDomains).ToArray();
				var requested=RequestedApplicationDomain(source);
				var describe=new Func<string>(()=>String.Join(", ",domains.Select(domain=>domain.Id.ToString(CultureInfo.InvariantCulture)+"="+domain.Name)));
				if(requested is null) {
					if(domains.Length<=1) return (null,DefaultApplicationDomainId);
					throw new RpcException("hooklab_application_domain_required",
						"This process runs code in "+domains.Length.ToString(CultureInfo.InvariantCulture)+" application domains, so HookLab will not guess which one to enter. "+
						"Pass app_domain_id naming the domain that holds the code you intend to hook; list_modules reports it per module. Domains: "+describe());
				}
				var selected=domains.Where(domain=>domain.Id==requested.Value).ToArray();
				if(selected.Length==0)
					throw new RpcException("hooklab_application_domain_not_found","No application domain with id "+requested.Value.ToString(CultureInfo.InvariantCulture)+" is loaded in this process. Domains: "+describe());
				var name=selected[0].Name;
				// The resident is selected by name in the target, so two domains sharing one is a refusal
				// rather than a coin flip.
				if(domains.Count(domain=>String.Equals(domain.Name,name,StringComparison.Ordinal))>1)
					throw new RpcException("hooklab_application_domain_ambiguous","More than one application domain in this process is named '"+name+"', and the resident selects its domain by name. Domains: "+describe());
				// A single-domain process needs no selection at all, so it keeps the shipped path exactly.
				return domains.Length<=1
					?(null,DefaultApplicationDomainId)
					:(name,requested.Value.ToString(CultureInfo.InvariantCulture));
			}

			/// <summary>What the recorded identity has always carried for "the domain we entered", back
			/// when the native path could only ever enter the default one.</summary>
			const string DefaultApplicationDomainId="1";

			/// <summary>Refuses when this process already has a resident and the caller named a different
			/// application domain.
			///
			/// <para>The model is still one resident per process, so the alternative to refusing is
			/// handing back a resident in a domain the caller did not ask for and letting them install
			/// hooks that can never bind - which is the failure this road exists to remove, moved one step
			/// later. Naming no domain still returns the existing resident: there is nothing to choose
			/// when it already exists.</para>
			///
			/// <para>One resident per domain is the obvious next step and is deliberately not taken here.
			/// It needs a domain in the runtime key and therefore in every operation that names a process,
			/// which is a surface change rather than a keying change.</para></summary>
			static void RefuseDomainMismatch(RpcRequest source,RuntimeRecord existing) {
				var requested=RequestedApplicationDomain(source);
				if(requested is null) return;
				var wanted=requested.Value.ToString(CultureInfo.InvariantCulture);
				if(String.Equals(wanted,existing.AppDomainId,StringComparison.Ordinal)) return;
				throw new RpcException("hooklab_application_domain_conflict",
					"HookLab is already initialized in this process in application domain "+existing.AppDomainId+
					", and this request names domain "+wanted+". One resident per process is supported, so remove the existing "+
					"resident before initializing in a different domain.");
			}
			static string CoreClrRuntimeId(int processId) {
				try {
					using var process=Process.GetProcessById(processId);
					var modules=process.Modules.Cast<ProcessModule>().Where(module=>String.Equals(module.ModuleName,"coreclr.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
					if(modules.Length!=1) throw new InvalidOperationException("Expected exactly one loaded coreclr.dll; found "+modules.Length.ToString(CultureInfo.InvariantCulture)+".");
					var runtimeVersionText=Path.GetFileName(Path.GetDirectoryName(modules[0].FileName));
					if(!System.Version.TryParse(runtimeVersionText,out _)) throw new InvalidOperationException("The loaded CoreCLR runtime directory does not carry a version: "+modules[0].FileName);
					return "v"+runtimeVersionText;
				}
				catch(Exception ex) when(ex is not RpcException) { throw new RpcException("hooklab_runtime_identity_unavailable","Could not derive the exact loaded CoreCLR runtime identity: "+ex.Message); }
			}
			// appDomainId defaults to what this always recorded. It only matters to callers that compare
			// with SameTarget; ExtensionLiveTargets compares with SameProcessTarget and ignores it.
			static TargetIdentity LiveTarget(int processId,string hostId=DgSpyStateRoot.ResidentHostId,string runtimeId="v4.0.30319",string appDomainId=DefaultApplicationDomainId) { using var process=Process.GetProcessById(processId); return new TargetIdentity(hostId,process.MainModule?.FileName??throw new InvalidOperationException("Target image is unavailable."),processId,process.StartTime.ToUniversalTime(),"x64",runtimeId,appDomainId); }
			// Two different questions, and only one of them involves the application domain.
			//
			// "Is this record still about a live process that is the same process?" must NOT consider the
			// domain. DiscoverCore treats a record it is told is not current as either garbage to delete
			// or an integrity failure to quarantine, so answering false for a perfectly valid resident
			// that merely lives in another domain would destroy it - and "preserve unknown and
			// foreign-owned resident hooks" is an invariant of this whole road.
			//
			// "Is this record the resident I am operating on?" must consider it, or an operation aimed at
			// domain 3 adopts the resident in domain 2.
			static bool SameProcessTarget(TargetIdentity left,TargetIdentity right)=>HookLabTargetMatch.SameProcess(left,right,DgSpyStateRoot.ResidentHostId);
			static bool SameTarget(TargetIdentity left,TargetIdentity right)=>HookLabTargetMatch.SameTarget(left,right,DgSpyStateRoot.ResidentHostId);
			// Reading a process can fail in three ways, and only one of them is "no such process".
			// ArgumentException is that one. Process.MainModule and Process.StartTime additionally throw
			// Win32Exception when the process cannot be opened - a recycled PID now owned by another
			// account, an elevated process, a protected one - and InvalidOperationException when it is
			// exiting. This type caught only ArgumentException, so a Win32Exception escaped discovery,
			// escaped initialization, and surfaced as a bare "Win32Exception: Access is denied.".
			//
			// One record left over from an earlier session was therefore enough to make EVERY
			// initialize_hooklab on the machine fail, for any target and any backend, naming nothing that
			// pointed at a file on disk. Records live five minutes unless a health check refreshes them,
			// so that record was expired garbage the scan would have deleted on its own had it been able
			// to reach the check.
			//
			// HookLab.Injector's SystemLiveTargets - the watcher's copy of this same concept - already
			// handled all three. This aligns the two rather than inventing a third policy.
			//
			// Open question deliberately not settled here: false from IsAlive means "provably dead" and
			// DiscoverCore deletes the record, so a target we merely cannot read is treated as one that
			// is gone. With a five-minute lifetime that is nearly always right, but "cannot determine" and
			// "provably dead" are different answers and the target environment contract's subslice 7 owns the distinction -
			// not_provable_preflight is exactly this shape. Recorded in the task, not folded in here.
			sealed class ExtensionLiveTargets : ILiveTargetIdentity,ILiveTargetLiveness {
				readonly string runtimeId; public ExtensionLiveTargets(string runtimeId="v4.0.30319")=>this.runtimeId=runtimeId;
				public bool IsCurrent(TargetIdentity identity) { try { return SameProcessTarget(identity,LiveTarget(identity.ProcessId,identity.HostId,runtimeId)); } catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } catch(System.ComponentModel.Win32Exception) { return false; } }
				public bool IsAlive(TargetIdentity identity) { try { using var process=Process.GetProcessById(identity.ProcessId); return process.StartTime.ToUniversalTime().Ticks==identity.ProcessCreationTimeUtc.ToUniversalTime().Ticks; } catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } catch(System.ComponentModel.Win32Exception) { return false; } }
			}

			static async Task EnsurePausedAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				if(!await host.OnDebuggerAsync(()=>host.SelectProcess(source).IsRunning,token).ConfigureAwait(false)) return;
				await host.PauseProcessAsync(ControlRequest(source),token).ConfigureAwait(false);
			}
			static async Task SynchronizeCoreClrAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				if(await host.OnDebuggerAsync(()=>host.SelectProcess(source).IsRunning,token).ConfigureAwait(false)) await host.PauseProcessAsync(ControlRequest(source),token).ConfigureAwait(false);
				await host.ContinueProcessAsync(ControlRequest(source),token).ConfigureAwait(false);
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
			static async Task ReconcileCoreClrRunAsync(RpcHost host,RpcRequest source,CancellationToken token) {
				var runtime=await host.OnDebuggerAsync(()=>{
					var candidates=host.SelectProcess(source).Runtimes.Where(host.ownedBreakpoints.IsSupported).ToArray();
					if(candidates.Length!=1) throw new RpcException("hooklab_runtime_ambiguous","HookLab expected exactly one CorDebug runtime for state reconciliation, found "+candidates.Length.ToString(CultureInfo.InvariantCulture)+".");
					return candidates[0];
				},token).ConfigureAwait(false);
				await host.ownedBreakpoints.ReconcileRunAsync(runtime,token).ConfigureAwait(false);
			}
			static RpcRequest ControlRequest(RpcRequest source)=>new RpcRequest { Operation=source.Operation,Arguments=new JsonObject { ["session_id"]=Required(source.Arguments,"session_id"),["process_id"]=RequiredInt(source.Arguments,"process_id") } };
			static object Initialized(RuntimeRecord runtime,bool changed,bool? adopted=null)=>new { initialized=true,changed,adopted,process_id=runtime.ProcessId,state="ready",hooks_version=runtime.HooksVersion,probe_instance_id=runtime.ProbeInstanceId };
			static object Installed(HookRecord record,bool changed) => new { hook=View(record),installed=changed };
			static object View(HookRecord record) => new { hook_id=record.Definition.HookId,patch_id=record.PatchId,controller=HookOwnership.DgSpyController,owned=true,capabilities=new[]{"edit","enable","disable","remove"},assembly_simple_name=record.Definition.Assembly,kind=record.Definition.Kind,revision=record.Definition.Revision,compiled=record.Definition.Source is not null,source=record.Definition.Source,diagnostics=Array.Empty<string>(),enabled=record.Enabled,process_id=record.Definition.ProcessId,module_id=record.Definition.ModuleId,method_token=record.Definition.MethodToken,declaring_type=record.Definition.DeclaringType,method=record.Definition.Method,state=record.Enabled?"enabled":"disabled" };
			static object View(ResidentHookRecord record) { var owned=record.Controller==HookOwnership.DgSpyController; return new { hook_id=record.HookId,patch_id=record.PatchId,controller=String.IsNullOrEmpty(record.Controller)?"unknown":record.Controller,owned,capabilities=owned?new[]{"enable","disable","remove"}:Array.Empty<string>(),assembly_simple_name=record.AssemblySimpleName,kind=record.Kind,revision=record.Revision,compiled=true,source=(string?)null,source_sha256=record.SourceSha256,diagnostics=Array.Empty<string>(),enabled=record.Enabled,process_id=record.ProcessId,module_id=(string?)null,method_token=record.MetadataToken,declaring_type=record.DeclaringType,method=(string?)null,state=record.Enabled?"enabled":"disabled" }; }
			static void EnsureCompleted(AtomicActionResult result,string operation) { if(result.Status.ActionOutcome!=HookLab.Contracts.ActionOutcome.completed) throw new RpcException("hook_operation_failed",HookLabInstallPresentation.Failure(operation,result.Status.ActionOutcome,result.Status.InterruptionReason,result.Error)); }
			static Dictionary<string,string> Report(AtomicActionResult result) { var node=JsonNode.Parse(result.VerificationEvidence ?? "{}") as JsonObject; return ParseReport((string?)node?["report"] ?? ""); }
			static Dictionary<string,string> ParseReport(string text) { var values=new Dictionary<string,string>(StringComparer.Ordinal); foreach(var line in text.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)) { var separator=line.IndexOf('='); if(separator>0) values[line.Substring(0,separator)]=line.Substring(separator+1); } return values; }
			/// <summary>What the report says after the deadline has passed, when it says anything.
			///
			/// <para>A resident that finished late leaves the whole answer on disk: its status, and how long
			/// its worker waited to be scheduled. Quoting that turns "did not publish within 20 seconds"
			/// into "published 0.9 seconds late", which are different problems with different fixes.</para></summary>
			static string TryReadLateReport(string path) {
				try {
					if(!File.Exists(path)) return " No report was written at all, so the resident did not reach the point of publishing one.";
					var report=ParseReport(File.ReadAllText(path));
					var status=report.TryGetValue("status",out var value)?value:"absent";
					var queued=report.TryGetValue("worker_queue_ms",out var queue)?queue:null;
					return queued is null
						?" A partial report exists with status="+status+"."
						:" A report does exist, with status="+status+" and worker_queue_ms="+queued+
							": the resident was scheduled that long after commit, so the work was done and published late rather than not at all.";
				}
				catch(Exception ex) { return " The report could not be read while diagnosing the timeout: "+ex.GetType().Name+"."; }
			}


			const int CompletionDeadlineSeconds=20;
			/// <summary>How long a timed-out initialization keeps looking for a resident it would otherwise
			/// orphan. Reached only on failure.</summary>
			const int CompletionCleanupGraceSeconds=5;

			/// <summary>Waits for the resident's ready record.
			///
			/// <para><b>Do not attack the intermittent cross-identity CoreCLR timeout by waiting longer or
			/// by resuming again. Both were tried and measured on 2026-08-20.</b> Five failures at a 20 s
			/// deadline reported <c>worker_queue_ms</c> of 20899, 20928, 20935, 20951 and 20929 - deadline
			/// plus ~930 ms. Adding a second resume and a five-second grace window moved that to 26014 and
			/// 26024 - deadline plus grace plus ~1 s - and the failure rate did not move at all: two of six
			/// runs, the same as before. The grace was reverted.</para>
			///
			/// <para>The hold is a split between dnSpy's manager state and CorDebug's engine state after
			/// func-eval. The manager can still say running and suppress <c>DbgProcess.Run</c> while
			/// <c>DnDebugger</c> is paused. CoreCLR initialization therefore uses the narrow CorDebug bridge
			/// to invoke the engine's existing idempotent <c>RunCore</c>, which decides from that authoritative
			/// state and does not expose raw <c>ICorDebug</c> control.</para>
			///
			/// <para>Verified against the produced package on 2026-08-20: 23 consecutive lifecycle runs
			/// reached this path and passed 19/0. A separate twelfth launch in the first batch never opened
			/// its RPC port and therefore did not reach readiness or this lifecycle.</para>
			///
			/// <para>The deadline keeps its original length, and <see cref="TryReadLateReport"/> still makes
			/// any future refusal distinguish late successful work from work that never published.</para></summary>
			static async Task<Dictionary<string,string>> ReadCompletionAsync(string path,CancellationToken token) {
				var report=await PollCompletionAsync(path,TimeSpan.FromSeconds(CompletionDeadlineSeconds),token).ConfigureAwait(false);
				if(report is not null) return report;
				// A second, bounded window - and deliberately not the one that was tried and reverted.
				//
				// That one waited longer hoping the resident would finish, which was answered: it moved
				// worker_queue_ms by exactly the extra wait and fixed nothing, because the target was held
				// by the wait itself. This one exists for the opposite reason. Giving up here is not free:
				// a resident that came up is authenticated, listening, and reachable only through the
				// secret this operation is about to zero. So before abandoning anything, find out whether
				// there is something to abandon, and if there is, take the normal path and adopt it - which
				// records it, and is a truthful answer besides, because the initialization did happen.
				//
				// It costs nothing on a healthy run, because a healthy run never reaches this line.
				report=await PollCompletionAsync(path,TimeSpan.FromSeconds(CompletionCleanupGraceSeconds),token).ConfigureAwait(false);
				if(report is not null) return report;
				// Nothing published in either window. The resident may still come up later and be orphaned;
				// that residue is bounded by this grace and is not silent - the preserved exchange area and
				// this message are what is left of it.
				var late=TryReadLateReport(path);
				throw new RpcException("hook_operation_timed_out","HookLab worker did not publish a complete ready record within "+
					(CompletionDeadlineSeconds+CompletionCleanupGraceSeconds).ToString(CultureInfo.InvariantCulture)+" seconds."+late);
			}

			/// <summary>Polls for a <b>complete</b> ready record, or null when the window closes. The target
			/// writes the report incrementally, so file existence - and even its first status line - do not
			/// mean the record has been fully published.</summary>
			static async Task<Dictionary<string,string>?> PollCompletionAsync(string path,TimeSpan window,CancellationToken token) {
				var deadline=DateTime.UtcNow+window;
				while(DateTime.UtcNow<deadline) {
					token.ThrowIfCancellationRequested();
					if(File.Exists(path)) {
						var report=ParseReport(File.ReadAllText(path));
						if(report.TryGetValue("status",out var status) && (!String.Equals(status,"ok",StringComparison.Ordinal) || report.ContainsKey("pipe_name"))) return report;
					}
					await Task.Delay(50,token).ConfigureAwait(false);
				}
				return null;
			}
			// TryDelete and TryDeleteDirectory lived here. Both are gone: the exchange area owns the
			// lifetime of everything staged for an initialization, including whether it survives a
			// failure, and leaving unconditional deletes lying around is how that decision gets quietly
			// taken again somewhere else.
			static string Required(Dictionary<string,string> values,string key,string message) => values.TryGetValue(key,out var value) && !String.IsNullOrWhiteSpace(value) ? value : throw new RpcException("hook_operation_failed",message);
			static long Long(Dictionary<string,string> values,string key) => values.TryGetValue(key,out var value) && Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed) ? parsed : 0;
			static string ParameterText(JsonObject values) => String.Join("\n",values.Select(pair=>pair.Key+"="+(string?)pair.Value))+"\n";
			static string Required(JsonObject values,string key) => (string?)values[key] is string value && !String.IsNullOrWhiteSpace(value) ? value : throw new RpcException("invalid_arguments",key+" is required.");
			static int RequiredInt(JsonObject values,string key) => (int?)values[key] ?? throw new RpcException("invalid_arguments",key+" is required.");
			static string Key(string session,int process,string hookId)=>session+"\n"+process.ToString(CultureInfo.InvariantCulture)+"\n"+hookId;
			static string RuntimeKey(string session,int process)=>session+"\n"+process.ToString(CultureInfo.InvariantCulture);

			sealed class HookRecord { public HookRecord(HookDefinition definition,string patchId) { Definition=definition; PatchId=patchId; } public HookDefinition Definition { get; } public string PatchId { get; } public bool Enabled=true; public MethodDef? MethodDefinition; }
			sealed class ResidentHookRecord { public ResidentHookRecord(string sessionId,int processId,string patchId,string controller,string hookId,string assemblySimpleName,string kind,string mvid,int metadataToken,string declaringType,string signature,string ilSha256,string sourceSha256,int revision,bool enabled) { SessionId=sessionId; ProcessId=processId; PatchId=patchId; Controller=controller; HookId=hookId; AssemblySimpleName=assemblySimpleName; Kind=kind; Mvid=mvid; MetadataToken=metadataToken; DeclaringType=declaringType; Signature=signature; IlSha256=ilSha256; SourceSha256=sourceSha256; Revision=revision; Enabled=enabled; } public string SessionId,PatchId,Controller,HookId,AssemblySimpleName,Kind,Mvid,DeclaringType,Signature,IlSha256,SourceSha256; public int ProcessId,MetadataToken,Revision; public bool Enabled; public string Key=>SessionId+"\n"+ProcessId.ToString(CultureInfo.InvariantCulture)+"\n"+PatchId; }
			sealed class HookCarrier { public HookCarrier(string moduleId,int methodToken,int ilOffset,int[] nearbyOffsets,string appDomainId) { ModuleId=moduleId; MethodToken=methodToken; IlOffset=ilOffset; NearbyOffsets=nearbyOffsets; AppDomainId=appDomainId; } public string ModuleId { get; } public int MethodToken { get; } public int IlOffset { get; } public int[] NearbyOffsets { get; } public string AppDomainId { get; } }
			sealed class RuntimeRecord : IDisposable { public RuntimeRecord(string sessionId,int processId,ProbeConnection connection,long hooksVersion,string probeInstanceId,bool adopted,string appDomainId=DefaultApplicationDomainId) { SessionId=sessionId; ProcessId=processId; Connection=connection; HooksVersion=hooksVersion; ProbeInstanceId=probeInstanceId; Adopted=adopted; AppDomainId=appDomainId; } public string SessionId { get; } public int ProcessId { get; } public string ProbeInstanceId { get; } public bool Adopted { get; } public string AppDomainId { get; } public ProbeConnection Connection { get; } public long HooksVersion; public SemaphoreSlim OperationGate { get; }=new SemaphoreSlim(1,1); public CancellationTokenSource Cancellation { get; }=new CancellationTokenSource(); public Task? Pump;
				/// <summary>Hooks the resident has seen shadowed, keyed by patch id, as of the last status
				/// round trip. Empty is the normal answer.</summary>
				public Dictionary<string,(string DeclaringType,string ShadowingAssembly)> Shadowed { get; }=new Dictionary<string,(string,string)>(StringComparer.Ordinal);
				public void Dispose() { Cancellation.Cancel(); Connection.Dispose(); OperationGate.Dispose(); Cancellation.Dispose(); } }
			sealed class HookEventRecord { public HookEventRecord(long cursor,string sequence,string patchId,string payloadJson,long dropped) { Cursor=cursor; Sequence=sequence; PatchId=patchId; PayloadJson=payloadJson; Dropped=dropped; } public long Cursor { get; } public string Sequence { get; } public string PatchId { get; } public string PayloadJson { get; } public long Dropped { get; } }

			sealed class HookDefinition {
				public string SessionId="",HookId="",Kind="",ModuleId="",Assembly="",DeclaringType="",Method="",Signature="",Mvid="",IlSha256="",ArrivalModuleId="",ImagePath="",RuntimeId="v4.0.30319"; public string? Source;
				public int ProcessId,MethodToken,ArrivalMethodToken,ArrivalIlOffset,MaximumEventsPerSecond=100,MaximumStringLength=1024,Revision; public int[] NearbyOffsets=Array.Empty<int>(); public long ProcessCreationTicks;
				public string Key=>HookLabService.Key(SessionId,ProcessId,HookId);
				public static HookDefinition Parse(JsonObject values) {
					var value=new HookDefinition { SessionId=Required(values,"session_id"),ProcessId=RequiredInt(values,"process_id"),HookId=Required(values,"hook_id"),Kind=Required(values,"kind"),ModuleId=Required(values,"module_id"),Assembly=Required(values,"assembly"),DeclaringType=Required(values,"declaring_type"),Method=Required(values,"method"),Signature=Required(values,"signature"),Mvid=Required(values,"module_mvid"),IlSha256=Required(values,"il_sha256"),MethodToken=RequiredInt(values,"method_token") };
					if(value.Kind!="Prefix" && value.Kind!="Postfix" && value.Kind!="Finalizer" && value.Kind!="Transpiler") throw new RpcException("invalid_arguments","kind must be Prefix, Postfix, Finalizer, or Transpiler.");
					value.Source=(string?)values["source"];
					if(value.Source is null&&value.Kind=="Transpiler") throw new RpcException("invalid_arguments","Transpiler requires compiled custom source; use create_hook or update_hook.");
					if(value.Source is not null) { if(value.Kind!="Prefix"&&value.Kind!="Postfix"&&value.Kind!="Finalizer"&&value.Kind!="Transpiler") throw new RpcException("invalid_arguments","Custom source currently supports Prefix, Postfix, Finalizer, and Transpiler only."); value.Revision=RequiredInt(values,"revision"); if(value.Revision<=0) throw new RpcException("invalid_arguments","revision must be positive."); }
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
				public JsonObject Parameters(string completion,string? residentHookId=null) { var values=new JsonObject { ["host_id"]=DgSpyStateRoot.ResidentHostId,["image_path"]=ImagePath,["process_id"]=ProcessId.ToString(CultureInfo.InvariantCulture),["process_creation_utc_ticks"]=ProcessCreationTicks.ToString(CultureInfo.InvariantCulture),["architecture"]="x64",["runtime_id"]=RuntimeId,["appdomain_id"]="1",["endpoint"]="pipe",["completion_path"]=completion,["hook_id"]=residentHookId??HookId,["hook_kind"]=Kind,["hook_assembly"]=Assembly,["hook_type"]=DeclaringType,["hook_method"]=Method,["hook_module_mvid"]=Mvid,["hook_metadata_token"]=MethodToken.ToString(CultureInfo.InvariantCulture),["hook_declaring_type"]=DeclaringType,["hook_method_signature"]=Signature,["hook_il_sha256"]=IlSha256,["maximum_events_per_second"]=MaximumEventsPerSecond.ToString(CultureInfo.InvariantCulture),["maximum_string_length"]=MaximumStringLength.ToString(CultureInfo.InvariantCulture) }; if(Source is not null) { values["hook_source_base64"]=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Source)); values["hook_revision"]=Revision.ToString(CultureInfo.InvariantCulture); } return values; }
			}
		}
	}
}
