using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		async Task<AnalysisResult> AnalyzeSymbolAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req); var target=await WithMetadataAsync(req,"module",metadata=>{ var value=metadata.ResolveToken((uint?)req.Arguments["token"]??throw new RpcException("invalid_arguments","token is required.")) as IMemberRef; return value??throw new RpcException("token_not_found","The token is not a symbol."); },token).ConfigureAwait(false);
			var moduleFilter=(string?)req.Arguments["search_module"]; var max=Math.Min(500,Math.Max(1,(int?)req.Arguments["count"]??100)); var maxMethods=Math.Min(5000,Math.Max(1,(int?)req.Arguments["max_methods"]??1000));
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules).Where(m=>string.IsNullOrEmpty(moduleFilter)||Matches(m.Name,moduleFilter)||Matches(m.Filename,moduleFilter)).ToArray(),token).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var edges=new List<AnalysisEdge>(); var total=0; var scannedMethods=0; var scanTruncated=false; var targetSymbol=Symbol(Kind(target),target.Module?.Name??"",target,target.DeclaringType?.ResolveTypeDef());
				void Add(string kind,SymbolInfo source,SymbolInfo destination) { total++; if(edges.Count<max) edges.Add(new AnalysisEdge { Kind=kind,Source=source,Target=destination }); }
				foreach(var dbg in modules) { var metadata=metadataService.TryGetMetadata(dbg); if(metadata is null) continue; var moduleName=metadata.Name?.ToString()??dbg.Name; foreach(var type in metadata.GetTypes()) { token.ThrowIfCancellationRequested(); var typeSymbol=Symbol("type",moduleName,type,null);
					if(target is TypeDef targetType) { if(type.BaseType?.FullName==targetType.FullName||type.Interfaces.Any(i=>i.Interface?.FullName==targetType.FullName)) Add("implements",typeSymbol,targetSymbol); if(type.CustomAttributes.Any(a=>a.AttributeType.FullName==targetType.FullName)) Add("attribute",typeSymbol,targetSymbol); }
					foreach(var method in type.Methods) { var methodSymbol=Symbol("method",moduleName,method,type); if(target is MethodDef targetMethod) foreach(var ov in method.Overrides) if(ov.MethodDeclaration.FullName==targetMethod.FullName) Add("override",methodSymbol,targetSymbol); if(method.CustomAttributes.Any(a=>a.AttributeType.FullName==target.FullName)) Add("attribute",methodSymbol,targetSymbol); if(!method.HasBody) continue; if(scannedMethods>=maxMethods) { scanTruncated=true; break; } scannedMethods++;
						foreach(var instruction in method.Body.Instructions) { if(instruction.Operand is IMethod called) { var calledSymbol=Symbol("method",called.Module?.Name??moduleName,called,called.DeclaringType?.ResolveTypeDef()); if(called.FullName==target.FullName) Add(instruction.OpCode.Code==Code.Newobj?"constructs":"caller",methodSymbol,targetSymbol); if(target is EventDef targetEvent) { if(called.FullName==targetEvent.AddMethod?.FullName) Add("event_add",methodSymbol,targetSymbol); if(called.FullName==targetEvent.RemoveMethod?.FullName) Add("event_remove",methodSymbol,targetSymbol); } if(method.FullName==target.FullName) Add(instruction.OpCode.Code==Code.Newobj?"constructs":"callee",targetSymbol,calledSymbol); }
							else if(instruction.Operand is IField field) { var fieldSymbol=Symbol("field",field.Module?.Name??moduleName,field,field.DeclaringType?.ResolveTypeDef()); var write=instruction.OpCode.Code==Code.Stfld||instruction.OpCode.Code==Code.Stsfld; if(field.FullName==target.FullName) Add(write?"field_write":"field_read",methodSymbol,targetSymbol); if(method.FullName==target.FullName) Add(write?"writes_field":"reads_field",targetSymbol,fieldSymbol); }
						}
					} if(scanTruncated) break;
				} if(scanTruncated) break; }
				return new AnalysisResult { Edges=edges.ToArray(),Total=total,Truncated=total>edges.Count||scanTruncated,ScannedMethods=scannedMethods,ScanTruncated=scanTruncated };
			},token).ConfigureAwait(false);
		}
		static string Kind(IMemberRef member)=>member is TypeDef?"type":member is IMethod?"method":member is IField?"field":member is EventDef?"event":"member";
	}
}
