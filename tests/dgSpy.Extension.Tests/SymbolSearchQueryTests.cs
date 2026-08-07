using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>The matching rules ported from dnSpy's Search window. The first test here is the bug that
/// motivated the whole tool: <c>search_symbols</c> returns a member as <c>ChatDisplay.ChatMode</c> and
/// then finds nothing when that exact string is handed back to it.</summary>
public class SymbolSearchQueryTests {
	static SymbolSearchQuery Query(string pattern,bool caseSensitive=false,bool wholeWord=false,bool anyWord=false,bool literal=false) =>
		SymbolSearchQuery.Parse(pattern,caseSensitive,wholeWord,anyWord,literal ? SearchTargets.Literal : SearchTargets.TypeDef|SearchTargets.Member,literal);

	[Theory]
	// The reported failures, verbatim.
	[InlineData("ChatMode",true)]
	[InlineData("ChatDisplay.ChatMode",true)]
	[InlineData("ChatDisplay::ChatMode",true)]
	// Still discriminating: a different declaring type must not match.
	[InlineData("GameState.ChatMode",false)]
	[InlineData("ChatDisplay.Other",false)]
	public void QualifiedMemberPathsMatch(string pattern,bool expected) =>
		Assert.Equal(expected,Query(pattern).MatchesMemberName("ChatMode","ChatDisplay","System.Boolean ChatDisplay::ChatMode"));

	[Fact]
	public void FieldOnATypeIsFoundByItsQualifiedPath() =>
		// GameState.ChatSystem is the field an agent was told did not exist.
		Assert.True(Query("GameState.ChatSystem").MatchesMemberName("ChatSystem","GameState","ChatDisplay GameState::ChatSystem"));

	[Fact]
	public void SignatureFullNameMatches() =>
		Assert.True(Query("System.Boolean ChatDisplay::ChatMode").MatchesMemberName("ChatMode","ChatDisplay","System.Boolean ChatDisplay::ChatMode"));

	[Theory]
	[InlineData("Outer/Inner`1","Outer.Inner")]
	[InlineData("Ns.Outer/Inner/Deep","Ns.Outer.Inner.Deep")]
	[InlineData("List`1","List")]
	[InlineData("Plain","Plain")]
	public void FixTypeNameMatchesDnSpy(string raw,string expected) => Assert.Equal(expected,SymbolSearchQuery.FixTypeName(raw));

	[Fact]
	public void NestedGenericTypeIsFoundByItsDottedName() =>
		Assert.True(Query("Outer.Inner").MatchesTypeName("Ns.Outer/Inner`1","Inner`1"));

	[Fact]
	public void TypeIsAlsoFoundByShortName() =>
		Assert.True(Query("Inner").MatchesTypeName("Ns.Outer/Inner`1","Inner`1"));

	// dnSpy ANDs space-separated terms and ORs them only when asked.
	[Fact] public void TermsAreAndedByDefault() {
		Assert.True(Query("chat mode").MatchesText("ChatModeToggle"));
		Assert.False(Query("chat mode").MatchesText("ChatDisplay"));
	}
	[Fact] public void MatchAnyWordOrsTheTerms() {
		Assert.True(Query("chat mode",anyWord:true).MatchesText("ChatDisplay"));
		Assert.False(Query("chat mode",anyWord:true).MatchesText("Inventory"));
	}
	[Fact] public void CaseSensitivityIsOptIn() {
		Assert.True(Query("chatmode").MatchesText("ChatMode"));
		Assert.False(Query("chatmode",caseSensitive:true).MatchesText("ChatMode"));
	}
	[Fact] public void WholeWordRequiresTheEntireString() {
		Assert.True(Query("ChatMode",wholeWord:true).MatchesText("ChatMode"));
		Assert.False(Query("ChatMode",wholeWord:true).MatchesText("ChatModeToggle"));
	}

	[Fact] public void SlashesMakeItARegularExpression() {
		var query=Query("/^Chat.*Mode$/");
		Assert.True(query.MatchesText("ChatDisplayMode"));
		Assert.False(query.MatchesText("PreChatMode"));
	}
	[Fact] public void AnInvalidRegexFallsBackToSubstringRatherThanThrowing() =>
		// The GUI narrows results while a regex is half-typed instead of erroring; so do we.
		Assert.True(Query("/[unclosed/").MatchesText("x/[unclosed/y"));

	// Literal mode: names never match, values do. This is the mirror of dnSpy's comparers rejecting the
	// argument they do not own.
	[Fact] public void LiteralModeIgnoresNames() {
		Assert.False(Query("42",literal:true).MatchesText("42"));
		Assert.False(Query("\"hello\"",literal:true).MatchesText("hello"));
	}
	[Theory]
	[InlineData("42",42,true)]
	[InlineData("42",43,false)]
	[InlineData("0x2A",42,true)]
	[InlineData("-1",-1,true)]
	public void LiteralIntegersMatchAcrossNumericTypes(string pattern,int value,bool expected) {
		var query=Query(pattern,literal:true);
		Assert.Equal(expected,query.MatchesLiteral(value));
		Assert.Equal(expected,query.MatchesLiteral((long)value));
		Assert.Equal(expected,query.MatchesLiteral((short)value));
	}
	[Fact] public void LiteralDoublesMatch() => Assert.True(Query("3.5",literal:true).MatchesLiteral(3.5));
	[Fact] public void QuotedLiteralIsAnExactStringSubstring() {
		Assert.True(Query("\"not enough\"",literal:true).MatchesLiteral("There is not enough space"));
		Assert.False(Query("\"not enough\"",literal:true).MatchesLiteral(42));
	}
	[Fact] public void UnquotedLiteralRegexMatchesStrings() =>
		Assert.True(Query("/^err/",literal:true).MatchesLiteral("error code"));

	// Kind taxonomy: every entry of dnSpy's "Search For" dropdown has a wire name, and an unknown one is
	// reported rather than silently ignored -- a typo'd kind must not quietly widen the search.
	[Fact] public void EveryAdvertisedKindParses() {
		foreach (var kind in SymbolSearchQuery.KindNames) Assert.NotNull(SymbolSearchQuery.ParseKind(kind));
		Assert.Equal(24,SymbolSearchQuery.KindNames.Length);
	}
	[Fact] public void UnknownKindIsReported() {
		SymbolSearchQuery.ParseKinds(new[]{"type","propery"},out _,out var unknown);
		Assert.Equal("propery",unknown);
	}
	[Fact] public void MemberExpandsToTheFourMemberKinds() {
		var targets=SymbolSearchQuery.ParseKinds(new[]{"member"},out _,out _);
		Assert.Equal(SearchTargets.MethodDef|SearchTargets.FieldDef|SearchTargets.PropertyDef|SearchTargets.EventDef,targets);
	}
	[Fact] public void NoKindsMeansTypesAndMembers() =>
		Assert.Equal(SearchTargets.TypeDef|SearchTargets.Member,SymbolSearchQuery.ParseKinds(new string[0],out _,out _));
	[Fact] public void LiteralIsFlaggedAndIsolated() {
		var targets=SymbolSearchQuery.ParseKinds(new[]{"literal"},out var literal,out _);
		Assert.True(literal);
		Assert.Equal(SearchTargets.Literal,targets);
		// Mixed with a name kind the result is no longer exactly Literal, which is what the RPC layer
		// turns into invalid_arguments instead of running a query that means neither thing.
		var mixed=SymbolSearchQuery.ParseKinds(new[]{"literal","type"},out var stillLiteral,out _);
		Assert.True(stillLiteral);
		Assert.NotEqual(SearchTargets.Literal,mixed);
	}
	[Fact] public void ParameterOrLocalCoversBoth() =>
		Assert.Equal(SearchTargets.ParamDef|SearchTargets.Local,SymbolSearchQuery.ParseKinds(new[]{"parameter_or_local"},out _,out _));
}
