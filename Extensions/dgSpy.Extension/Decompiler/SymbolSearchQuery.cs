using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace dgSpy.Extension {
	/// <summary>What a search is allowed to match. Mirrors dnSpy's
	/// <c>dnSpy.Contracts.Search.VisibleMembersFlags</c> one bit at a time, deliberately: the wire names
	/// in <see cref="SymbolSearchQuery.ParseKinds"/> are the snake_case spelling of dnSpy's own
	/// "Search For" dropdown, and keeping the two enums shaped alike is what makes that table checkable
	/// by eye. This is a separate enum rather than a reference to dnSpy's because this file is compiled
	/// into the .NET test project, which has no dnSpy dependency.</summary>
	[Flags]
	enum SearchTargets {
		None			= 0,
		AssemblyDef		= 0x00000001,
		ModuleDef		= 0x00000002,
		Namespace		= 0x00000004,
		TypeDef			= 0x00000008,
		FieldDef		= 0x00000010,
		MethodDef		= 0x00000020,
		PropertyDef		= 0x00000040,
		EventDef		= 0x00000080,
		AssemblyRef		= 0x00000100,
		ModuleRef		= 0x00000800,
		GenericTypeDef	= 0x00004000,
		NonGenericTypeDef=0x00008000,
		EnumTypeDef		= 0x00010000,
		InterfaceTypeDef= 0x00020000,
		ClassTypeDef	= 0x00040000,
		StructTypeDef	= 0x00080000,
		DelegateTypeDef	= 0x00100000,
		MethodBody		= 0x00200000,
		ParamDef		= 0x01000000,
		Local			= 0x02000000,
		Resource		= 0x08000000,

		TypeDefOther	= GenericTypeDef | NonGenericTypeDef | EnumTypeDef | InterfaceTypeDef | ClassTypeDef | StructTypeDef | DelegateTypeDef,
		AnyTypeDef		= TypeDef | TypeDefOther,
		Member			= MethodDef | FieldDef | PropertyDef | EventDef,
		// dnSpy's "All of the Above" is TreeViewAll | ParamDef | Local. TreeViewAll's BaseTypes,
		// DerivedTypes, ResourceList, NonNetFile and Other entries are treeview presentation rows with no
		// metadata symbol behind them, so they have no wire kind and are not part of this.
		Any				= AssemblyDef | ModuleDef | Namespace | TypeDef | Member | AssemblyRef | ModuleRef | Resource | ParamDef | Local,
		// dnSpy's Number/String entry. Its Attributes bit is deliberately absent: we do not walk custom
		// attribute arguments, and the tool description says so rather than implying coverage we lack.
		Literal			= MethodBody | FieldDef | ParamDef | PropertyDef | Resource,
	}

	/// <summary>A parsed search pattern. Ported from dnSpy's <c>SearchComparerFactory</c> and the
	/// <c>CheckMatch</c> family in <c>dnSpy/Search/FilterSearcher.cs</c> (GPLv3, de4dot@gmail.com).
	///
	/// The port exists because dnSpy's own <c>ISearchComparer</c>/<c>IDocumentSearcher</c> are internal to
	/// <c>dnSpy.Contracts.DnSpy</c> behind a closed <c>InternalsVisibleTo</c> list that does not include
	/// this extension, and because the service searches Assembly Explorer tree nodes rather than the
	/// <c>ModuleDef</c>s every other dgSpy decompiler tool works from. See docs/SEARCH_PROPOSAL.md.
	///
	/// Nothing here touches dnlib or dnSpy, so it compiles into dgSpy.Extension.Tests.</summary>
	sealed class SymbolSearchQuery {
		/// <summary>Matches a candidate display string. Always false in literal mode.</summary>
		public Func<string?,bool> MatchesText { get; }
		/// <summary>Matches a boxed constant, <c>ldc.*</c>/<c>ldstr</c> operand, or resource name. Always
		/// false outside literal mode -- dnSpy behaves the same way, because its name comparers reject a
		/// null <c>text</c> and its literal comparers reject a non-constant <c>obj</c>.</summary>
		public Func<object?,bool> MatchesLiteral { get; }
		public SearchTargets Targets { get; }
		public bool IsLiteral { get; }

		SymbolSearchQuery(Func<string?,bool> matchesText,Func<object?,bool> matchesLiteral,SearchTargets targets,bool isLiteral) {
			MatchesText=matchesText; MatchesLiteral=matchesLiteral; Targets=targets; IsLiteral=isLiteral;
		}

		/// <summary>Wire name to flags. The table is dnSpy's SearchControlVM "Search For" dropdown, in its
		/// order. Returns null for an unknown name so the caller can report which one was wrong.</summary>
		public static SearchTargets? ParseKind(string kind) => kind switch {
			"assembly" => SearchTargets.AssemblyDef,
			"module" => SearchTargets.ModuleDef,
			"namespace" => SearchTargets.Namespace,
			"type" => SearchTargets.TypeDef,
			"field" => SearchTargets.FieldDef,
			"method" => SearchTargets.MethodDef,
			"property" => SearchTargets.PropertyDef,
			"event" => SearchTargets.EventDef,
			"parameter" => SearchTargets.ParamDef,
			"local" => SearchTargets.Local,
			"parameter_or_local" => SearchTargets.ParamDef|SearchTargets.Local,
			"assembly_ref" => SearchTargets.AssemblyRef,
			"module_ref" => SearchTargets.ModuleRef,
			"resource" => SearchTargets.Resource,
			"generic_type" => SearchTargets.GenericTypeDef,
			"non_generic_type" => SearchTargets.NonGenericTypeDef,
			"enum" => SearchTargets.EnumTypeDef,
			"interface" => SearchTargets.InterfaceTypeDef,
			"class" => SearchTargets.ClassTypeDef,
			"struct" => SearchTargets.StructTypeDef,
			"delegate" => SearchTargets.DelegateTypeDef,
			"member" => SearchTargets.Member,
			"any" => SearchTargets.Any,
			"literal" => SearchTargets.Literal,
			_ => null,
		};

		public static readonly string[] KindNames = {
			"assembly","module","namespace","type","field","method","property","event",
			"parameter","local","parameter_or_local","assembly_ref","module_ref","resource",
			"generic_type","non_generic_type","enum","interface","class","struct","delegate",
			"member","any","literal",
		};

		/// <summary>Folds the requested kinds together. <c>literal</c> selects a different comparer, so
		/// mixing it with a name kind would silently produce a query that means neither thing; the caller
		/// turns a false <paramref name="literal"/>-plus-names combination into invalid_arguments.</summary>
		public static SearchTargets ParseKinds(IEnumerable<string> kinds,out bool literal,out string? unknownKind) {
			var targets=SearchTargets.None; literal=false; unknownKind=null; var any=false;
			foreach (var kind in kinds) {
				var parsed=ParseKind(kind);
				if (parsed is null) { unknownKind=kind; return SearchTargets.None; }
				if (kind=="literal") literal=true;
				targets|=parsed.Value; any=true;
			}
			return any ? targets : SearchTargets.TypeDef|SearchTargets.Member;
		}

		public static SymbolSearchQuery Parse(string pattern,bool caseSensitive,bool wholeWord,bool matchAnyWord,SearchTargets targets,bool literal) {
			var comparison=caseSensitive ? StringComparison.InvariantCulture : StringComparison.InvariantCultureIgnoreCase;
			if (literal) {
				var (text,value)=BuildLiteral(pattern,caseSensitive,wholeWord,comparison);
				return new SymbolSearchQuery(text,value,targets,true);
			}
			var regex=TryCreateRegex(pattern,caseSensitive);
			if (regex is not null) return new SymbolSearchQuery(s=>s is not null && regex.IsMatch(s),_=>false,targets,false);
			var terms=pattern.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
			Func<string?,bool> match=s=>{
				if (s is null) return false;
				foreach (var term in terms) {
					var hit=wholeWord ? s.Equals(term,comparison) : s.IndexOf(term,comparison)>=0;
					// AND is the default, matching dnSpy: "chat mode" means both terms, not either.
					if (matchAnyWord) { if (hit) return true; }
					else if (!hit) return false;
				}
				return !matchAnyWord && terms.Length>0;
			};
			return new SymbolSearchQuery(match,_=>false,targets,false);
		}

		static (Func<string?,bool>,Func<object?,bool>) BuildLiteral(string pattern,bool caseSensitive,bool wholeWord,StringComparison comparison) {
			Func<string?,bool> noText=_=>false;
			var s=pattern.Trim();
			if (TryParseInt64(s) is long signed) return (noText,value=>MatchesInteger(value,signed));
			if (TryParseUInt64(s) is ulong unsigned) return (noText,value=>MatchesInteger(value,unchecked((long)unsigned)));
			if (double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var dbl)) return (noText,value=>MatchesDouble(value,dbl));
			if (s.Length>=2 && s[0]=='"' && s[s.Length-1]=='"') s=s.Substring(1,s.Length-2);
			else {
				var regex=TryCreateRegex(s,caseSensitive);
				if (regex is not null) return (noText,value=>value is string text && regex.IsMatch(text));
			}
			var needle=s;
			return (noText,value=>value is string text && (wholeWord ? text.Equals(needle,comparison) : text.IndexOf(needle,comparison)>=0));
		}

		static bool MatchesInteger(object? value,long search) => value is null ? false : Type.GetTypeCode(value.GetType()) switch {
			TypeCode.Char => search==(char)value,
			TypeCode.SByte => search==(sbyte)value,
			TypeCode.Byte => search==(byte)value,
			TypeCode.Int16 => search==(short)value,
			TypeCode.UInt16 => search==(ushort)value,
			TypeCode.Int32 => search==(int)value,
			TypeCode.UInt32 => search==(uint)value,
			TypeCode.Int64 => search==(long)value,
			TypeCode.UInt64 => search==unchecked((long)(ulong)value),
			TypeCode.Single => search==(float)value,
			TypeCode.Double => search==(double)value,
			TypeCode.Decimal => search==(decimal)value,
			TypeCode.DateTime => new DateTime(search)==(DateTime)value,
			_ => false,
		};

		static bool MatchesDouble(object? value,double search) => value is null ? false : Type.GetTypeCode(value.GetType()) switch {
			TypeCode.Char => search==(char)value,
			TypeCode.SByte => search==(sbyte)value,
			TypeCode.Byte => search==(byte)value,
			TypeCode.Int16 => search==(short)value,
			TypeCode.UInt16 => search==(ushort)value,
			TypeCode.Int32 => search==(int)value,
			TypeCode.UInt32 => search==(uint)value,
			TypeCode.Int64 => search==(long)value,
			TypeCode.UInt64 => search==(ulong)value,
			TypeCode.Single => search==(float)value,
			TypeCode.Double => search==(double)value,
			_ => false,
		};

		/// <summary>dnSpy's convention: a pattern wrapped in slashes is a regular expression. An invalid
		/// expression falls back to a literal substring search rather than failing the call, which is what
		/// the GUI does -- a half-typed regex should narrow results, not produce an error dialog.</summary>
		static Regex? TryCreateRegex(string s,bool caseSensitive) {
			s=s.Trim();
			if (s.Length<=2 || s[0]!='/' || s[s.Length-1]!='/') return null;
			var options=RegexOptions.None;
			if (!caseSensitive) options|=RegexOptions.IgnoreCase;
			try { return new Regex(s.Substring(1,s.Length-2),options); }
			catch (ArgumentException) { return null; }
		}

		static long? TryParseInt64(string s) {
			var negative=s.StartsWith("-",StringComparison.Ordinal);
			if (negative) s=s.Substring(1);
			if (s!=s.Trim()) return null;
			ulong value;
			if (s.StartsWith("0x",StringComparison.OrdinalIgnoreCase)) {
				s=s.Substring(2);
				if (s!=s.Trim() || !ulong.TryParse(s,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out value)) return null;
			}
			else if (!ulong.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out value)) return null;
			if (negative) return value>(ulong)long.MaxValue+1 ? (long?)null : unchecked(-(long)value);
			return value>long.MaxValue ? (long?)null : (long)value;
		}

		static ulong? TryParseUInt64(string s) {
			if (s!=s.Trim()) return null;
			if (s.StartsWith("0x",StringComparison.OrdinalIgnoreCase)) {
				s=s.Substring(2);
				return s==s.Trim() && ulong.TryParse(s,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var hex) ? hex : (ulong?)null;
			}
			return ulong.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out var value) ? value : (ulong?)null;
		}

		/// <summary>dnSpy's <c>FilterSearcher.FixTypeName</c>: nested-type separators become dots and
		/// generic arity suffixes are dropped, so <c>Outer/Inner`1</c> reads as <c>Outer.Inner</c>. This is
		/// the form the GUI matches against and the form we emit, which is what makes a returned
		/// <c>full_name</c> or <c>declaring_type</c> work when it is passed straight back in.</summary>
		public static string FixTypeName(string name) {
			var cut=-1;
			for (var i=0;i<name.Length;i++) if (name[i]=='/' || name[i]=='`') { cut=i; break; }
			if (cut<0) return name;
			var builder=new StringBuilder(name.Length);
			builder.Append(name,0,cut);
			for (var i=cut;i<name.Length;i++) {
				var c=name[i];
				if (c=='/') builder.Append('.');
				else if (c=='`') { while (i+1<name.Length && char.IsDigit(name[i+1])) i++; }
				else builder.Append(c);
			}
			return builder.ToString();
		}

		/// <summary>The candidate strings dnSpy tests for a member, in <c>FilterSearcher.CheckMatch</c>
		/// order. The two qualified forms are the whole point: without them
		/// <c>search_symbols("GameState.ChatSystem")</c> returns nothing for a field the tool itself
		/// reports as <c>GameState.ChatSystem</c>.</summary>
		public bool MatchesMemberName(string name,string declaringTypeFullName,string? signatureFullName) {
			if (MatchesText(name)) return true;
			var fixedType=FixTypeName(declaringTypeFullName);
			if (MatchesText(fixedType+"."+name)) return true;
			if (MatchesText(fixedType+"::"+name)) return true;
			return signatureFullName is not null && MatchesText(signatureFullName);
		}

		/// <summary>The candidate strings dnSpy tests for a type: the namespace-qualified full name in
		/// <see cref="FixTypeName"/> form, then the short name.</summary>
		public bool MatchesTypeName(string fullName,string name) => MatchesText(FixTypeName(fullName)) || MatchesText(FixTypeName(name));
	}
}
