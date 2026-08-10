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
		// This tool was not merely truncated before, it was unreachable. max_methods capped at 5000 and
		// counted only methods with bodies, so asking for the callers of a method in a 7227-method module
		// returned "0 edges, scan_truncated" at every setting the schema allowed, and the 20311-method
		// Assembly-CSharp of a Unity player was worse. Two things fix that, and both are needed: the walk
		// now carries a resume cursor, so a caller can continue past the bound instead of stopping at it,
		// and max_scan reaches as far as `search`'s does for a caller that would rather pay in one call.
		//
		// The cursor counts every inspected slot, not only the method bodies, because the type-level edges
		// (implements, attribute) are emitted from the same traversal. Counting bodies alone would re-emit
		// every type-level edge on every resumed page, which is a duplicate answer dressed as a new one.
		const int DefaultAnalysisScan = 20000;
		const int MaxAnalysisScan = 2000000;

		async Task<AnalysisResult> AnalyzeSymbolAsync(RpcRequest req,CancellationToken token) {
			CheckSession(req); var targetSelection=await WithModuleMetadataAsync(req,"module",(_,moduleId,metadata)=>{ var value=metadata.ResolveToken((uint?)req.Arguments["token"]??throw new RpcException("invalid_arguments","token is required.")) as IMemberRef; return (Target:value??throw new RpcException("token_not_found","The token is not a symbol."),ModuleId:moduleId); },token).ConfigureAwait(false);
			var target=targetSelection.Target;
			var moduleFilter=(string?)req.Arguments["search_module"]; var max=Math.Min(500,Math.Max(1,(int?)req.Arguments["count"]??100));
			// max_methods is the older name for the same work bound and is still honoured, so a caller that
			// learned the old schema keeps working. max_scan is what it is called now, because the bound
			// counts type slots too.
			var walk=new ScanCursor {
				Skip=Math.Max(0,(int?)req.Arguments["scan_offset"]??0),
				Max=Math.Min(MaxAnalysisScan,Math.Max(1,(int?)req.Arguments["max_scan"]??(int?)req.Arguments["max_methods"]??DefaultAnalysisScan)),
			};
			var modules=await OnDebuggerAsync(()=>ScanModuleSelections(moduleFilter),token).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var edges=new List<AnalysisEdge>(); var total=0; var scannedMethods=0; var targetSymbol=Symbol(Kind(target),target.Module?.Name??"",targetSelection.ModuleId,target,target.DeclaringType?.ResolveTypeDef());
				void Add(string kind,SymbolInfo source,SymbolInfo destination) { total++; if(edges.Count<max) edges.Add(new AnalysisEdge { Kind=kind,Source=source,Target=destination }); }
				foreach(var selected in modules) {
					var dbg=selected.Module; var metadata=metadataService.TryGetMetadata(dbg); if(metadata is null) continue; var moduleName=metadata.Name?.ToString()??selected.Name; var moduleId=selected.ModuleId;
					foreach(var type in metadata.GetTypes()) {
						token.ThrowIfCancellationRequested(); var typeSymbol=Symbol("type",moduleName,moduleId,type,null);
						// One slot for the type's own edges. Skipped when it precedes the cursor, which is
						// exactly what stops a resumed page repeating them.
						if(!walk.Claim()) { if(walk.Truncated) break; }
						else if(target is TypeDef targetType) { if(type.BaseType?.FullName==targetType.FullName||type.Interfaces.Any(i=>i.Interface?.FullName==targetType.FullName)) Add("implements",typeSymbol,targetSymbol); if(type.CustomAttributes.Any(a=>a.AttributeType.FullName==targetType.FullName)) Add("attribute",typeSymbol,targetSymbol); }
						foreach(var method in type.Methods) {
							if(!walk.Claim()) { if(walk.Truncated) break; continue; }
							scannedMethods++;
							var methodSymbol=Symbol("method",moduleName,moduleId,method,type);
							if(target is MethodDef targetMethod && (method.Overrides.Any(ov=>ov.MethodDeclaration.FullName==targetMethod.FullName)||Overrides(method,targetMethod))) Add("override",methodSymbol,targetSymbol);
							if(method.CustomAttributes.Any(a=>a.AttributeType.FullName==target.FullName)) Add("attribute",methodSymbol,targetSymbol);
							if(!method.HasBody) continue;
							foreach(var instruction in method.Body.Instructions) { if(instruction.Operand is IMethod called) { var calledSymbol=Symbol("method",called.Module?.Name??moduleName,moduleId,called,called.DeclaringType?.ResolveTypeDef()); if(called.FullName==target.FullName) Add(instruction.OpCode.Code==Code.Newobj?"constructs":"caller",methodSymbol,targetSymbol); if(target is EventDef targetEvent) { if(called.FullName==targetEvent.AddMethod?.FullName) Add("event_add",methodSymbol,targetSymbol); if(called.FullName==targetEvent.RemoveMethod?.FullName) Add("event_remove",methodSymbol,targetSymbol); } if(method.FullName==target.FullName) Add(instruction.OpCode.Code==Code.Newobj?"constructs":"callee",targetSymbol,calledSymbol); }
								else if(instruction.Operand is IField field) { var fieldSymbol=Symbol("field",field.Module?.Name??moduleName,moduleId,field,field.DeclaringType?.ResolveTypeDef()); var write=instruction.OpCode.Code==Code.Stfld||instruction.OpCode.Code==Code.Stsfld; if(field.FullName==target.FullName) Add(write?"field_write":"field_read",methodSymbol,targetSymbol); if(method.FullName==target.FullName) Add(write?"writes_field":"reads_field",targetSymbol,fieldSymbol); }
							}
						}
						if(walk.Truncated) break;
					}
					if(walk.Truncated) break;
				}
				return new AnalysisResult {
					Edges=edges.ToArray(),Total=total,Truncated=total>edges.Count||walk.Truncated,
					ScannedMethods=scannedMethods,Scanned=walk.Worked,ScanTruncated=walk.Truncated,NextScanOffset=walk.Inspected,
				};
			},token).ConfigureAwait(false);
		}
		static string Kind(IMemberRef member)=>member is TypeDef?"type":member is IMethod?"method":member is IField?"field":member is EventDef?"event":"member";
		static bool Overrides(MethodDef method,MethodDef target) { if(method.Name!=target.Name || !new SigComparer().Equals(method.MethodSig,target.MethodSig)) return false; for(var type=method.DeclaringType?.BaseType?.ResolveTypeDef();type is not null;type=type.BaseType?.ResolveTypeDef()) if(type.FullName==target.DeclaringType?.FullName) return true; return false; }
	}
}
