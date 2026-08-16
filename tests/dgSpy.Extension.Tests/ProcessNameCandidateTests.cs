using System.ComponentModel;
using dnSpy.Debugger.Attach;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ProcessNameCandidateTests {
	[Fact] public void ReturnsTheReadExecutableName() {
		Assert.True(ProcessNameCandidate.TryReadExecutableName(()=>"Barnyard.exe",out var name));
		Assert.Equal("Barnyard.exe",name);
	}

	[Theory]
	[MemberData(nameof(ExpectedInspectionFailures))]
	public void ExpectedInspectionFailureRejectsOnlyTheCandidate(Exception failure) {
		Assert.False(ProcessNameCandidate.TryReadExecutableName(()=>throw failure,out var name));
		Assert.Equal(string.Empty,name);
	}

	[Fact] public void UnexpectedFailureIsNotSwallowed() {
		Assert.Throws<NotSupportedException>(()=>ProcessNameCandidate.TryReadExecutableName(()=>throw new NotSupportedException(),out _));
	}

	[Fact] public void NullReaderIsRejected() {
		Assert.Throws<ArgumentNullException>(()=>ProcessNameCandidate.TryReadExecutableName(null!,out _));
	}

	public static IEnumerable<object[]> ExpectedInspectionFailures() {
		yield return new object[]{new Win32Exception(998)};
		yield return new object[]{new Win32Exception(5)};
		yield return new object[]{new InvalidOperationException()};
		yield return new object[]{new ArgumentException()};
	}
}
