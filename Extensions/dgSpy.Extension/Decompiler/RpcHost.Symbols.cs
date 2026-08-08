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
		// list_modules and list_documents used to take no arguments beyond session_id and answer with every
		// module. On a Unity player that is a 60,131-character response that exceeds a caller's token
		// budget outright -- and module_not_found used to point straight at it, so the recovery advice was
		// "go and blow your context". Both are paged now, with the same defaults.
		const int MaxModuleResults = 500;
		const int DefaultModuleResults = 100;
		/// <summary>search_text's per-call ceiling on decompiled methods. Unchanged; it is a work bound and
		/// relaxing it is not what a caller needs. A cursor is.</summary>
		const int MaxTextScan = 1000;

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

		// Module names have one rule for the whole family, and it lives in ModuleNameMatch -- kept
		// dnlib- and dnSpy-free so tests\dgSpy.Extension.Tests can exercise it against strings, the same
		// way SymbolSearchQuery is. Rank() ranks a query against one module and Matches() is its filter
		// form; the header comment there records why the rule is ranked rather than flat.

		DbgModule FindModule(RpcRequest req,string module) {
			var processId=(int?)req.Arguments["process_id"]; var runtimeId=(string?)req.Arguments["runtime_id"];
			var scoped=manager.Processes.Where(p=>!processId.HasValue||p.Id==processId.Value).SelectMany(p=>p.Runtimes)
				.Where(r=>string.IsNullOrEmpty(runtimeId)||StringComparer.OrdinalIgnoreCase.Equals(r.Guid.ToString("D"),runtimeId)||StringComparer.OrdinalIgnoreCase.Equals(r.Name,runtimeId))
				.SelectMany(r=>r.Modules).ToArray();
			var ranked=scoped.Select(m=>new { Module=m,Rank=ModuleNameMatch.Rank(m.Name,m.Filename,module) }).Where(x=>x.Rank<ModuleNameMatch.None).ToArray();
			if (ranked.Length==0) throw new RpcException("module_not_found",DescribeMissingModule(scoped,module));
			// Only the closest tier competes. A query that exactly names one module is never ambiguous
			// merely because it is also a substring of another.
			var best=ranked.Min(x=>x.Rank);
			var matches=ranked.Where(x=>x.Rank==best).Select(x=>x.Module).ToArray();
			if (matches.Length>1)
				throw new RpcException("ambiguous_target",$"'{module}' matches {matches.Length} loaded modules: {string.Join(", ",matches.Take(10).Select(m=>m.Name))}. Pass one of those names, or process_id and runtime_id if the same module is loaded in more than one runtime.");
			return matches[0];
		}

		/// <summary>Names the near misses instead of sending the caller to <c>list_modules</c>. The old
		/// message did the latter, and against a Unity player that is 170 modules and 60,131 characters of
		/// answer -- an error that tells a caller to go and blow its own context. Now <c>list_modules</c>
		/// pages and filters, so naming a few candidates here is both cheap and usually the whole fix.</summary>
		static string DescribeMissingModule(DbgModule[] loaded,string query) {
			var candidates=ModuleNameMatch.NearMisses(loaded.Select(m=>((string?)m.Name,(string?)m.Filename)),query);
			var directory=$"{loaded.Length} module(s) are loaded; list_modules takes name_pattern, count and offset, so it can be searched without returning all of them.";
			return candidates.Length==0
				? $"No loaded module matches '{query}'. {directory}"
				: $"No loaded module matches '{query}'. Closest loaded names: {string.Join(", ",candidates)}. {directory}";
		}

		/// <summary>The session's modules, filtered by the one module rule and returned in a deterministic
		/// order. The order is not tidiness: a resume cursor counts slots in traversal order, so two calls
		/// carrying the same arguments have to walk the same modules in the same sequence, and the
		/// debugger's own enumeration order guarantees nothing of the kind. Must run on the debugger
		/// thread.</summary>
		DbgModule[] ScanModules(string? moduleFilter) =>
			manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules)
				.Where(m=>ModuleNameMatch.Matches(m.Name,m.Filename,moduleFilter))
				.OrderBy(m=>m.Process.Id).ThenBy(m=>m.Order).ThenBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ToArray();

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

		// Paged and filtered for the same reason list_modules is: this walks the same module set, and
		// against a Unity player that is 170 entries. It also costs more per entry -- every row loads
		// metadata to answer has_metadata and type_count -- so paging here saves work, not just output.
		async Task<DocumentList> ListDocumentsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var namePattern=(string?)req.Arguments["name_pattern"];
			var offset=Math.Max(0,(int?)req.Arguments["offset"] ?? 0);
			var count=Math.Min(MaxModuleResults,Math.Max(1,(int?)req.Arguments["count"] ?? DefaultModuleResults));
			var modules=await OnDebuggerAsync(()=>ScanModules(namePattern)
				.Select(m=>(Module:m,m.Name,m.Filename,m.IsDynamic,m.IsInMemory,Pid:m.Process.Id)).ToArray(),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>new DocumentList {
				Documents=modules.Skip(offset).Take(count).Select(m=>{
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
				}).ToArray(),
				Total=modules.Length,Offset=offset,Truncated=offset+count<modules.Length,
			},cancellationToken).ConfigureAwait(false);
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
			throw new RpcException("type_not_found",DescribeMissingType(metadata,typeName));
		}

		/// <summary>A caller that asks for <c>GameState.ChatSystem</c> is usually holding a member path, not
		/// a type name. Answering with a bare "no such type" once led an agent to report that
		/// <c>GameState.ChatSystem.ChatMode</c> did not exist, when GameState is a type, ChatSystem is a
		/// ChatDisplay field on it and ChatMode is a field on ChatDisplay. So when the prefix does resolve
		/// to a type carrying that member, say which type to ask for instead.</summary>
		static string DescribeMissingType(ModuleDef metadata,string typeName) {
			var split=typeName.LastIndexOf('.');
			if (split>0 && split<typeName.Length-1) {
				var prefix=typeName.Substring(0,split); var memberName=typeName.Substring(split+1);
				var owners=metadata.GetTypes().Where(t=>t.FullName==prefix || t.Name==prefix).Take(2).ToArray();
				if (owners.Length==1) {
					var owner=owners[0];
					var field=owner.Fields.FirstOrDefault(f=>f.Name==memberName);
					if (field is not null)
						return $"'{typeName}' is not a type. '{memberName}' is a field on type '{owner.FullName}', of type '{field.FieldType?.FullName}'. Pass that type to list_members, or use search to walk the path.";
					var property=owner.Properties.FirstOrDefault(p=>p.Name==memberName);
					if (property is not null)
						return $"'{typeName}' is not a type. '{memberName}' is a property on type '{owner.FullName}', of type '{property.PropertySig?.RetType?.FullName}'. Pass that type to list_members, or use search to walk the path.";
					if (owner.Methods.Any(m=>m.Name==memberName) || owner.Events.Any(e=>e.Name==memberName))
						return $"'{typeName}' is not a type. '{memberName}' is a member of type '{owner.FullName}'. Use list_members on '{owner.FullName}', or search.";
				}
			}
			return $"No type '{typeName}' in this module. Use search to find it by name, or list_types to enumerate.";
		}

		async Task<SymbolList> SearchSymbolsAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			var pattern=(string?)req.Arguments["pattern"];
			if (string.IsNullOrWhiteSpace(pattern)) throw new RpcException("invalid_arguments","pattern is required.");
			var kinds=ProtocolJson.FromNode<string[]>(req.Arguments["kinds"]) ?? new[]{"type","method"};
			var moduleFilter=(string?)req.Arguments["module"];
			var count=Math.Min(MaxSymbolResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100));
			var modules=await OnDebuggerAsync(()=>ScanModules(moduleFilter),cancellationToken).ConfigureAwait(false);
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
			// The bound is unchanged and deliberately still small -- this decompiles every method it looks
			// at. What is new is that a spent bound is no longer the end of the road: the walk counts method
			// slots in a deterministic order, so next_scan_offset resumes exactly where it stopped and a
			// caller can cover a 20311-method Assembly-CSharp a page at a time. Without it, an agent
			// sweeping a plugin for hotkey definitions hit the cap, was told scan_truncated, and reported
			// the sweep complete; the user's own UI then showed three keybinds it had never reached.
			var walk=new ScanCursor {
				Skip=Math.Max(0,(int?)req.Arguments["scan_offset"] ?? 0),
				Max=Math.Min(MaxTextScan,Math.Max(1,(int?)req.Arguments["max_methods"] ?? 200)),
			};
			var modules=await OnDebuggerAsync(()=>ScanModules(moduleFilter),cancellationToken).ConfigureAwait(false);
			return await evaluations.RunAsync(()=>{
				var decompiler=decompilers.AllDecompilers.FirstOrDefault(d=>d.GenericNameUI=="C#") ?? decompilers.Decompiler;
				var hits=new List<TextSearchHit>(); var total=0;
				foreach (var dbgModule in modules) {
					cancellationToken.ThrowIfCancellationRequested();
					ModuleDef? metadata=null; try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
					// A module with no metadata claims no slots, so it cannot shift the cursor. Nothing here
					// may depend on state that varies between two calls with the same arguments.
					if (metadata is null) continue;
					foreach (var method in metadata.GetTypes().Where(t=>string.IsNullOrEmpty(typeFilter) || Matches(t.FullName,typeFilter)).SelectMany(t=>t.Methods).Where(m=>m.HasBody)) {
						if (!walk.Claim()) { if (walk.Truncated) break; continue; }
						cancellationToken.ThrowIfCancellationRequested();
						var output=new StringBuilderDecompilerOutput();
						try { decompiler.Decompile(method,output,new DecompilationContext { CancellationToken=cancellationToken }); } catch (Exception) { continue; }
						var lines=output.ToString().Replace("\r\n","\n").Split('\n');
						for (var i=0;i<lines.Length;i++) if (Matches(lines[i],pattern)) {
							total++; if (hits.Count<count) hits.Add(new TextSearchHit { Module=metadata.Name?.ToString() ?? dbgModule.Name,Type=method.DeclaringType?.FullName ?? "",MethodToken=method.MDToken.ToUInt32(),Method=method.FullName,Line=i+1,Text=lines[i].Trim() });
						}
					}
					if (walk.Truncated) break;
				}
				return new TextSearchResult {
					Hits=hits.ToArray(),Total=total,Truncated=total>hits.Count,
					ScannedMethods=walk.Worked,ScanTruncated=walk.Truncated,NextScanOffset=walk.Inspected,
				};
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
			var modules=await OnDebuggerAsync(()=>ScanModules(moduleFilter),cancellationToken).ConfigureAwait(false);
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
			var modules=await OnDebuggerAsync(()=>ScanModules(moduleFilter),cancellationToken).ConfigureAwait(false);
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
			var module=req.Arguments["module"]?.GetValue<string>()!;
			req.Arguments["method_token"]=resolved;
			// JsonNode retains the CLR numeric type assigned here. SetBreakpointAsync reads UInt32, so an
			// Int32 zero would throw instead of using the named-breakpoint default.
			if (req.Arguments["il_offset"] is null) req.Arguments["il_offset"]=(uint)0;
			return await SetBreakpointAsync(req,cancellationToken).ConfigureAwait(false);
		}
	}
}
