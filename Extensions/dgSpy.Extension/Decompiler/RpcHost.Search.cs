using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger;

namespace dgSpy.Extension {
	// dnSpy's Search window, as an MCP tool. The matching rules are ported from dnSpy rather than driven
	// through IDocumentSearcher: that service is internal to dnSpy.Contracts.DnSpy behind a closed
	// InternalsVisibleTo list, it searches Assembly Explorer tree nodes rather than the ModuleDefs every
	// other decompiler tool here works from, and it delivers UI-flavoured results asynchronously through
	// events. docs/SEARCH_PROPOSAL.md records the decision in full. SymbolSearchQuery holds the ported
	// comparer and name-candidate logic, dnlib-free so the test project can exercise it.
	sealed partial class RpcHost {
		const int MaxSearchResults = 500;
		const int DefaultSearchScan = 200000;
		const int MaxSearchScan = 2000000;

		/// <summary>One module to walk, resolved to metadata before the walk starts. <see cref="InSession"/>
		/// is the fact a caller needs before it tries a session-scoped follow-up call, and it is why
		/// documents scope is safe to offer: a hit in an Assembly Explorer document is never silently
		/// presented as something get_il will accept.</summary>
		sealed class SearchModule {
			public ModuleDef Metadata=null!;
			public string? ModuleId;
			public string Name="";
			public string? Path;
			public bool InSession;
		}

		/// <summary>Bounded, resumable walk state. The counter lives in <see cref="ScanCursor"/>, which
		/// <c>search_text</c> and <c>analyze_symbol</c> now share: it is over inspected symbol slots in a
		/// deterministic traversal order, which is what lets <c>next_scan_offset</c> mean "resume exactly
		/// here" rather than "roughly there".</summary>
		sealed class SearchWalk : ScanCursor {
			public int Total;
			public readonly List<SearchHit> Hits=new List<SearchHit>();
			public int Count;

			public void Add(SearchHit hit) { Total++; if (Hits.Count<Count) Hits.Add(hit); }
		}

		async Task<SearchResults> SearchAsync(RpcRequest req,CancellationToken cancellationToken) {
			var pattern=(string?)req.Arguments["pattern"];
			if (string.IsNullOrWhiteSpace(pattern)) throw new RpcException("invalid_arguments","pattern is required.");
			var scope=((string?)req.Arguments["scope"] ?? "session").ToLowerInvariant();
			if (scope is not ("session" or "documents" or "all"))
				throw new RpcException("invalid_arguments",$"scope must be session, documents or all, not '{scope}'.");
			// documents scope reads dnSpy's Assembly Explorer, which exists whether or not anything is
			// being debugged. Requiring a session there would refuse the one thing this scope is for.
			if (scope!="documents") CheckSession(req);

			var kindNames=ProtocolJson.FromNode<string[]>(req.Arguments["kinds"]) ?? new[]{"type","member"};
			var targets=SymbolSearchQuery.ParseKinds(kindNames,out var literal,out var unknownKind);
			if (unknownKind is not null)
				throw new RpcException("invalid_arguments",$"Unknown kind '{unknownKind}'. Valid kinds: {string.Join(", ",SymbolSearchQuery.KindNames)}.");
			// Literal mode swaps the comparer for one that reads constant values instead of names. A query
			// asking for both would mean neither, so it is refused rather than silently reinterpreted.
			if (literal && targets!=SearchTargets.Literal)
				throw new RpcException("invalid_arguments","kinds:[\"literal\"] searches constant values and cannot be combined with name kinds. Issue it as a separate call.");

			var query=SymbolSearchQuery.Parse(pattern!,
				(bool?)req.Arguments["case_sensitive"] ?? false,
				(bool?)req.Arguments["whole_word"] ?? false,
				(bool?)req.Arguments["match_any_word"] ?? false,
				targets,literal);
			var compilerGenerated=(bool?)req.Arguments["compiler_generated"] ?? true;
			var moduleFilter=(string?)req.Arguments["module"];
			var walk=new SearchWalk {
				Count=Math.Min(MaxSearchResults,Math.Max(1,(int?)req.Arguments["count"] ?? 100)),
				Skip=Math.Max(0,(int?)req.Arguments["scan_offset"] ?? 0),
				Max=Math.Min(MaxSearchScan,Math.Max(1,(int?)req.Arguments["max_scan"] ?? DefaultSearchScan)),
			};

			// Read the session's modules for every scope, documents included. That scope does not *search*
			// them, but it still has to answer in_session truthfully, and it cannot do that without knowing
			// what the session holds. When nothing is being debugged this is an empty enumeration, which is
			// what makes the scope usable with no session at all.
			var sessionModules=await OnDebuggerAsync(()=>manager.Processes.SelectMany(p=>p.Runtimes).SelectMany(r=>r.Modules).Select(module=>(Module:module,ModuleId:ModuleIdOf(module),Name:module.Name,Filename:module.Filename,ProcessId:module.Process.Id,Runtime:module.Runtime.Guid,AppDomainId:module.AppDomain?.Id ?? -1,Order:module.Order)).ToArray(),cancellationToken).ConfigureAwait(false);

			return await evaluations.RunAsync(()=>{
				var modules=CollectSearchModules(scope,sessionModules,moduleFilter);
				foreach (var module in modules) {
					cancellationToken.ThrowIfCancellationRequested();
					SearchOneModule(module,query,walk,compilerGenerated,cancellationToken);
					if (walk.Truncated) break;
				}
				return new SearchResults {
					Hits=walk.Hits.ToArray(),
					Total=walk.Total,
					Truncated=walk.Total>walk.Hits.Count,
					Scanned=walk.Worked,
					ScanTruncated=walk.Truncated,
					NextScanOffset=walk.Inspected,
					ModulesSearched=modules.Select(m=>m.Name).ToArray(),
				};
			},cancellationToken).ConfigureAwait(false);
		}

		/// <summary>Resolves the requested scope to a stable module list. Loaded instances are retained;
		/// only the Assembly Explorer copy is de-duplicated under <c>all</c>. Order must not vary
		/// between two calls with the same arguments or the resume cursor is meaningless, so the union is
		/// session-first and then sorted within each source.</summary>
		List<SearchModule> CollectSearchModules(string scope,(DbgModule Module,string ModuleId,string Name,string Filename,int ProcessId,Guid Runtime,int AppDomainId,int Order)[] sessionModules,string? moduleFilter) {
			var result=new List<SearchModule>();
			var seenInstances=new HashSet<ModuleDef>();
			var seenMvids=new HashSet<Guid>();
			// Reference identity and MVID survive the common views of the same code. A name deliberately
			// does not participate: two unrelated assemblies called Helpers.dll must remain two modules.
			// The debugger's
			// metadata service and the Assembly Explorer hand back *different* ModuleDef instances for one
			// module, so reference identity does not join them; and against a live Unity player the MVID
			// does not join them either -- a `scope: "all"` search walked all 54 modules twice until the
			// name key was added.
			//
			bool IsNew(ModuleDef metadata) {
				if (!seenInstances.Add(metadata)) return false;
				var mvid=metadata.Mvid;
				return !mvid.HasValue || mvid.Value==Guid.Empty || seenMvids.Add(mvid.Value);
			}
			// Resolve the session's metadata once, before walking anything. Two things need it: the session
			// branch below, and in_session, which is a fact about the module rather than about which loop
			// happened to find it. Deriving the flag from the branch reported in_session:false under
			// `scope: "documents"` for a module the session tools accept without complaint -- a tool saying
			// a symbol is out of reach when it is not, which is the class of wrong answer this exists to end.
			var resolvedSession=new List<(ModuleDef Metadata,string ModuleId,string Name,string? Path)>();
			foreach (var selected in sessionModules.OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ThenBy(m=>m.Filename,StringComparer.OrdinalIgnoreCase)
				.ThenBy(m=>m.ProcessId).ThenBy(m=>m.Runtime).ThenBy(m=>m.AppDomainId).ThenBy(m=>m.Order)) {
				var dbgModule=selected.Module;
				ModuleDef? metadata=null;
				try { metadata=metadataService.TryGetMetadata(dbgModule); } catch (Exception) { }
				if (metadata is null) continue;
				resolvedSession.Add((metadata,selected.ModuleId,metadata.Name?.ToString() ?? selected.Name,selected.Filename));
			}
			var sessionPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var sessionMvids=new HashSet<Guid>();
			var sessionIdByPath=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
			var sessionIdByMvid=new Dictionary<Guid,string>();
			foreach (var entry in resolvedSession) {
				if (!string.IsNullOrEmpty(entry.Path)) { var path=NormalizePath(entry.Path!); sessionPaths.Add(path); if (!sessionIdByPath.ContainsKey(path)) sessionIdByPath[path]=entry.ModuleId; }
				var mvid=entry.Metadata.Mvid;
				if (mvid.HasValue && mvid.Value!=Guid.Empty) { sessionMvids.Add(mvid.Value); if (!sessionIdByMvid.ContainsKey(mvid.Value)) sessionIdByMvid[mvid.Value]=entry.ModuleId; }
			}
			bool IsInSession(ModuleDef metadata,string? path) {
				var mvid=metadata.Mvid;
				return (mvid.HasValue && mvid.Value!=Guid.Empty && sessionMvids.Contains(mvid.Value))
					|| (!string.IsNullOrEmpty(path) && sessionPaths.Contains(NormalizePath(path!)));
			}
			string? SessionModuleId(ModuleDef metadata,string? path) {
				var mvid=metadata.Mvid;
				if (mvid.HasValue && mvid.Value!=Guid.Empty && sessionIdByMvid.TryGetValue(mvid.Value,out var byMvid)) return byMvid;
				return !string.IsNullOrEmpty(path) && sessionIdByPath.TryGetValue(NormalizePath(path!),out var byPath) ? byPath : null;
			}

			if (scope is "session" or "all") {
				foreach (var entry in resolvedSession) {
					if (!MatchesModuleFilter(entry.Name,entry.Path,moduleFilter)) continue;
					// Every loaded instance remains visible. Two app domains can carry the same MVID and
					// path but have different module_id values, and collapsing those would recreate the
					// ambiguity this identity is designed to remove.
					result.Add(new SearchModule { Metadata=entry.Metadata,ModuleId=entry.ModuleId,Name=entry.Name,Path=entry.Path,InSession=true });
				}
				if(scope=="all") foreach(var entry in resolvedSession) { seenInstances.Add(entry.Metadata); var mvid=entry.Metadata.Mvid; if(mvid.HasValue && mvid.Value!=Guid.Empty) seenMvids.Add(mvid.Value); }
			}
			if (scope is "documents" or "all") {
				foreach (var document in documentService.GetDocuments().OrderBy(d=>d.Filename,StringComparer.OrdinalIgnoreCase)) {
					var metadata=document.ModuleDef;
					if (metadata is null) continue;
					var name=metadata.Name?.ToString() ?? document.Filename ?? "";
					if (!MatchesModuleFilter(name,document.Filename,moduleFilter)) continue;
					// Session first, so under `all` a shared module is walked once, as the session copy,
					// carrying the identifiers the session-scoped tools actually accept.
					if (!IsNew(metadata)) continue;
					result.Add(new SearchModule { Metadata=metadata,ModuleId=SessionModuleId(metadata,document.Filename),Name=name,Path=document.Filename,InSession=IsInSession(metadata,document.Filename) });
				}
			}
			return result;
		}

		static string NormalizePath(string path) {
			try { return Path.GetFullPath(path); } catch { return path; }
		}

		// The same module-name rule the resolving tools use, so a name that get_csharp accepts also filters
		// here and vice versa. Half this family used to be exact and half substring, with no way to tell.
		static bool MatchesModuleFilter(string name,string? path,string? filter) => ModuleNameMatch.Matches(name,path,filter);

		void SearchOneModule(SearchModule module,SymbolSearchQuery query,SearchWalk walk,bool compilerGenerated,CancellationToken cancellationToken) {
			var targets=query.Targets;
			var metadata=module.Metadata;

			if ((targets&SearchTargets.ModuleDef)!=0 && walk.Claim() && query.MatchesText(metadata.Name?.ToString()))
				walk.Add(Hit(module,"module",0,metadata.Name?.ToString() ?? module.Name,metadata.Name?.ToString() ?? module.Name,null,null,null,null));
			if ((targets&SearchTargets.AssemblyDef)!=0 && metadata.Assembly is AssemblyDef assembly && walk.Claim()
				&& (query.MatchesText(assembly.FullName) || query.MatchesText(assembly.Name)))
				walk.Add(Hit(module,"assembly",assembly.MDToken.ToUInt32(),assembly.Name,assembly.FullName,null,null,null,null));
			if ((targets&SearchTargets.AssemblyRef)!=0)
				foreach (var reference in metadata.GetAssemblyRefs()) {
					if (!walk.Claim()) { if (walk.Truncated) return; continue; }
					if (query.MatchesText(reference.FullName) || query.MatchesText(reference.Name))
						walk.Add(Hit(module,"assembly_ref",reference.MDToken.ToUInt32(),reference.Name,reference.FullName,null,null,module.Name,null));
				}
			if ((targets&SearchTargets.ModuleRef)!=0)
				foreach (var reference in metadata.GetModuleRefs()) {
					if (!walk.Claim()) { if (walk.Truncated) return; continue; }
					if (query.MatchesText(reference.FullName)) walk.Add(Hit(module,"module_ref",reference.MDToken.ToUInt32(),reference.Name,reference.FullName,null,null,module.Name,null));
				}
			if ((targets&SearchTargets.Resource)!=0)
				foreach (var resource in metadata.Resources) {
					if (!walk.Claim()) { if (walk.Truncated) return; continue; }
					// In literal mode a resource contributes its name as a string value, matching dnSpy's
					// inclusion of Resource in the Number/String flag set.
					var hit=query.IsLiteral ? query.MatchesLiteral(resource.Name?.ToString()) : query.MatchesText(resource.Name?.ToString());
					if (hit) walk.Add(Hit(module,"resource",0,resource.Name?.ToString() ?? "",resource.Name?.ToString() ?? "",null,null,module.Name,null));
				}
			if ((targets&SearchTargets.Namespace)!=0)
				foreach (var ns in metadata.Types.Select(t=>t.Namespace?.ToString() ?? "").Distinct(StringComparer.Ordinal).OrderBy(n=>n,StringComparer.Ordinal)) {
					if (!walk.Claim()) { if (walk.Truncated) return; continue; }
					if (query.MatchesText(ns)) walk.Add(Hit(module,"namespace",0,ns,ns,null,ns,module.Name,null));
				}

			var wantsTypes=(targets&SearchTargets.AnyTypeDef)!=0;
			var wantsMembers=(targets&(SearchTargets.Member|SearchTargets.ParamDef|SearchTargets.Local|SearchTargets.MethodBody))!=0;
			if (!wantsTypes && !wantsMembers) return;
			foreach (var type in metadata.GetTypes()) {
				cancellationToken.ThrowIfCancellationRequested();
				if (!compilerGenerated && IsCompilerGenerated(type)) continue;
				if (wantsTypes) {
					if (!walk.Claim()) { if (walk.Truncated) return; }
					else if (MatchesTypeCategory(type,targets) && query.MatchesTypeName(type.FullName,type.Name)) {
						var fixedName=SymbolSearchQuery.FixTypeName(type.FullName);
						walk.Add(Hit(module,"type",type.MDToken.ToUInt32(),SymbolSearchQuery.FixTypeName(type.Name),fixedName,
							type.DeclaringType is null ? null : SymbolSearchQuery.FixTypeName(type.DeclaringType.FullName),
							type.Namespace?.ToString(),
							type.DeclaringType is null ? type.Namespace?.ToString() : SymbolSearchQuery.FixTypeName(type.DeclaringType.FullName),null));
					}
				}
				if (wantsMembers) { SearchTypeMembers(module,type,query,walk,compilerGenerated,cancellationToken); if (walk.Truncated) return; }
			}
		}

		void SearchTypeMembers(SearchModule module,TypeDef type,SymbolSearchQuery query,SearchWalk walk,bool compilerGenerated,CancellationToken cancellationToken) {
			var targets=query.Targets;
			var declaringType=SymbolSearchQuery.FixTypeName(type.FullName);
			foreach (var method in type.Methods) {
				cancellationToken.ThrowIfCancellationRequested();
				if (!compilerGenerated && IsCompilerGenerated(method)) continue;
				if ((targets&SearchTargets.MethodDef)!=0) {
					if (!walk.Claim()) { if (walk.Truncated) return; }
					else if (query.MatchesMemberName(method.Name,type.FullName,method.FullName))
						walk.Add(Member(module,"method",method,type,declaringType,null));
				}
				if ((targets&SearchTargets.ParamDef)!=0)
					foreach (var parameter in method.ParamDefs) {
						if (!walk.Claim()) { if (walk.Truncated) return; continue; }
						if (query.MatchesText(parameter.Name)) { walk.Add(Member(module,"method",method,type,declaringType,$"parameter '{parameter.Name}'")); break; }
					}
				if ((targets&SearchTargets.Local)!=0 && method.Body is CilBody localBody)
					foreach (var local in localBody.Variables) {
						if (!walk.Claim()) { if (walk.Truncated) return; continue; }
						if (query.MatchesText(local.Name)) { walk.Add(Member(module,"method",method,type,declaringType,$"local '{local.Name}'")); break; }
					}
				if ((targets&SearchTargets.MethodBody)!=0 && method.Body is CilBody body) {
					if (!walk.Claim()) { if (walk.Truncated) return; }
					else SearchMethodBodyLiterals(module,type,method,declaringType,body,query,walk);
				}
			}
			if ((targets&SearchTargets.FieldDef)!=0)
			foreach (var field in type.Fields) {
				if (!compilerGenerated && IsCompilerGenerated(field)) continue;
				if (!walk.Claim()) { if (walk.Truncated) return; continue; }
				if (query.IsLiteral) {
					if (field.Constant is not null && query.MatchesLiteral(field.Constant.Value))
						walk.Add(Member(module,"field",field,type,declaringType,$"constant {Describe(field.Constant.Value)}"));
				}
				else if (query.MatchesMemberName(field.Name,type.FullName,field.FullName))
					walk.Add(Member(module,"field",field,type,declaringType,null));
			}
			if ((targets&SearchTargets.PropertyDef)!=0)
			foreach (var property in type.Properties) {
				if (!compilerGenerated && IsCompilerGenerated(property)) continue;
				if (!walk.Claim()) { if (walk.Truncated) return; continue; }
				if (query.IsLiteral) {
					if (property.Constant is not null && query.MatchesLiteral(property.Constant.Value))
						walk.Add(Member(module,"property",property,type,declaringType,$"constant {Describe(property.Constant.Value)}"));
				}
				else if (query.MatchesMemberName(property.Name,type.FullName,property.FullName))
					walk.Add(Member(module,"property",property,type,declaringType,null));
			}
			if ((targets&SearchTargets.EventDef)!=0 && !query.IsLiteral)
			foreach (var evt in type.Events) {
				if (!compilerGenerated && IsCompilerGenerated(evt)) continue;
				if (!walk.Claim()) { if (walk.Truncated) return; continue; }
				if (query.MatchesMemberName(evt.Name,type.FullName,evt.FullName))
					walk.Add(Member(module,"event",evt,type,declaringType,null));
			}
		}

		/// <summary>dnSpy's literal search over a method body: only <c>ldc.*</c> and <c>ldstr</c> operands
		/// are considered, and the first match ends the scan of that body. Anything else would compare a
		/// search value against a token operand, which is meaningless.</summary>
		static void SearchMethodBodyLiterals(SearchModule module,TypeDef type,MethodDef method,string declaringType,CilBody body,SymbolSearchQuery query,SearchWalk walk) {
			if (!query.IsLiteral) return;
			foreach (var instruction in body.Instructions) {
				object? operand=instruction.OpCode.Code switch {
					Code.Ldc_I4_M1 => (object?)(-1),
					Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
					Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7,
					Code.Ldc_I4_8 => 8,
					Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_R4 or Code.Ldc_R8 or Code.Ldstr => instruction.Operand,
					_ => null,
				};
				if (operand is null || !query.MatchesLiteral(operand)) continue;
				walk.Add(Member(module,"method",method,type,declaringType,$"{instruction.OpCode.Name} at IL_{instruction.Offset:X4}: {Describe(operand)}"));
				return;
			}
		}

		static string Describe(object? value) => value is string text
			? "\""+(text.Length>60 ? text.Substring(0,60)+"..." : text)+"\""
			: value?.ToString() ?? "null";

		/// <summary>dnSpy splits TypeDef into the Generic/Enum/Interface/Class/Struct/Delegate entries of its
		/// Search For dropdown. A query that asked only for <c>interface</c> must not be answered with
		/// classes, so the plain TypeDef bit means "any type" and the others narrow it.</summary>
		static bool MatchesTypeCategory(TypeDef type,SearchTargets targets) {
			if ((targets&SearchTargets.TypeDef)!=0) return true;
			if ((targets&SearchTargets.GenericTypeDef)!=0 && type.HasGenericParameters) return true;
			if ((targets&SearchTargets.NonGenericTypeDef)!=0 && !type.HasGenericParameters) return true;
			if ((targets&SearchTargets.EnumTypeDef)!=0 && type.IsEnum) return true;
			if ((targets&SearchTargets.InterfaceTypeDef)!=0 && type.IsInterface) return true;
			if ((targets&SearchTargets.DelegateTypeDef)!=0 && type.IsDelegate) return true;
			// Order matters: an enum and a delegate are both value/reference types that would otherwise fall
			// into struct and class, so they are decided above this point.
			if ((targets&SearchTargets.StructTypeDef)!=0 && type.IsValueType && !type.IsEnum) return true;
			if ((targets&SearchTargets.ClassTypeDef)!=0 && type.IsClass && !type.IsValueType && !type.IsDelegate) return true;
			return false;
		}

		static bool IsCompilerGenerated(IHasCustomAttribute member) =>
			member.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute");

		static SearchHit Member(SearchModule module,string kind,IMemberRef member,TypeDef type,string declaringType,string? context) =>
			Hit(module,kind,member.MDToken.ToUInt32(),member.Name,declaringType+"."+member.Name,declaringType,type.Namespace?.ToString(),declaringType,context);

		static SearchHit Hit(SearchModule module,string kind,uint token,string name,string fullName,string? declaringType,string? ns,string? location,string? context) => new SearchHit {
			Kind=kind,Module=module.Name,ModuleId=module.ModuleId,ModulePath=string.IsNullOrEmpty(module.Path) ? null : module.Path,InSession=module.InSession,
			Token=token,Name=name,FullName=fullName,DeclaringType=declaringType,
			Namespace=string.IsNullOrEmpty(ns) ? null : ns,Location=string.IsNullOrEmpty(location) ? null : location,MatchContext=context,
		};
	}
}
