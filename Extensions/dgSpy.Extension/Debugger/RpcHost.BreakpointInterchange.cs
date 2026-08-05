using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using System.Text.Json.Nodes;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		async Task<BreakpointDocument> ExportBreakpointsAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req); var max=Math.Min(5000,Math.Max(1,(int?)req.Arguments["max_exception_policies"]??5000));
			var policies=await OnDebuggerAsync(()=>exceptions.Exceptions.Select(e=>Policy(e.Definition,e.Settings)).ToArray(),token).ConfigureAwait(false);
			return new BreakpointDocument { Code=await ListBreakpointsAsync(token).ConfigureAwait(false),Modules=await ListModuleBreakpointsAsync(token).ConfigureAwait(false),Exceptions=policies.Take(max).ToArray(),ExceptionTotal=policies.Length,ExceptionTruncated=policies.Length>max };
		}

		async Task<BreakpointImportResult> ImportBreakpointsAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req); var document=ProtocolJson.FromNode<BreakpointDocument>(req.Arguments["document"])??throw new RpcException("invalid_arguments","document is required.");
			if(document.Format!="dgspy.breakpoints" || document.Version!=1) throw new RpcException("unsupported_format","Expected dgspy.breakpoints version 1.");
			var mode=((string?)req.Arguments["mode"]??"merge").ToLowerInvariant(); if(mode!="merge"&&mode!="replace") throw new RpcException("invalid_arguments","mode must be merge or replace.");
			if(mode=="replace"&&document.ExceptionTruncated) throw new RpcException("truncated_document","A truncated exception-policy export cannot be used with replace mode.");
			var dry=(bool?)req.Arguments["dry_run"]??false;
			foreach(var code in document.Code) { if(string.IsNullOrWhiteSpace(code.Module)||code.MethodToken==0) throw new RpcException("invalid_breakpoint_document","Every code breakpoint needs module and method_token."); }
			foreach(var policy in document.Exceptions) { if(string.IsNullOrWhiteSpace(policy.Category)) throw new RpcException("invalid_breakpoint_document","Every exception policy needs a category."); if(policy.Conditions.Any(c=>c.Kind!="module_equals"&&c.Kind!="module_not_equals")) throw new RpcException("invalid_breakpoint_document","Exception condition kind must be module_equals or module_not_equals."); }
			var existingCode=await ListBreakpointsAsync(token).ConfigureAwait(false); var existingModules=await ListModuleBreakpointsAsync(token).ConfigureAwait(false); var existingPolicies=await OnDebuggerAsync(()=>exceptions.Exceptions.Select(e=>Policy(e.Definition,e.Settings)).ToArray(),token).ConfigureAwait(false);
			bool Same(BreakpointInfo a,BreakpointInfo b)=>StringComparer.OrdinalIgnoreCase.Equals(a.Module,b.Module)&&a.MethodToken==b.MethodToken&&a.IlOffset==b.IlOffset;
			bool SameModule(ModuleBreakpointInfo a,ModuleBreakpointInfo b)=>StringComparer.OrdinalIgnoreCase.Equals(a.ModuleName??"",b.ModuleName??"")&&a.IsDynamic==b.IsDynamic&&a.IsInMemory==b.IsInMemory&&a.IsLoaded==b.IsLoaded&&a.Order==b.Order&&StringComparer.OrdinalIgnoreCase.Equals(a.ProcessName??"",b.ProcessName??"")&&StringComparer.OrdinalIgnoreCase.Equals(a.AppDomainName??"",b.AppDomainName??"");
			bool SamePolicy(ExceptionPolicyInfo a,ExceptionPolicyInfo b)=>StringComparer.OrdinalIgnoreCase.Equals(a.Category,b.Category)&&StringComparer.Ordinal.Equals(a.Name,b.Name)&&a.StopThrown==b.StopThrown&&a.StopUnhandled==b.StopUnhandled&&a.Conditions.Length==b.Conditions.Length&&a.Conditions.All(c=>b.Conditions.Any(x=>StringComparer.OrdinalIgnoreCase.Equals(c.Kind,x.Kind)&&StringComparer.OrdinalIgnoreCase.Equals(c.Module,x.Module)));
			document.Code=document.Code.GroupBy(c=>$"{c.Module.ToUpperInvariant()}\0{c.MethodToken}\0{c.IlOffset}").Select(g=>g.First()).ToArray();
			document.Modules=document.Modules.GroupBy(c=>$"{c.ModuleName?.ToUpperInvariant()}\0{c.IsDynamic}\0{c.IsInMemory}\0{c.IsLoaded}\0{c.Order}\0{c.ProcessName?.ToUpperInvariant()}\0{c.AppDomainName?.ToUpperInvariant()}").Select(g=>g.First()).ToArray();
			document.Exceptions=document.Exceptions.GroupBy(c=>$"{c.Category.ToUpperInvariant()}\0{c.Name}").Select(g=>g.Last()).ToArray();
			var unchanged=document.Code.Count(c=>existingCode.Any(e=>Same(c,e)))+document.Modules.Count(c=>existingModules.Any(e=>SameModule(c,e)))+document.Exceptions.Count(c=>existingPolicies.Any(e=>SamePolicy(c,e)));
			var added=document.Code.Length+document.Modules.Length+document.Exceptions.Length-unchanged; var removed=mode=="replace"?existingCode.Count(e=>!document.Code.Any(c=>Same(c,e)))+existingModules.Count(e=>!document.Modules.Any(c=>SameModule(c,e)))+existingPolicies.Count(e=>!document.Exceptions.Any(c=>SamePolicy(c,e))):0;
			if(dry) return new BreakpointImportResult { DryRun=true,Mode=mode,Added=added,Removed=removed,Unchanged=unchanged };
			if(mode=="replace") { foreach(var bp in existingCode) await RemoveBreakpointByIdAsync(bp.BreakpointId,token).ConfigureAwait(false); foreach(var bp in existingModules) await OnDebuggerAsync(()=>{ var found=moduleBreakpoints.Breakpoints.FirstOrDefault(x=>x.Id==bp.BreakpointId); if(found is not null) moduleBreakpoints.Remove(found); return true; },token).ConfigureAwait(false); await RestoreExceptionDefaultsAsync(token).ConfigureAwait(false); existingCode=Array.Empty<BreakpointInfo>(); existingModules=Array.Empty<ModuleBreakpointInfo>(); }
			foreach(var code in document.Code.Where(c=>!existingCode.Any(e=>Same(c,e)))) { var args=new JsonObject { ["session_id"]=sessionId,["module"]=code.Module,["method_token"]=code.MethodToken,["il_offset"]=code.IlOffset }; var created=await SetBreakpointAsync(new RpcRequest { Operation="set_il_breakpoint",Arguments=args },token).ConfigureAwait(false); if(!code.Enabled || code.Condition is not null || code.HitCount is not null || code.TraceMessage is not null) { var update=new JsonObject { ["breakpoint_id"]=created.BreakpointId,["enabled"]=code.Enabled }; if(code.Condition is not null) { update["condition"]=code.Condition; update["condition_kind"]=code.ConditionKind??BreakpointConditionKinds.IsTrue; } if(code.HitCount is not null) { update["hit_count"]=code.HitCount; update["hit_count_kind"]=code.HitCountKind??HitCountKinds.Equals; } if(code.TraceMessage is not null) { update["trace_message"]=code.TraceMessage; update["trace_continue"]=code.TraceContinue??true; } await UpdateBreakpointAsync(new RpcRequest { Operation="update_breakpoint",Arguments=update },token).ConfigureAwait(false); } }
			foreach(var module in document.Modules.Where(c=>!existingModules.Any(e=>SameModule(c,e)))) await OnDebuggerAsync(()=>{ moduleBreakpoints.Add(new dnSpy.Contracts.Debugger.Breakpoints.Modules.DbgModuleBreakpointSettings { IsEnabled=module.Enabled,ModuleName=module.ModuleName,IsDynamic=module.IsDynamic,IsInMemory=module.IsInMemory,IsLoaded=module.IsLoaded,Order=module.Order,ProcessName=module.ProcessName,AppDomainName=module.AppDomainName }); return true; },token).ConfigureAwait(false);
			foreach(var policy in document.Exceptions) { var args=ProtocolJson.ToObject(new { category=policy.Category,name=policy.Name,stop_thrown=policy.StopThrown,stop_unhandled=policy.StopUnhandled,conditions=policy.Conditions }); await SetExceptionPolicyAsync(new RpcRequest { Operation="set_exception_policy",Arguments=args },token).ConfigureAwait(false); }
			return new BreakpointImportResult { DryRun=false,Mode=mode,Added=added,Removed=removed,Unchanged=unchanged };
		}
		async Task RemoveBreakpointByIdAsync(int id,CancellationToken token)=>await OnDebuggerAsync(()=>{ var bp=breakpoints.Breakpoints.FirstOrDefault(b=>b.Id==id); if(bp is not null) breakpoints.Remove(bp); return true; },token).ConfigureAwait(false);
	}
}
