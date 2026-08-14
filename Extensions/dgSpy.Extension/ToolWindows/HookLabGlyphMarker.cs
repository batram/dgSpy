using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text.Editor;

namespace dgSpy.Extension.ToolWindows {
	[ExportDocumentViewerListener]
	sealed class HookLabGlyphMarker : IDocumentViewerListener {
		static readonly ImageReference hookImage=new ImageReference(typeof(HookLabGlyphMarker).Assembly,"HookLabHook");
		static readonly ImageReference disabledHookImage=new ImageReference(typeof(HookLabGlyphMarker).Assembly,"HookLabHookDisabled");
		static readonly ImageReference stackedHookImage=new ImageReference(typeof(HookLabGlyphMarker).Assembly,"HookLabHookStacked");
		static readonly ImageReference stackedDisabledHookImage=new ImageReference(typeof(HookLabGlyphMarker).Assembly,"HookLabHookStackedDisabled");
		readonly IGlyphTextMarkerService markerService;
		readonly IModuleIdProvider moduleIdProvider;
		readonly Dictionary<string,IGlyphTextMarker> markers=new Dictionary<string,IGlyphTextMarker>(StringComparer.Ordinal);
		readonly HookLabGlyphHandler handler=new HookLabGlyphHandler();
		[ImportingConstructor]
		HookLabGlyphMarker(IGlyphTextMarkerService markerService,IModuleIdProvider moduleIdProvider) {
			this.markerService=markerService; this.moduleIdProvider=moduleIdProvider;
			HookLabUiBridge.Changed+=Changed; Changed();
		}
		void IDocumentViewerListener.OnEvent(DocumentViewerEventArgs e) { }
		void Changed() {
			if(Application.Current?.Dispatcher.CheckAccess()==false) { Application.Current.Dispatcher.BeginInvoke(new Action(Changed)); return; }
			foreach(var marker in markers.Values) markerService.Remove(marker); markers.Clear();
			var groups=HookLabUiBridge.Snapshot().Hooks.Where(row=>row.MethodDefinition is not null).GroupBy(row=>row.MethodDefinition!.Module.Mvid?.ToString("D")+":"+row.MethodDefinition.MDToken.Raw);
			foreach(var group in groups) {
				var rows=group.ToArray(); var method=rows[0].MethodDefinition!;
				var location=new DotNetTokenGlyphTextMarkerLocationInfo(moduleIdProvider.Create(method.Module),(int)method.MDToken.Raw);
				var summary=new HookLabGlyphSummary(method,rows);
				var anyEnabled=rows.Any(row=>row.Enabled);
				var image=rows.Length>1 ? (anyEnabled?stackedHookImage:stackedDisabledHookImage) : (anyEnabled?hookImage:disabledHookImage);
				markers[group.Key]=markerService.AddMarker(location,image,null,null,null,2520,summary,handler,null);
			}
		}
	}

	sealed class HookLabGlyphSummary {
		readonly HookLabHookRow[] rows;
		public HookLabGlyphSummary(dnlib.DotNet.MethodDef method,HookLabHookRow[] rows) { Method=method; this.rows=rows; }
		public dnlib.DotNet.MethodDef Method { get; } public int Total=>rows.Length; public int Compiled=>rows.Count(row=>row.Compiled); public int Observers=>rows.Count(row=>!row.Compiled);
		HookLabHookRow? Single=>rows.Length==1?rows[0]:null;
		public void Show()=>HookLabUiBridge.ShowMethod(Method);
		public bool CanEdit=>HookLabUiBridge.SingleCompiled(Method) is not null;
		public bool CanManage=>Single is not null;
		public string ToggleHeader=>Single?.Enabled==false?"Enable Hook":"Disable Hook";
		public void Edit() { var row=HookLabUiBridge.SingleCompiled(Method); if(row is not null) new CustomHookEditorDialog(row).ShowDialog(); else Show(); }
		public async void ToggleOrShow() { var row=Single; if(row is null) { Show(); return; } try { await HookLabUiBridge.ToggleAsync(row); } catch(Exception ex) { HookLabUiBridge.ReportStatus(ex.Message); } }
		public async void Remove() { var row=Single; if(row is null) { Show(); return; } try { await HookLabUiBridge.RemoveAsync(row); } catch(Exception ex) { HookLabUiBridge.ReportStatus(ex.Message); } }
		public override string ToString()=>Single is HookLabHookRow row ? "HookLab: "+row.Id+" - "+row.State+" (click to "+(row.Enabled?"disable":"enable")+")" : "HookLab: "+Total+" hooks ("+Compiled+" compiled, "+Observers+" observer) - open HookLab to manage";
	}

	sealed class HookLabGlyphHandler : IGlyphTextMarkerHandler {
		public IGlyphTextMarkerHandlerMouseProcessor MouseProcessor { get; }=new HookLabGlyphMouseProcessor();
		public IEnumerable<GuidObject> GetContextMenuObjects(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker,Point marginRelativePoint) { if(marker.Tag is HookLabGlyphSummary summary) yield return new GuidObject(new Guid("75D9AF6C-104B-47D8-AE04-02964E285D16"),summary); }
		public GlyphTextMarkerToolTip? GetToolTipContent(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker)=>marker.Tag is HookLabGlyphSummary summary ? new GlyphTextMarkerToolTip(summary.ToString()) : null;
		public FrameworkElement? GetPopupContent(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker)=>null;
	}

	sealed class HookLabGlyphMouseProcessor : GlyphTextMarkerHandlerMouseProcessorBase {
		public override void OnMouseLeftButtonUp(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker,MouseButtonEventArgs e) { if(marker.Tag is HookLabGlyphSummary summary) { summary.ToggleOrShow(); e.Handled=true; } }
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.GLYPHMARGIN_GUID,Header="Show in HookLab",Group="0,16473E86-060B-4F19-849E-BBBD63390093",Order=0)]
	sealed class ShowHookLabGlyphCommand : MenuItemBase {
		public override bool IsVisible(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>() is not null;
		public override void Execute(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.Show();
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.GLYPHMARGIN_GUID,Header="Edit Custom C# Hook...",Group="0,16473E86-060B-4F19-849E-BBBD63390093",Order=10)]
	sealed class EditHookLabGlyphCommand : MenuItemBase {
		public override bool IsVisible(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.CanEdit==true;
		public override void Execute(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.Edit();
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.GLYPHMARGIN_GUID,Header="Toggle Hook",Group="0,16473E86-060B-4F19-849E-BBBD63390093",Order=20)]
	sealed class ToggleHookLabGlyphCommand : MenuItemBase {
		public override bool IsVisible(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.CanManage==true;
		public override string? GetHeader(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.ToggleHeader;
		public override void Execute(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.ToggleOrShow();
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.GLYPHMARGIN_GUID,Header="Remove Hook",Group="10,16473E86-060B-4F19-849E-BBBD63390093",Order=0)]
	sealed class RemoveHookLabGlyphCommand : MenuItemBase {
		public override bool IsVisible(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.CanManage==true;
		public override void Execute(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.Remove();
	}
}
