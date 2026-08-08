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

	/// <summary>Output must leave in the order it left the buffer, even when a second thread arrives
	/// while the first is inside emit.
	///
	/// The regression: Append and FlushCore both took text out under the lock and emitted after
	/// releasing it. A thread descheduled in that gap let a later writer emit first, and because emit is
	/// what assigns output_id, the newer fragment got the lower id — wait_for_output then reported the
	/// target's console output in an order the target never wrote it in.
	///
	/// Deterministic rather than timing-based: the first emit blocks inside the callback until this test
	/// releases it, so the interleaving is forced rather than hoped for. Before the fix the second
	/// thread sailed past and "second" was recorded first; now it waits behind the record ahead of
	/// it.</summary>
	[Fact]
	public void ConcurrentWritersEmitInBufferOrderEvenWhenEmitBlocks() {
		var lines=new List<string>();
		using var insideFirstEmit=new ManualResetEventSlim(false);
		using var releaseFirstEmit=new ManualResetEventSlim(false);
		var emitted=0;
		var assembler=new ProgramOutputAssembler((_,line) => {
			// Only the first record blocks; the point is to hold the gate, not to deadlock the drain.
			if (Interlocked.Increment(ref emitted)==1) { insideFirstEmit.Set(); Assert.True(releaseFirstEmit.Wait(TimeSpan.FromSeconds(10))); }
			lock(lines) lines.Add(line);
		},TimeSpan.FromHours(1));

		var first=Task.Run(() => assembler.Append(Out(),"first\n"));
		Assert.True(insideFirstEmit.Wait(TimeSpan.FromSeconds(10)),"the first Append never reached emit");

		// Arrives while "first" is mid-emit. It must not overtake it.
		var second=Task.Run(() => assembler.Append(Out(),"second\n"));
		// Give the second thread every chance to overtake, which is what it used to do.
		Thread.Sleep(150);
		lock(lines) Assert.Empty(lines);

		releaseFirstEmit.Set();
		Assert.True(Task.WhenAll(first,second).Wait(TimeSpan.FromSeconds(10)),"an Append never completed");
		Assert.Equal(new[]{"first","second"},lines);
		assembler.Dispose();
	}

	/// <summary>The same ordering guarantee across the two entry points: a Flush landing while an Append
	/// is mid-emit must not publish the trailing fragment ahead of the completed line.</summary>
	[Fact]
	public void AFlushDoesNotOvertakeAnAppendThatIsMidEmit() {
		var lines=new List<string>();
		using var insideFirstEmit=new ManualResetEventSlim(false);
		using var releaseFirstEmit=new ManualResetEventSlim(false);
		var emitted=0;
		var assembler=new ProgramOutputAssembler((_,line) => {
			if (Interlocked.Increment(ref emitted)==1) { insideFirstEmit.Set(); Assert.True(releaseFirstEmit.Wait(TimeSpan.FromSeconds(10))); }
			lock(lines) lines.Add(line);
		},TimeSpan.FromHours(1));

		// "done" completes a line and starts a trailing fragment in one write.
		var append=Task.Run(() => assembler.Append(Out(),"done\ntrailing"));
		Assert.True(insideFirstEmit.Wait(TimeSpan.FromSeconds(10)),"the Append never reached emit");

		var flush=Task.Run(() => assembler.Flush());
		Thread.Sleep(150);
		lock(lines) Assert.Empty(lines);

		releaseFirstEmit.Set();
		Assert.True(Task.WhenAll(append,flush).Wait(TimeSpan.FromSeconds(10)),"a writer never completed");
		Assert.Equal(new[]{"done","trailing"},lines);
		assembler.Dispose();
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
	public void ProcessFlushDoesNotPublishAnotherProcessesPartialLine() {
		var (assembler,lines)=New();
		assembler.Append(Out(1),"first partial");
		assembler.Append(Out(2),"second partial");
		assembler.Flush(1);
		Assert.Equal(new[]{"first partial"},lines.Select(l=>l.Line));
		assembler.Append(Out(2)," completed\n");
		Assert.Equal(new[]{"first partial","second partial completed"},lines.Select(l=>l.Line));
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
		Assert.NotEqual(new ProgramOutputOrigin("StandardOutput",7,"r"),new ProgramOutputOrigin("DebugOutput",7,"r"));
	}
}
