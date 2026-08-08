using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

public class ProgramOutputAssemblerTests {
	static ProgramOutputOrigin Out(int pid=1) => new ProgramOutputOrigin("StandardOutput",pid,"runtime");
	static ProgramOutputOrigin Err(int pid=1) => new ProgramOutputOrigin("StandardError",pid,"runtime");

	static (ProgramOutputAssembler Assembler,List<(string Category,string Line)> Lines) New(int maxPending=4096) {
		var lines=new List<(string,string)>();
		// A quiet period long enough that no test races the timer: every flush here is explicit.
		var assembler=new ProgramOutputAssembler((origin,line) => lines.Add((origin.Category,line)),TimeSpan.FromHours(1),maxPending);
		return (assembler,lines);
	}

	[Fact]
	public void EmitsCompleteLinesAndStripsTheLineBreak() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"READY pid=15380\r\nFINGERPRINT -304823368\n");
		Assert.Equal(new[]{"READY pid=15380","FINGERPRINT -304823368"},lines.Select(l => l.Line));
	}

	[Fact]
	public void JoinsALineSplitAcrossReads() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"VERDICT ");
		Assert.Empty(lines);
		assembler.Append(Out(),"DEN");
		assembler.Append(Out(),"IED\r\n");
		Assert.Equal(new[]{"VERDICT DENIED"},lines.Select(l => l.Line));
	}

	[Fact]
	public void KeepsTheStreamsSeparateWhileReassembling() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"out-");
		assembler.Append(Err(),"error line\n");
		assembler.Append(Out(),"line\n");
		Assert.Equal(new[]{("StandardError","error line"),("StandardOutput","out-line")},lines);
	}

	[Fact]
	public void FlushPublishesATrailingLineWithNoNewline() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"DONE");
		Assert.Empty(lines);
		assembler.Flush();
		Assert.Equal(new[]{"DONE"},lines.Select(l => l.Line));
	}

	[Fact]
	public void FlushIsIdempotent() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"partial");
		assembler.Flush();
		assembler.Flush();
		Assert.Single(lines);
	}

	[Fact]
	public void ANewlinelessFloodIsEmittedRatherThanBufferedWithoutBound() {
		var (assembler,lines)=New(maxPending:8);
		assembler.Append(Out(),"123456789");
		Assert.Equal(new[]{"123456789"},lines.Select(l => l.Line));
	}

	[Fact]
	public void ResetDropsAPreviousSessionsPartialLine() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"stale fragment");
		assembler.Reset();
		assembler.Append(Out(),"fresh line\n");
		Assert.Equal(new[]{"fresh line"},lines.Select(l => l.Line));
	}

	[Fact]
	public void EmptyLinesSurvive() {
		var (assembler,lines)=New();
		assembler.Append(Out(),"a\n\nb\n");
		Assert.Equal(new[]{"a","","b"},lines.Select(l => l.Line));
	}

	[Fact]
	public void TheQuietPeriodTimerPublishesAPromptNobodyTerminated() {
		var lines=new List<string>();
		using var published=new ManualResetEventSlim();
		var assembler=new ProgramOutputAssembler((_,line) => { lines.Add(line); published.Set(); },TimeSpan.FromMilliseconds(20));
		assembler.Append(Out(),"Enter key: ");
		Assert.True(published.Wait(TimeSpan.FromSeconds(5)),"the pending line was never flushed");
		Assert.Equal(new[]{"Enter key: "},lines);
	}

	[Fact]
	public void OriginsCompareByEveryPart() {
		Assert.Equal(new ProgramOutputOrigin("StandardOutput",7,"r"),new ProgramOutputOrigin("StandardOutput",7,"r"));
		Assert.NotEqual(new ProgramOutputOrigin("StandardOutput",7,"r"),new ProgramOutputOrigin("StandardError",7,"r"));
		Assert.NotEqual(new ProgramOutputOrigin("StandardOutput",7,"r"),new ProgramOutputOrigin("StandardOutput",8,"r"));
	}
}
