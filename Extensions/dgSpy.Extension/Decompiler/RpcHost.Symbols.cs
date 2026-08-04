using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Metadata;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		// Metadata and decompilation run on the evaluation queue, not the dispatcher. Neither touches
		// debugger state, but dnlib's ModuleDef loads lazily and is shared across requests through
		// dnSpy's metadata cache, so two concurrent readers of the same module race. One serialized
		// worker is the cheap correct answer; it also keeps a large decompile off the dispatcher, where
		// it would stall event delivery for the whole session.
		const int MaxSymbolResults = 500;

		/// <summary>Resolves a caller-supplied module name or path to a live debugger module. Accepts a
		/// full path, a filename, or the module's short name, because a caller holding a frame has a
		/// path, one holding a search result has a name, and refusing either is just friction.</summary>
		/// <summary>Gets the engine's stable identity for breakpoint binding. An in-memory module reports
		/// a bare assembly name as its filename rather than nothing at all, but that display value omits
		/// the engine's per-module discriminator and produces a breakpoint that never binds.
		/// <c>list_modules</c> reports this same predicate as
		/// <c>can_set_breakpoint</c>; the two must never disagree.</summary>
		ModuleId? GetModuleId(DbgModule module) => moduleIdProviders.Select(provider=>provider.Value.GetModuleId(module)).FirstOrDefault(id=>id is not null);

		bool CanCarryBreakpoint(DbgModule module) => GetModuleId(module) is not null;

		async Task<ModuleId> ResolveBreakpointModuleIdAsync(RpcRequest req,string module,CancellationToken cancellationToken) {
			DbgModule? loaded=null;
			try { loaded=await OnDebuggerAsync(()=>FindModule(req,module),cancellationToken).ConfigureAwait(false); }
			catch (RpcException ex) when (ex.Code=="module_not_found") { }
			if (loaded is null) return ModuleId.Create(module);
			return await OnDebuggerAsync(()=>GetModuleId(loaded)
				?? throw new RpcException("metadata_unavailable",$"dnSpy cannot construct a breakpoint identity for '{module}' because the runtime published no module identity."),cancellationToken).ConfigureAwait(false);
		}

		DbgModule FindModule(string module) {
			var all=manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules).ToArray();
			var matches=all.Where(m=>string.Equals(m.Filename,module,StringComparison.OrdinalIgnoreCase)||string.Equals(m.Name,module,StringComparison.OrdinalIgnoreCase)||m.Filename.EndsWith("\\"+module,StringComparison.OrdinalIgnoreCase)).ToArray();
			if (matches.Length==0) throw new RpcException("module_not_found",$"No loaded module matches '{module}'. Use list_modules and pass an exact filename or name.");
			if (matches.Length>1) throw new RpcException("ambiguous_target",$"Module '{module}' exists in more than one active runtime; pass process_id and runtime_id.");
			return matches[0];
		}
		DbgModule FindModule(RpcRequest req,string module) {
			var processId=(int?)req.Arguments["process_id"]; var runtimeId=(string?)req.Arguments["runtime_id"];
			var matches=manager.Processes.Where(p=>!processId.HasValue||p.Id==processId.Value).SelectMany(p=>p.Runtimes).Where(r=>string.IsNullOrEmpty(runtimeId)||StringComparer.OrdinalIgnoreCase.Equals(r.Guid.ToString("D"),runtimeId)||StringComparer.OrdinalIgnoreCase.Equals(r.Name,runtimeId)).SelectMany(r=>r.Modules).Where(m=>string.Equals(m.Filename,module,StringComparison.OrdinalIgnoreCase)||string.Equals(m.Name,module,StringComparison.OrdinalIgnoreCase)||m.Filename.EndsWith("\\"+module,StringComparison.OrdinalIgnoreCase)).ToArray();
			if(matches.Length==0) throw new RpcException("module_not_found",$"No loaded module matches '{module}' in the selected target. Use list_modules and pass an exact filename or name.");
			if(matches.Length>1) throw new RpcException("ambiguous_target",$"Module '{module}' matches more than one active runtime; pass process_id and runtime_id.");
			return matches[0];
		}

		async Task<T> WithMetadataAsync<T>(RpcRequest req,string moduleArgument,Func<ModuleDef,T> callback,CancellationToken cancellationToken) {
			var module=(string?)req.Arguments[moduleArgument];
			if (string.IsNullOrWhiteSpace(module)) throw new RpcException("invalid_arguments",$"{moduleArgument} is required.");
			var dbgModule=await OnDebuggerAsync(()=>FindModule(req,module!),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				// This is also the answer to the in-memory module gap: dnSpy resolves dynamic and
				// in-memory modules through the same service, so metadata is reachable for a module that
				// has no file on disk even though set_il_breakpoint cannot address one yet.
				var metadata=metadataService.TryGetMetadata(dbgModule)
					?? throw new RpcException("metadata_unavailable",$"dnSpy could not load metadata for '{module}'. In-memory and dynamic modules can fail here when the runtime has not published an image.");
				return callback(metadata);
			},cancellationToken).ConfigureAwait(false);
		}

		static bool Matches(string value,string? pattern) =>
			string.IsNullOrEmpty(pattern) || value.IndexOf(pattern,StringComparison.OrdinalIgnoreCase)>=0;

		static SymbolInfo Symbol(string kind,string module,IMemberRef member,TypeDef? declaringType) => new SymbolInfo {
			Kind=kind,Module=module,
			// Token plus module is the durable identity, and it is exactly what set_il_breakpoint takes.
			// Display text is never the identity: an agent must never have to parse it back.
			MethodToken=member.MDToken.ToUInt32(),
			Name=member.Name,
			FullName=declaringType is null ? member.FullName : declaringType.FullName+"."+member.Name,
			DeclaringType=declaringType?.FullName,
			Namespace=declaringType?.Namespace ?? (member as TypeDef)?.Namespace,
		};

		async Task<DocumentInfo[]> ListDocumentsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Select(m=>(Module:m,m.Name,m.Filename,m.IsDynamic,m.IsInMemory,Pid:m.Process.Id)).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>modules.Select(m=>{
				ModuleDef? metadata=null;
				// A module whose metadata will not load is reported as such rather than omitted. Silently
				// dropping it would make a type that genuinely exists look like it does not.
				try { metadata=metadataService.TryGetMetadata(m.Module); } catch (Exception) { }
				return new DocumentInfo {
					Name=m.Name,Filename=m.Filename,ProcessId=m.Pid,IsDynamic=m.IsDynamic,IsInMemory=m.IsInMemory,
					HasMetadata=metadata is not null,
					AssemblyFullName=metadata?.Assembly?.FullName,
					TypeCount=metadata is null ? null : metadata.Types.Count,
				};
			}).ToArray(),cancellationToken).ConfigureAwait(false);
		}

		async Task<SymbolList> ListTypesAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var pattern=(string?)req.Arguments["name_pattern"];
			var offset=Math.Max(0,(int?)req.Arguments["offset"] ?? 0);
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			return await WithMetadataAsync(req,"module",metadata=>{
				var moduleName=metadata.Name?.ToString() ?? "";
				var matching=metadata.GetTypes().Where(t=>Matches(t.FullName,pattern))
					.OrderBy(t=>t.FullName,StringComparer.Ordinal).ToArray();
				return new SymbolList {
					Symbols=matching.Skip(offset).Take(count).Select(t=>Symbol("type",moduleName,t,null)).ToArray(),
					Total=matching.Length,Offset=offset,Truncated=offset+count<matching.Length,
				};
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<SymbolList> ListMembersAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var typeName=(string?)req.Arguments["type"];
			if (string.IsNullOrWhiteSpace(typeName)) throw new RpcException("invalid_arguments","type is required.");
			var pattern=(string?)req.Arguments["name_pattern"];
			var offset=Math.Max(0,(int?)req.Arguments["offset"] ?? 0);
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 200));
			return await WithMetadataAsync(req,"module",metadata=>{
				var moduleName=metadata.Name?.ToString() ?? "";
				var type=FindType(metadata,typeName!);
				var members=new List<SymbolInfo>();
				members.AddRange(type.Methods.Where(m=>Matches(m.Name,pattern)).Select(m=>Symbol("method",moduleName,m,type)));
				members.AddRange(type.Fields.Where(f=>Matches(f.Name,pattern)).Select(f=>Symbol("field",moduleName,f,type)));
				members.AddRange(type.Properties.Where(p=>Matches(p.Name,pattern)).Select(p=>Symbol("property",moduleName,p,type)));
				members.AddRange(type.Events.Where(e=>Matches(e.Name,pattern)).Select(e=>Symbol("event",moduleName,e,type)));
				var ordered=members.OrderBy(m=>m.Kind,StringComparer.Ordinal).ThenBy(m=>m.Name,StringComparer.Ordinal).ToArray();
				return new SymbolList { Symbols=ordered.Skip(offset).Take(count).ToArray(),Total=ordered.Length,Offset=offset,Truncated=offset+count<ordered.Length };
			},cancellationToken).ConfigureAwait(false);
		}

		// Ambiguity is reported, never resolved by picking one. A caller that asked for "Update" and got
		// a breakpoint in the wrong Update has no way to tell, which is the failure this avoids.
		static TypeDef FindType(ModuleDef metadata,string typeName) {
			var exact=metadata.GetTypes().Where(t=>t.FullName==typeName).ToArray();
			if (exact.Length==1) return exact[0];
			if (exact.Length>1) throw new RpcException("ambiguous_type",$"'{typeName}' matches {exact.Length} types in this module.");
			var byName=metadata.GetTypes().Where(t=>t.Name==typeName).ToArray();
			if (byName.Length==1) return byName[0];
			if (byName.Length>1) throw new RpcException("ambiguous_type",$"'{typeName}' matches {byName.Length} types: {string.Join(", ",byName.Take(10).Select(t=>t.FullName))}. Pass a full type name.");
			throw new RpcException("type_not_found",$"No type '{typeName}' in this module. Use list_types.");
		}

		async Task<SymbolList> SearchSymbolsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var pattern=(string?)req.Arguments["pattern"];
			if (string.IsNullOrWhiteSpace(pattern)) throw new RpcException("invalid_arguments","pattern is required.");
			var kinds=req.Arguments["kinds"]?.ToObject<string[]>() ?? new[]{"type","method"};
			var moduleFilter=(string?)req.Arguments["module"];
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Where(m=>string.IsNullOrEmpty(moduleFilter) || m.Name.IndexOf(moduleFilter,StringComparison.OrdinalIgnoreCase)>=0 || m.Filename.IndexOf(moduleFilter,StringComparison.OrdinalIgnoreCase)>=0)
				.ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var results=new List<SymbolInfo>(); var total=0;
				foreach (var dbgModule in modules) {
					ModuleDef? metadata=null;
					try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
					if (metadata is null) continue;
					var moduleName=metadata.Name?.ToString() ?? dbgModule.Name;
					foreach (var type in metadata.GetTypes()) {
						if (kinds.Contains("type") && Matches(type.FullName,pattern)) { total++; if (results.Count<count) results.Add(Symbol("type",moduleName,type,null)); }
						if (kinds.Contains("method"))
							foreach (var method in type.Methods) { if (!Matches(method.Name,pattern)) continue; total++; if (results.Count<count) results.Add(Symbol("method",moduleName,method,type)); }
						if (kinds.Contains("field"))
							foreach (var field in type.Fields) { if (!Matches(field.Name,pattern)) continue; total++; if (results.Count<count) results.Add(Symbol("field",moduleName,field,type)); }
					}
				}
				return new SymbolList { Symbols=results.ToArray(),Total=total,Offset=0,Truncated=total>results.Count };
			},cancellationToken).ConfigureAwait(false);
		}

		static MethodDef FindMethod(ModuleDef metadata,RpcRequest req) {
			var token=(uint?)req.Arguments["method_token"];
			if (token.HasValue && token.Value!=0)
				return metadata.ResolveToken(token.Value) as MethodDef
					?? throw new RpcException("method_not_found",$"Token 0x{token.Value:X8} is not a method in this module.");
			var typeName=(string?)req.Arguments["type"];
			var methodName=(string?)req.Arguments["method"];
			if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
				throw new RpcException("invalid_arguments","Pass either method_token, or both type and method.");
			var type=FindType(metadata,typeName!);
			var candidates=type.Methods.Where(m=>m.Name==methodName).ToArray();
			if (candidates.Length==0) throw new RpcException("method_not_found",$"No method '{methodName}' on {type.FullName}. Use list_members.");
			// Overloads are ambiguity, and the caller is given the signatures rather than a guess. This is
			// the Phase 4 exit criterion that moved here with breakpoint-by-name.
			if (candidates.Length>1) {
				var signature=(string?)req.Arguments["signature"];
				var exact=candidates.Where(m=>m.MethodSig?.ToString()==signature || m.FullName==signature).ToArray();
				if (exact.Length!=1)
					throw new RpcException("ambiguous_method",$"'{methodName}' has {candidates.Length} overloads on {type.FullName}: {string.Join(" | ",candidates.Select(m=>m.FullName))}. Pass method_token, or signature with one of these full names.");
				return exact[0];
			}
			return candidates[0];
		}

		async Task<MethodBodyInfo> GetIlAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			return await WithMetadataAsync(req,"module",metadata=>{
				var method=FindMethod(metadata,req);
				var body=method.Body;
				if (body is null) throw new RpcException("no_method_body",$"{method.FullName} has no IL body (abstract, extern, or a P/Invoke).");
				var instructions=body.Instructions.Select(i=>new IlInstruction {
					Offset=i.Offset,OpCode=i.OpCode.Name ?? "",
					Operand=i.Operand is null ? null : i.Operand is IMemberRef member ? member.FullName : i.Operand.ToString(),
					// The single most useful field on Mono: vm.CreateBreakpointRequest throws
					// NO_SEQ_POINT_AT_IL_OFFSET for any offset that is not one of these, and dnSpy turns
					// that into a silently unbound breakpoint. This makes the legal set discoverable
					// instead of something a caller finds by trial and error.
					IsSequencePoint=i.SequencePoint is not null,
					Line=i.SequencePoint?.StartLine,
				}).ToArray();
				return new MethodBodyInfo {
					Module=metadata.Name?.ToString() ?? "",MethodToken=method.MDToken.ToUInt32(),
					FullName=method.FullName,DeclaringType=method.DeclaringType?.FullName,
					MaxStack=body.MaxStack,CodeSize=instructions.Length==0 ? 0 : instructions[instructions.Length-1].Offset,
					LocalCount=body.Variables.Count,ExceptionHandlerCount=body.ExceptionHandlers.Count,
					// False means no PDB was available, not "no legal breakpoint offsets". Those are very
					// different answers for a Mono caller and must not be conflated.
					HasSequencePoints=instructions.Any(i=>i.IsSequencePoint),
					Instructions=instructions,
				};
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<DecompiledCode> GetCSharpAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var wholeType=(bool?)req.Arguments["whole_type"] ?? false;
			return await WithMetadataAsync(req,"module",metadata=>{
				var decompiler=decompilers.AllDecompilers.FirstOrDefault(d=>d.GenericNameUI=="C#") ?? decompilers.Decompiler;
				var output=new StringBuilderDecompilerOutput();
				var context=new DecompilationContext { CancellationToken=cancellationToken };
				string name;
				if (wholeType) {
					var typeName=(string?)req.Arguments["type"] ?? throw new RpcException("invalid_arguments","type is required when whole_type is true.");
					var type=FindType(metadata,typeName);
					name=type.FullName; decompiler.Decompile(type,output,context);
				}
				else {
					var method=FindMethod(metadata,req);
					name=method.FullName; decompiler.Decompile(method,output,context);
				}
				return new DecompiledCode { Module=metadata.Name?.ToString() ?? "",Name=name,Language=decompiler.GenericNameUI,Code=output.ToString() };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<TextSearchResult> SearchTextAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var pattern=(string?)req.Arguments["pattern"];
			if (string.IsNullOrWhiteSpace(pattern)) throw new RpcException("invalid_arguments","pattern is required.");
			var moduleFilter=(string?)req.Arguments["module"];
			var typeFilter=(string?)req.Arguments["type"];
			var count=Math.Min(200,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			var maxMethods=Math.Min(1000,Math.Max(1,(int?)req.Arguments["max_methods"] ?? 200));
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Where(m=>string.IsNullOrEmpty(moduleFilter) || Matches(m.Name,moduleFilter) || Matches(m.Filename,moduleFilter)).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var decompiler=decompilers.AllDecompilers.FirstOrDefault(d=>d.GenericNameUI=="C#") ?? decompilers.Decompiler;
				var hits=new List<TextSearchHit>(); var total=0; var scanned=0; var scanTruncated=false;
				foreach (var dbgModule in modules) {
					cancellationToken.ThrowIfCancellationRequested();
					ModuleDef? metadata=null; try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
					if (metadata is null) continue;
					foreach (var method in metadata.GetTypes().Where(t=>string.IsNullOrEmpty(typeFilter) || Matches(t.FullName,typeFilter)).SelectMany(t=>t.Methods).Where(m=>m.HasBody)) {
						if (scanned>=maxMethods) { scanTruncated=true; break; }
						scanned++;
						cancellationToken.ThrowIfCancellationRequested();
						var output=new StringBuilderDecompilerOutput();
						try { decompiler.Decompile(method,output,new DecompilationContext { CancellationToken=cancellationToken }); } catch (Exception) { continue; }
						var lines=output.ToString().Replace("\r\n","\n").Split('\n');
						for (var i=0;i<lines.Length;i++) if (Matches(lines[i],pattern)) {
							total++; if (hits.Count<count) hits.Add(new TextSearchHit { Module=metadata.Name?.ToString() ?? dbgModule.Name,Type=method.DeclaringType?.FullName ?? "",MethodToken=method.MDToken.ToUInt32(),Method=method.FullName,Line=i+1,Text=lines[i].Trim() });
						}
					}
					if (scanTruncated) break;
				}
				return new TextSearchResult { Hits=hits.ToArray(),Total=total,Truncated=total>hits.Count,ScannedMethods=scanned,ScanTruncated=scanTruncated };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<SymbolList> FindReferencesAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var target=await WithMetadataAsync(req,"module",metadata=>{
				var token=(uint?)req.Arguments["token"] ?? (uint?)req.Arguments["method_token"];
				if (!token.HasValue || token.Value==0) throw new RpcException("invalid_arguments","token is required.");
				return metadata.ResolveToken(token.Value) as IMemberRef ?? throw new RpcException("token_not_found",$"Token 0x{token.Value:X8} is not a member in this module.");
			},cancellationToken).ConfigureAwait(false);
			var moduleFilter=(string?)req.Arguments["search_module"];
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Where(m=>string.IsNullOrEmpty(moduleFilter) || Matches(m.Name,moduleFilter) || Matches(m.Filename,moduleFilter)).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var results=new List<SymbolInfo>(); var total=0;
				foreach (var dbgModule in modules) {
					ModuleDef? metadata=null; try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
					if (metadata is null) continue;
					var moduleName=metadata.Name?.ToString() ?? dbgModule.Name;
					foreach (var method in metadata.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody)) {
						cancellationToken.ThrowIfCancellationRequested();
						if (!method.Body.Instructions.Any(i=>i.Operand is IMemberRef mr && mr.FullName==target.FullName)) continue;
						total++; if (results.Count<count) results.Add(Symbol("method",moduleName,method,method.DeclaringType));
					}
				}
				return new SymbolList { Symbols=results.ToArray(),Total=total,Offset=0,Truncated=total>results.Count };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<SymbolList> FindImplementationsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var target=await WithMetadataAsync(req,"module",metadata=>{
				var token=(uint?)req.Arguments["token"];
				if (token.HasValue && token.Value!=0) return metadata.ResolveToken(token.Value) as TypeDef ?? throw new RpcException("type_not_found",$"Token 0x{token.Value:X8} is not a type.");
				var typeName=(string?)req.Arguments["type"] ?? throw new RpcException("invalid_arguments","Pass token or type.");
				return FindType(metadata,typeName);
			},cancellationToken).ConfigureAwait(false);
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			var moduleFilter=(string?)req.Arguments["search_module"];
			var modules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Where(m=>string.IsNullOrEmpty(moduleFilter) || Matches(m.Name,moduleFilter) || Matches(m.Filename,moduleFilter)).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var results=new List<SymbolInfo>(); var total=0;
				foreach (var dbgModule in modules) {
					ModuleDef? metadata=null; try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
					if (metadata is null) continue;
					var moduleName=metadata.Name?.ToString() ?? dbgModule.Name;
					foreach (var type in metadata.GetTypes()) {
						cancellationToken.ThrowIfCancellationRequested();
						var implements=type.BaseType?.FullName==target.FullName || type.Interfaces.Any(i=>i.Interface?.FullName==target.FullName);
						if (!implements) continue;
						total++; if (results.Count<count) results.Add(Symbol("type",moduleName,type,null));
					}
				}
				return new SymbolList { Symbols=results.ToArray(),Total=total,Offset=0,Truncated=total>results.Count };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<MetadataInfo> GetMetadataAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			return await WithMetadataAsync(req,"module",metadata=>{
				var types=metadata.GetTypes().ToArray();
				var token=(uint?)req.Arguments["token"];
				IMDTokenProvider? resolved=null;
				if (token.HasValue) resolved=metadata.ResolveToken(token.Value) ?? throw new RpcException("token_not_found",$"Token 0x{token.Value:X8} does not exist in this module.");
				var methods=types.Sum(t=>t.Methods.Count); var fields=types.Sum(t=>t.Fields.Count); var assemblyRefs=metadata.GetAssemblyRefs().Count();
				return new MetadataInfo { Module=metadata.Name?.ToString() ?? "",AssemblyFullName=metadata.Assembly?.FullName,Mvid=metadata.Mvid.ToString(),RuntimeVersion=metadata.RuntimeVersion ?? "",TypeCount=types.Length,MethodCount=methods,FieldCount=fields,AssemblyReferenceCount=assemblyRefs,TableRowCounts=new Dictionary<string,int> { ["TypeDef"]=types.Length,["MethodDef"]=methods,["Field"]=fields,["Property"]=types.Sum(t=>t.Properties.Count),["Event"]=types.Sum(t=>t.Events.Count),["AssemblyRef"]=assemblyRefs },Token=token,TokenKind=resolved?.GetType().Name,TokenFullName=(resolved as IMemberRef)?.FullName };
			},cancellationToken).ConfigureAwait(false);
		}

		async Task<RawModuleChunk> GetRawModuleAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var module=(string?)req.Arguments["module"];
			if (string.IsNullOrWhiteSpace(module)) throw new RpcException("invalid_arguments","module is required.");
			var offset=Math.Max(0,(int?)req.Arguments["offset"] ?? 0);
			var count=Math.Min(1024*1024,Math.Max(1,(int?)req.Arguments["count"] ?? 256*1024));
			var dbgModule=await OnDebuggerAsync(()=>FindModule(req,module!),cancellationToken).ConfigureAwait(false);
			var filename=await OnDebuggerAsync(()=>dbgModule.Filename,cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				byte[] bytes;
				if (!string.IsNullOrEmpty(filename) && File.Exists(filename)) bytes=File.ReadAllBytes(filename);
				else {
					var metadata=metadataService.TryGetMetadata(dbgModule) ?? throw new RpcException("metadata_unavailable",$"dnSpy could not load metadata for '{module}'.");
					using (var stream=new MemoryStream()) { metadata.Write(stream); bytes=stream.ToArray(); }
				}
				if (offset>bytes.Length) throw new RpcException("invalid_arguments",$"offset {offset} exceeds module size {bytes.Length}.");
				var length=Math.Min(count,bytes.Length-offset); var chunk=new byte[length]; Buffer.BlockCopy(bytes,offset,chunk,0,length);
				string hash; using (var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
				return new RawModuleChunk { Module=module!,Offset=offset,Count=length,TotalSize=bytes.Length,Truncated=offset+length<bytes.Length,Sha256=hash,DataBase64=Convert.ToBase64String(chunk) };
			},cancellationToken).ConfigureAwait(false);
		}

		// Breakpoint by name. This is the tool that removes the "you must already know a metadata token"
		// wall: it resolves the token here and then delegates to the exact same IL-offset path, so
		// binding behaviour, sequence-point snapping and cursor_event_id are identical.
		async Task<BreakpointInfo> SetNamedBreakpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var resolved=await WithMetadataAsync(req,"module",metadata=>{
				var method=FindMethod(metadata,req);
				return method.MDToken.ToUInt32();
			},cancellationToken).ConfigureAwait(false);
			var module=req.Arguments.Value<string>("module")!;
			req.Arguments["method_token"]=resolved;
			if (req.Arguments["il_offset"] is null) req.Arguments["il_offset"]=0;
			return await SetBreakpointAsync(req,cancellationToken).ConfigureAwait(false);
		}
	}
}
