namespace dgSpy.Extension {
	// Whether an expression dnSpy composed can be handed back to evaluate/get_members.
	//
	// dnSpy builds a member expression by appending the raw metadata name to the parent expression, and
	// DbgValueNode.CanEvaluateExpression is hardcoded true for every engine-backed node - it means "this
	// is a value row, not a grouping row like 'Raw View'", not "the string parses". For a compiler-
	// generated member the metadata name has no legal spelling in either language, so the composed
	// expression is guaranteed to fail:
	//
	//   aggregate.<AutoName>k__BackingField   ->  error CS1001: Identifier expected
	//
	// There is no escape. C# 6.4.2 requires a unicode escape in an identifier to denote a character that
	// is valid in an identifier, and '<' is not; the verbatim '@' prefix only suppresses keyword meaning.
	// Both were tried against the live evaluator and both fail. So the honest answer is no expression at
	// all, and this decides which rows get one.
	//
	// The test mirrors Roslyn's own GeneratedMetadataNames.IsCompilerGenerated - "starts with '<' or
	// contains '$'" - whose comment notes that none of those names are valid language identifiers. It is
	// applied per member-name segment rather than to the whole string, because the whole string may
	// legitimately contain both characters: '<' and '>' inside a generic type in a cast
	// (((List<int>)x).Count), and a leading '$' on a debugger pseudo-variable ($exception, or an object
	// id like $1). A generated name only ever reaches us as a segment following a '.'.
	static class ExpressionAddressability {
		public static bool IsAddressable(string expression) {
			if (string.IsNullOrEmpty(expression)) return false;
			if (IsGeneratedSegment(expression,0,isLeading:true)) return false;
			for (var i=0;i<expression.Length;i++) {
				if (expression[i]=='.' && IsGeneratedSegment(expression,i+1,isLeading:false)) return false;
			}
			return true;
		}

		// A segment runs from start to the first character that cannot continue a member name. Stopping at
		// '<' is what keeps a generic type argument list in a cast from being read as part of the name.
		static bool IsGeneratedSegment(string expression,int start,bool isLeading) {
			if (start>=expression.Length) return false;
			if (expression[start]=='<') return true;
			// A pseudo-variable legitimately starts with '$'; a '$' anywhere else in a name does not
			// (CS$<>8__locals0, $VB$Local_x).
			if (isLeading && expression[start]=='$') start++;
			for (var i=start;i<expression.Length;i++) {
				var c=expression[i];
				if (c=='.' || c=='(' || c==')' || c=='[' || c==']' || c==',' || c=='<' || c=='>' || c==' ') break;
				if (c=='$') return true;
			}
			return false;
		}
	}
}
