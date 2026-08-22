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
		Assert.Contains("selected?.Owned==true&&selected.Compiled&&selected.Sourceisnotnull&&selected.MethodDefinitionisnotnull",source,StringComparison.Ordinal);
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
		Assert.Contains("readonlyICodeEditorsource",source,StringComparison.Ordinal);
		Assert.Contains("HookLabToolWindowLoader(IDsToolWindowServicewindows,IDocumentTabServicedocumentTabs,ICodeEditorProvidercodeEditorProvider)",source,StringComparison.Ordinal);
		Assert.Contains("ContentTypeString=ContentTypes.CSharpRoslyn",source,StringComparison.Ordinal);
		Assert.Contains("source.TextViewHost.HostControl",source,StringComparison.Ordinal);
		Assert.Contains("AutomationProperties.SetName(source.TextView.VisualElement,\"CustomhookC#source\")",source,StringComparison.Ordinal);
		Assert.Contains("source.TextBuffer.CurrentSnapshot.GetText()",source,StringComparison.Ordinal);
		Assert.Contains("source.TextBuffer.Replace(newSpan(0,snapshot.Length),value)",source,StringComparison.Ordinal);
		Assert.Contains("protectedoverridevoidOnClosed(EventArgse){source.Dispose();base.OnClosed(e);}",source,StringComparison.Ordinal);
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

	/// <summary>The property is unchanged and the anchors moved, because the target environment contract's subslice 7 replaced the
	/// inline eligibility check with the whole precondition contract. What used to be asserted - an
	/// unsupported target is refused before anything is staged, resumed or injected - is now the weaker
	/// half of what holds: <em>every</em> precondition is evaluated and refused before the first mutation,
	/// not only architecture and runtime.
	///
	/// <para>Staging is anchored on the point the planned area is materialized, since planning it moved
	/// into the gathering that happens before the contract is evaluated. Planning creates nothing;
	/// materializing is the first filesystem change and therefore the line that matters.</para></summary>
	[Fact]
	public void HookLab_refuses_a_target_that_fails_its_contract_before_resuming_or_injecting() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		var initialize=source.IndexOf("publicasyncTask<object>InitializeAsync",StringComparison.Ordinal);
		var contract=source.IndexOf("HookLabPreconditions.Evaluate(gathered.Facts)",initialize,StringComparison.Ordinal);
		var refusal=source.IndexOf("thrownewRpcException(refused.RefusalCode",contract,StringComparison.Ordinal);
		var materialize=source.IndexOf("gathered.Exchange!.Materialize()",initialize,StringComparison.Ordinal);
		var resume=source.IndexOf("awaitResumeAsync(host,source,token)",initialize,StringComparison.Ordinal);
		var inject=source.IndexOf("awaitInitializeAutonomouslyAsync",initialize,StringComparison.Ordinal);
		Assert.True(initialize>=0 && contract>initialize && refusal>contract,"Initialization must evaluate the precondition contract and refuse on its first failure.");
		Assert.True(materialize>refusal && resume>refusal && inject>refusal,"A target that fails a precondition must be refused before staging, resume, or injection.");
	}

	/// <summary>The preflight and the acting path must be one computation. A second implementation drifts
	/// from the path it is supposed to gate, which is a worse defect than the one it prevents - so both
	/// entry points gather through the same method and evaluate the same function.</summary>
	[Fact]
	public void The_readiness_probe_and_initialization_evaluate_one_contract() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Equal(2,Occurrences(source,"awaitGatherFactsAsync(host,source,token)"));
		Assert.Equal(2,Occurrences(source,"HookLabPreconditions.Evaluate("));
		// The probe's boundary, asserted rather than documented: the words that mutate a target or this
		// machine must not appear between the probe's signature and its return.
		var probe=source.IndexOf("publicasyncTask<object>ReadinessAsync",StringComparison.Ordinal);
		var probeEnd=source.IndexOf("staticstringWire(",probe,StringComparison.Ordinal);
		Assert.True(probe>=0 && probeEnd>probe,"The readiness probe was not found where its boundary can be checked.");
		var body=source.Substring(probe,probeEnd-probe);
		foreach(var mutation in new[]{"Materialize(","ResumeAsync(","InitializeAutonomouslyAsync(","PauseAsync(","CreateSecret(","File.WriteAllText(","Directory.CreateDirectory("})
			Assert.DoesNotContain(mutation,body,StringComparison.Ordinal);
	}

	static int Occurrences(string source,string value) {
		var count=0;
		for(var index=source.IndexOf(value,StringComparison.Ordinal);index>=0;index=source.IndexOf(value,index+value.Length,StringComparison.Ordinal)) count++;
		return count;
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
	public void Package_export_uses_only_retained_compiled_hooks_and_never_enables_the_profile() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("if(definition.Sourceisnull)thrownewRpcException(\"hook_not_exportable\"",source,StringComparison.Ordinal);
		Assert.Contains("process.StartTime.ToUniversalTime().Ticks!=definition.ProcessCreationTicks",source,StringComparison.Ordinal);
		Assert.Contains("String.Equals(Path.GetFullPath(image),Path.GetFullPath(definition.ImagePath),StringComparison.OrdinalIgnoreCase)",source,StringComparison.Ordinal);
		Assert.Contains("profile_enabled=false",source,StringComparison.Ordinal);
		Assert.Contains("HookPackageExporter.Export(target,newHookPackageExportRequest",source,StringComparison.Ordinal);
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
		Assert.Contains("newImageReference(typeof(HookLabGlyphMarker).Assembly,\"HookLabHookStacked\")",source,StringComparison.Ordinal);
		Assert.Contains("newImageReference(typeof(HookLabGlyphMarker).Assembly,\"HookLabHookStackedDisabled\")",source,StringComparison.Ordinal);
		Assert.Contains("rows.Length>1?(anyEnabled?stackedHookImage:stackedDisabledHookImage):(anyEnabled?hookImage:disabledHookImage)",source,StringComparison.Ordinal);
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
		Assert.Equal(2,Count(source,"Icon=\"HookLabHook\""));
		Assert.DoesNotContain("Header=\"AddHook...\"",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Custom_hook_editor_and_service_accept_all_compiled_patch_kinds() {
		var ui=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		var service=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("ItemsSource=new[]{\"Prefix\",\"Postfix\",\"PrefixPostfix\",\"Finalizer\",\"Transpiler\"}",ui,StringComparison.Ordinal);
		Assert.Contains("value.Kind!=\"Prefix\"&&value.Kind!=\"Postfix\"&&value.Kind!=\"Finalizer\"&&value.Kind!=\"Transpiler\"",service,StringComparison.Ordinal);
		Assert.Contains("Transpilerrequirescompiledcustomsource",service,StringComparison.Ordinal);
	}

	[Fact]
	public void HookLab_toolbar_actions_follow_initialized_selection_and_collection_state() {
		var source=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		Assert.Contains("publicboolCanInitialize=>!busy",source,StringComparison.Ordinal);
		Assert.Contains("publicstringInitializeLabel=>initializing?\"Initializing...\":initialized?\"InitializeCurrentTarget\":\"InitializeHookLab\"",source,StringComparison.Ordinal);
		Assert.Contains("initialize.SetBinding(IsEnabledProperty,\"CanInitialize\")",source,StringComparison.Ordinal);
		Assert.Contains("publicboolCanRemove=>!busy&&selected?.Owned==true",source,StringComparison.Ordinal);
		Assert.Contains("remove.SetBinding(IsEnabledProperty,\"CanRemove\")",source,StringComparison.Ordinal);
		Assert.Contains("publicboolCanRemoveAll=>!busy&&Hooks.Any(value=>value.Owned)",source,StringComparison.Ordinal);
		Assert.Contains("all.SetBinding(IsEnabledProperty,\"CanRemoveAll\")",source,StringComparison.Ordinal);
		Assert.Contains("lock(gate){initialized=false;hooks.Clear();events.Clear();}",source,StringComparison.Ordinal);
	}

	[Fact]
	public void Gui_initialization_resumes_a_stopped_resident_and_resolves_multiple_targets_explicitly() {
		var service=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("varprocess=awaitOnDebuggerAsync(SelectHookLabUiProcess,token)",service,StringComparison.Ordinal);
		Assert.Contains("matches.Length>1&&manager.CurrentProcess.Currentis",service,StringComparison.Ordinal);
		Assert.Contains("if(manager.CurrentProcess.Currentis",service,StringComparison.Ordinal);
		Assert.Contains("Multipleprocessesareattached.Selectamethodortheintendedprocess",service,StringComparison.Ordinal);
		var adoption=service.IndexOf("if(discovered.Length==1)",StringComparison.Ordinal);
		var resume=service.IndexOf("if(!wasRunning)awaitResumeAsync(host,source,token)",adoption,StringComparison.Ordinal);
		var health=service.IndexOf("store.VerifyHealthAndRefresh",adoption,StringComparison.Ordinal);
		Assert.True(adoption>=0&&resume>adoption&&health>resume,"A stopped adopted resident must run before its pipe health check.");
		var ui=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		Assert.Contains("Header=\"Process\",DisplayMemberBinding=newSystem.Windows.Data.Binding(\"Process\")",ui,StringComparison.Ordinal);
	}

	[Fact]
	public void Foreign_resident_hooks_are_visible_but_every_management_surface_is_read_only() {
		var ui=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabToolWindow.cs")));
		var service=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		var glyph=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","ToolWindows","HookLabGlyphMarker.cs")));
		Assert.Contains("HookLabUiBridge.PublishResidentHooks",service,StringComparison.Ordinal);
		Assert.Contains("value.Controller==HookOwnership.DgSpyController",service,StringComparison.Ordinal);
		Assert.Contains("Header=\"Owner\"",ui,StringComparison.Ordinal);
		Assert.Contains("Enabled(read-only)",ui,StringComparison.Ordinal);
		Assert.Contains("publicboolCanEdit=>!busy&&selected?.Owned==true",ui,StringComparison.Ordinal);
		Assert.Contains("publicboolCanToggle=>!busy&&selected?.Owned==true",ui,StringComparison.Ordinal);
		Assert.Contains("publicboolCanRemove=>!busy&&selected?.Owned==true",ui,StringComparison.Ordinal);
		Assert.Contains("removeHook.IsEnabled=vm.CanRemove",ui,StringComparison.Ordinal);
		Assert.Contains("value.Owned&&value.Id==id",ui,StringComparison.Ordinal);
		Assert.Contains("publicboolCanManage=>Single?.Owned==true",glyph,StringComparison.Ordinal);
		Assert.Contains("if(row?.Owned!=true){Show();return;}",glyph,StringComparison.Ordinal);
	}

	[Fact]
	public void Hook_requests_use_reflection_nested_type_names_for_old_and_new_residents() {
		var service=Normalize(File.ReadAllText(RepoFile("Extensions","dgSpy.Extension","Debugger","HookLab","RpcHost.HookLab.cs")));
		Assert.Contains("varresidentType=MethodIdentityText.Canonicalize(DeclaringType)",service,StringComparison.Ordinal);
		Assert.Contains("varresidentSignature=MethodIdentityText.Canonicalize(Signature)",service,StringComparison.Ordinal);
		Assert.Contains("[\"hook_type\"]=residentType",service,StringComparison.Ordinal);
		Assert.Contains("[\"hook_declaring_type\"]=residentType",service,StringComparison.Ordinal);
		Assert.Contains("[\"hook_method_signature\"]=residentSignature",service,StringComparison.Ordinal);
	}

	static string Normalize(string value)=>String.Concat(value.Where(character=>!Char.IsWhiteSpace(character)));
	static int Count(string value,string needle) { int count=0,index=0; while((index=value.IndexOf(needle,index,StringComparison.Ordinal))>=0) { count++; index+=needle.Length; } return count; }

	static string RepoFile(params string[] parts) {
		var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		return Path.Combine(new[] { root }.Concat(parts).ToArray());
	}
}
