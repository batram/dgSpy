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
	public void Mcp_install_initializes_the_target_when_needed_and_serializes_initialization() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("if(!initialized)awaitInitializeAsync(host,source,token)",source,StringComparison.Ordinal);
		Assert.Contains("awaitinitialization.WaitAsync(token)",source,StringComparison.Ordinal);
		Assert.Contains("finally{initialization.Release();}",source,StringComparison.Ordinal);
		Assert.Contains("report.ContainsKey(\"pipe_name\")",source,StringComparison.Ordinal);
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
		Assert.Contains("HookLabUiBridge.Changed+=Changed",source,StringComparison.Ordinal);
		Assert.Contains("HookLabUiBridge.Snapshot().Hooks",source,StringComparison.Ordinal);
		Assert.Contains("OnMouseLeftButtonUp",source,StringComparison.Ordinal);
		Assert.Contains("Header=\"ShowinHookLab\"",source,StringComparison.Ordinal);
	}

	static string Normalize(string value)=>String.Concat(value.Where(character=>!Char.IsWhiteSpace(character)));

	static string RepoFile(params string[] parts) {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		return Path.Combine(new[] { root }.Concat(parts).ToArray());
	}
}
