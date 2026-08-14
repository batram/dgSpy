using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class HookLabUiBoundaryTests {
	[Fact]
	public void Gui_operations_adopt_the_active_debugger_and_initialize_before_installing() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("InitializeHookLabFromUiAsync(CancellationTokentoken){awaitEnsureUiSessionAsync(token)",source,StringComparison.Ordinal);
		Assert.Contains("InstallSelectedHookAsync(MethodDefselected,stringhookId,stringkind,intmaximumEventsPerSecond,intmaximumStringLength,string?source,intrevision,CancellationTokentoken){awaitEnsureUiSessionAsync(token)",source,StringComparison.Ordinal);
		Assert.Contains("if(sourceisnotnull){request[\"source\"]=source;request[\"revision\"]=revision;}",source,StringComparison.Ordinal);
		Assert.Contains("(method,template)=>host.GenerateSelectedHookTemplate(method,template)",source,StringComparison.Ordinal);
		Assert.Contains("(method,id,kind,source,revision)=>host.InstallSelectedHookAsync(method,id,kind,100,1024,source,revision,CancellationToken.None)",source,StringComparison.Ordinal);
		var install=source.IndexOf("asyncTask<object>InstallSelectedHookAsync(",StringComparison.Ordinal);
		var initialize=source.IndexOf("awaithookLab.InitializeAsync(",install,StringComparison.Ordinal);
		var pipeInstall=source.IndexOf("returnawaithookLab.InstallAsync(",install,StringComparison.Ordinal);
		Assert.True(initialize>install && pipeInstall>initialize,"GUI hook installation must initialize HookLab before using its resident transport.");
	}

	[Fact]
	public void Compiled_hook_rows_can_reopen_their_exact_source_at_the_next_revision() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		Assert.Contains("Content=\"Edit\"",source,StringComparison.Ordinal);
		Assert.Contains("edit.SetBinding(IsEnabledProperty,\"CanEdit\")",source,StringComparison.Ordinal);
		Assert.Contains("selected?.Compiled==true&&selected.Sourceisnotnull&&selected.MethodDefinitionisnotnull",source,StringComparison.Ordinal);
		Assert.Contains("newCustomHookEditorDialog(value).ShowDialog()",source,StringComparison.Ordinal);
		Assert.Contains("existing.Revision+1,existing.Source,existing.Kind",source,StringComparison.Ordinal);
		Assert.Contains("id.IsReadOnly=existingisnotnull",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"EditCustomC#Hook...\"",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"ShowMethodinSource\"",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"RemoveHook\"",source,StringComparison.Ordinal);
		Assert.Contains("removeHook.Click+=(s,e)=>vm.RemoveSelected()",source,StringComparison.Ordinal);
		Assert.Contains("hookList.PreviewMouseRightButtonDown",source,StringComparison.Ordinal);
		Assert.Contains("item.IsSelected=true",source,StringComparison.Ordinal);
		Assert.Contains("hookList.MouseDoubleClick+=(s,e)=>vm.ActivateSelected()",source,StringComparison.Ordinal);
		Assert.Contains("TextWrapping=TextWrapping.Wrap",source,StringComparison.Ordinal);
		Assert.Contains("VerticalContentAlignment=VerticalAlignment.Top",source,StringComparison.Ordinal);
		Assert.Contains("VerticalAlignment=VerticalAlignment.Stretch",source,StringComparison.Ordinal);
		Assert.Contains("HorizontalAlignment=HorizontalAlignment.Stretch",source,StringComparison.Ordinal);
		Assert.Contains("MinHeight=200",source,StringComparison.Ordinal);
		Assert.Contains("toggleHook.SetBinding(Button.ContentProperty,\"ToggleLabel\")",source,StringComparison.Ordinal);
		Assert.Contains("toggleHook.SetBinding(IsEnabledProperty,\"CanToggle\")",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"State\"",source,StringComparison.Ordinal);
		Assert.Contains("record.Enabled?\"enabled\":\"disabled\"",Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs"))),StringComparison.Ordinal);
	}

	[Fact]
	public void Mcp_install_initializes_the_target_when_needed_and_serializes_initialization() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("if(!initialized)awaitInitializeAsync(host,source,token)",source,StringComparison.Ordinal);
		Assert.Contains("awaitinitialization.WaitAsync(token)",source,StringComparison.Ordinal);
		Assert.Contains("finally{initialization.Release();}",source,StringComparison.Ordinal);
		Assert.Contains("report.ContainsKey(\"pipe_name\")",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Compiled_hook_api_separates_create_update_and_publishes_editable_state() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("CreateAsync(RpcHosthost,RpcRequestsource,CancellationTokentoken)=>InstallAsync(host,source,\"create\",token)",source,StringComparison.Ordinal);
		Assert.Contains("UpdateAsync(RpcHosthost,RpcRequestsource,CancellationTokentoken)=>InstallAsync(host,source,\"update\",token)",source,StringComparison.Ordinal);
		Assert.Contains("if(lifecycle==\"create\")thrownewRpcException(\"hook_exists\"",source,StringComparison.Ordinal);
		Assert.Contains("elseif(lifecycle==\"update\")thrownewRpcException(\"hook_not_found\"",source,StringComparison.Ordinal);
		Assert.Contains("source=record.Definition.Source,diagnostics=Array.Empty<string>()",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Synthetic_gui_session_is_released_when_debugging_ends() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Rpc","RpcHost.cs")));
		Assert.Contains("if(sessionKind==\"ui\"){sessionId=null;attachedProgramId=null;sessionKind=null",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Installed_hooks_publish_native_method_glyphs() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabGlyphMarker.cs")));
		Assert.Contains("[ExportDocumentViewerListener]",source,StringComparison.Ordinal);
		Assert.Contains("DotNetTokenGlyphTextMarkerLocationInfo",source,StringComparison.Ordinal);
		Assert.Contains("newImageReference(typeof(HookLabGlyphMarker).Assembly,\"HookLabHook\")",source,StringComparison.Ordinal);
		Assert.Contains("newImageReference(typeof(HookLabGlyphMarker).Assembly,\"HookLabHookDisabled\")",source,StringComparison.Ordinal);
		Assert.Contains("rows.Any(row=>row.Enabled)?hookImage:disabledHookImage",source,StringComparison.Ordinal);
		Assert.Contains("HookLabUiBridge.Changed+=Changed",source,StringComparison.Ordinal);
		Assert.Contains("HookLabUiBridge.Snapshot().Hooks",source,StringComparison.Ordinal);
		Assert.Contains("OnMouseLeftButtonUp",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"ShowinHookLab\"",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"EditCustomC#Hook...\"",source,StringComparison.Ordinal);
		Assert.Contains("HookLabUiBridge.SingleCompiled(Method)",source,StringComparison.Ordinal);
		Assert.Contains("summary.ToggleOrShow()",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"ToggleHook\"",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"RemoveHook\"",source,StringComparison.Ordinal);
		Assert.Contains("GetHeader(IMenuItemContextcontext)=>context.Find<HookLabGlyphSummary>()?.ToggleHeader",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Method_menu_distinguishes_observation_from_custom_csharp_hooks() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		Assert.Contains("Header=\"AddObservationHook...\"",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"CreateCustomC#Hook...\"",source,StringComparison.Ordinal);
		Assert.DoesNotContain("Header=\"AddHook...\"",source,StringComparison.Ordinal);
	}

	static string Normalize(string value)=>String.Concat(value.Where(character=>!Char.IsWhiteSpace(character)));

	static string RepoFile(params string[] parts) {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		return Path.Combine(new[] { root }.Concat(parts).ToArray());
	}
}
