using System;
using dgSpy.Extension.ToolWindows;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookLabEditorStateTests {
	[Fact]
	public void StartsWithPairedTemplateAndRevisionOne() {
		var state=State();
		Assert.Equal("PrefixPostfix",state.Template);
		Assert.Equal("source:PrefixPostfix",state.Source);
		Assert.Equal("Prefix",state.Kind);
		Assert.Equal(1,state.Revision);
	}

	[Fact]
	public void ExplicitTemplateSelectionRegeneratesSourceAndPhase() {
		var state=State(); state.Source="user edit";
		state.SelectTemplate("Postfix");
		Assert.Equal("source:Postfix",state.Source);
		Assert.Equal("Postfix",state.Kind);
	}

	[Theory]
	[InlineData("Prefix","Prefix")]
	[InlineData("PrefixPostfix","Prefix")]
	[InlineData("Postfix","Postfix")]
	[InlineData("Finalizer","Finalizer")]
	[InlineData("Transpiler","Transpiler")]
	public void TemplateMapsToTheSubmittedCompiledKind(string template,string kind) {
		var state=State(); state.SelectTemplate(template);
		Assert.Equal(kind,state.Kind);
	}

	[Fact]
	public void ValidationKeepsEditableState() {
		var state=State(); state.HookId=" "; state.Source="user edit";
		Assert.False(state.TryBegin(out var error));
		Assert.Equal("Hook ID is required.",error);
		Assert.Equal("user edit",state.Source);
		Assert.False(state.Busy);
	}

	[Fact]
	public void CompilerFailureKeepsSourceAndRevisionForRetry() {
		var state=State(); state.Source="broken source";
		Assert.True(state.TryBegin(out _));
		state.Fail("CS1002: ; expected");
		Assert.Equal("broken source",state.Source);
		Assert.Equal(1,state.Revision);
		Assert.Equal("CS1002: ; expected",state.Diagnostics);
		Assert.False(state.Busy);
		Assert.True(state.TryBegin(out _));
	}

	[Fact]
	public void ExistingHookStartsAtNextRevisionWithItsInstalledSource() {
		var state=new HookLabEditorState("fixture.custom",template=>"generated:"+template,4,"installed source","Postfix");
		Assert.Equal(4,state.Revision);
		Assert.Equal("installed source",state.Source);
		Assert.Equal("Postfix",state.Template);
		Assert.True(state.TryBegin(out _));
		Assert.Equal("Compiling revision 4...",state.Diagnostics);
	}

	static HookLabEditorState State()=>new HookLabEditorState("fixture.custom",template=>"source:"+template);
}
