using System.ComponentModel;
using dgSpy.Extension.Debugger;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ProgramDiscoverySelectionTests {
	[Theory]
	[InlineData("Barnyard","Barnyard")]
	[InlineData("barnyard","Barnyard")]
	[InlineData("Barnyard.exe","Barnyard")]
	[InlineData("Barn*","Barnyard")]
	[InlineData("Barn?ard","Barnyard")]
	public void MatchesCanonicalProcessNames(string pattern,string processName) {
		var result=Resolve(new[]{pattern},Array.Empty<int>(),Candidate(42,processName));
		Assert.Equal(new[]{42},result.ProcessIds);
	}

	[Fact] public void PidsAndNamesAreIntersected() {
		var result=Resolve(new[]{"Barnyard"},new[]{42},Candidate(41,"Barnyard"),Candidate(42,"Barnyard"),Candidate(43,"Other"));
		Assert.Equal(new[]{42},result.ProcessIds);
	}

	[Fact] public void MissingNameReturnsEmptySelection() {
		var result=Resolve(new[]{"Missing"},Array.Empty<int>(),Candidate(42,"Barnyard"));
		Assert.Empty(result.ProcessIds);
	}

	[Fact] public void ExpectedCandidateFailuresAreSkippedIndividually() {
		var candidates=new[]{
			Candidate(()=>1,()=>throw new Win32Exception(998)),
			Candidate(()=>2,()=>throw new InvalidOperationException()),
			Candidate(()=>3,()=>throw new ArgumentException()),
			Candidate(4,"Barnyard"),
		};
		var result=Resolve(new[]{"Barnyard"},Array.Empty<int>(),candidates);
		Assert.Equal(new[]{4},result.ProcessIds);
		Assert.Equal(3,result.SkippedCandidates);
	}

	[Fact] public void UnexpectedCandidateFailureIsNotSwallowed() {
		Assert.Throws<NotSupportedException>(()=>Resolve(new[]{"Barnyard"},Array.Empty<int>(),Candidate(()=>1,()=>throw new NotSupportedException())));
	}

	[Theory]
	[InlineData("")]
	[InlineData("C:\\Games\\Barnyard.exe")]
	[InlineData("folder/Barnyard")]
	public void InvalidNameSelectorsAreRejected(string pattern) {
		Assert.Throws<ArgumentException>(()=>Resolve(new[]{pattern},Array.Empty<int>(),Candidate(42,"Barnyard")));
	}

	[Fact] public void NewGenerationInvalidatesPriorProgramsImmediately() {
		var cache=new ProgramDiscoveryCache<object>();
		var first=cache.Begin();
		var value=new object();
		Assert.True(cache.TryPublish(first,new[]{new KeyValuePair<string,object>("old",value)}));
		Assert.True(cache.TryGetValue("old",out _));
		cache.Begin();
		Assert.False(cache.TryGetValue("old",out _));
	}

	[Fact] public void OlderCompletionCannotOverwriteNewerGeneration() {
		var cache=new ProgramDiscoveryCache<object>();
		var first=cache.Begin();
		var second=cache.Begin();
		var current=new object();
		Assert.True(cache.TryPublish(second,new[]{new KeyValuePair<string,object>("new",current)}));
		Assert.False(cache.TryPublish(first,new[]{new KeyValuePair<string,object>("old",new object())}));
		Assert.True(cache.TryGetValue("new",out var actual));
		Assert.Same(current,actual);
		Assert.False(cache.TryGetValue("old",out _));
	}

	static ProcessDiscoverySelection Resolve(string[] names,int[] ids,params ProcessDiscoveryCandidate[] candidates) => ProgramDiscoverySelector.Resolve(names,ids,candidates);
	static ProcessDiscoveryCandidate Candidate(int id,string name) => Candidate(()=>id,()=>name);
	static ProcessDiscoveryCandidate Candidate(Func<int> id,Func<string> name) => new ProcessDiscoveryCandidate(id,name);
}
