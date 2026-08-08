using System;
using System.Collections.Generic;
using System.Linq;

namespace dgSpy.Extension {
	/// <summary>The one rule for matching a caller-supplied module string against a loaded module.
	///
	/// It used to be two rules. The predicate was written out inline in both <c>FindModule</c> overloads
	/// and compared for equality, while <c>search_symbols</c>, <c>search_text</c> and <c>search</c> took a
	/// substring -- so <c>get_csharp(module: "Assembly-CSharp")</c> answered <c>module_not_found</c> while
	/// <c>search(module: "Assembly-CSharp")</c> happily searched it, and nothing in either schema said
	/// which half a tool belonged to. Equality missed because <c>Name</c> and <c>Filename</c> both carry
	/// the ".dll", and the old <c>EndsWith("\\"+module)</c> arm missed for exactly the same reason: the
	/// path ends "\Assembly-CSharp.dll".
	///
	/// The rule is ranked rather than flat, because a filter and a resolver want different things from one
	/// query. A filter selects a set, and a plain substring is right for it. A resolver has to pick exactly
	/// one module, and a plain substring would make "Assembly-CSharp" ambiguous against
	/// "Assembly-CSharp-firstpass.dll" -- a regression wearing a fix's clothes. Ranking keeps the
	/// substring's reach while letting an exact stem win outright, so both halves of the family accept the
	/// same strings and neither has to guess.
	///
	/// Everything here is case-insensitive, which is why a caller transcribing
	/// <c>Microsoft.PowerShell.PSReadLine.dll</c> off disk still reaches the module dnSpy reports as
	/// <c>Microsoft.Powershell.PSReadline.dll</c>. That was never the defect; exact-versus-substring was.
	///
	/// dnlib- and dnSpy-free on purpose, so tests\dgSpy.Extension.Tests can exercise it against strings.</summary>
	static class ModuleNameMatch {
		/// <summary>The query <i>is</i> the module's name, its filename, or its path leaf.</summary>
		public const int Exact = 0;
		/// <summary>The query is one of those without its file extension.</summary>
		public const int Stemmed = 1;
		/// <summary>The query occurs somewhere inside the name or the path.</summary>
		public const int Substring = 2;
		/// <summary>Higher than any real rank: no match at all.</summary>
		public const int None = 3;

		public static int Rank(string? name,string? filename,string query) {
			if (string.IsNullOrEmpty(query)) return None;
			var moduleName=name ?? ""; var path=filename ?? ""; var leaf=Leaf(path);
			if (Same(moduleName,query)||Same(path,query)||Same(leaf,query)) return Exact;
			if (Same(Stem(moduleName),query)||Same(Stem(leaf),query)) return Stemmed;
			if (Contains(moduleName,query)||Contains(path,query)) return Substring;
			return None;
		}

		/// <summary>The filter form: an empty query matches everything, and any rank at all matches this
		/// module. Every tool taking a <c>module</c> or <c>search_module</c> filter goes through here, so a
		/// name that resolves for <c>get_csharp</c> also filters for <c>search_text</c>.</summary>
		public static bool Matches(string? name,string? filename,string? query) =>
			string.IsNullOrEmpty(query) || Rank(name,filename,query!)<None;

		/// <summary>Names the near misses for a <c>module_not_found</c> message. The old message sent the
		/// caller to <c>list_modules</c> instead, and against a Unity player that was 170 modules and
		/// 60,131 characters of answer: an error telling a caller to go and blow its own context. Both
		/// listing tools page now, so this is the cheap half of the fix and usually the whole one.</summary>
		public static string[] NearMisses(IEnumerable<(string? Name,string? Filename)> loaded,string query,int limit=8) {
			// Two ways to be close, because the two failures look different.
			//
			// Tokens catch a query that names part of a dotted name: matching on "PSReadLine" finds what
			// matching on the full transcription cannot. Two characters would nominate almost everything,
			// so three is the floor, and the extension is dropped before splitting or "dll" becomes a token
			// that nominates every module loaded.
			//
			// Squashing catches the separator being wrong, which is the commonest miss of all --
			// "AssemblyCSharp" for "Assembly-CSharp.dll" shares no token with it at all. Comparing both
			// directions covers a query shorter than the module's name and one longer than it.
			var stem=Stem(Leaf(query));
			var tokens=stem.Split(new[]{'.','\\','/','-','_',' ',','},StringSplitOptions.RemoveEmptyEntries).Where(t=>t.Length>=3).ToArray();
			var squashed=Squash(stem);
			return loaded
				.Where(m=>tokens.Any(t=>Contains(m.Name ?? "",t)||Contains(Leaf(m.Filename ?? ""),t))
					|| Overlaps(squashed,Squash(Stem(m.Name ?? ""))))
				.Select(m=>m.Name ?? "").Where(n=>n.Length>0)
				.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
		}

		public static string Leaf(string path) {
			var separator=path.LastIndexOfAny(new[]{'\\','/'});
			return separator<0 ? path : path.Substring(separator+1);
		}
		public static string Stem(string value) { var dot=value.LastIndexOf('.'); return dot>0 ? value.Substring(0,dot) : value; }

		/// <summary>Lowercased, with every separator removed, so "Assembly-CSharp" and "AssemblyCSharp"
		/// become the same string. Only ever used for suggesting near misses --- never for resolving one,
		/// where guessing past a separator would be exactly the kind of "helpful" wrong answer this surface
		/// refuses to give.</summary>
		static string Squash(string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
		static bool Overlaps(string a,string b) => a.Length>=3 && b.Length>=3 && (a.IndexOf(b,StringComparison.Ordinal)>=0 || b.IndexOf(a,StringComparison.Ordinal)>=0);

		static bool Same(string value,string query) => value.Length>0 && string.Equals(value,query,StringComparison.OrdinalIgnoreCase);
		static bool Contains(string value,string query) => value.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0;
	}
}
