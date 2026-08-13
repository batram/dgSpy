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
				var summary=new HookLabGlyphSummary(method,rows.Length,rows.Count(row=>row.Compiled),rows.Count(row=>!row.Compiled));
				markers[group.Key]=markerService.AddMarker(location,hookImage,null,null,null,2520,summary,handler,null);
			}
		}
	}

	sealed class HookLabGlyphSummary {
		public HookLabGlyphSummary(dnlib.DotNet.MethodDef method,int total,int compiled,int observers) { Method=method; Total=total; Compiled=compiled; Observers=observers; }
		public dnlib.DotNet.MethodDef Method { get; } public int Total { get; } public int Compiled { get; } public int Observers { get; }
		public void Show()=>HookLabUiBridge.ShowMethod(Method);
		public override string ToString()=>"HookLab: "+Total+" hook"+(Total==1?"":"s")+" ("+Compiled+" compiled, "+Observers+" observer)";
	}

	sealed class HookLabGlyphHandler : IGlyphTextMarkerHandler {
		public IGlyphTextMarkerHandlerMouseProcessor MouseProcessor { get; }=new HookLabGlyphMouseProcessor();
		public IEnumerable<GuidObject> GetContextMenuObjects(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker,Point marginRelativePoint) { if(marker.Tag is HookLabGlyphSummary summary) yield return new GuidObject(new Guid("75D9AF6C-104B-47D8-AE04-02964E285D16"),summary); }
		public GlyphTextMarkerToolTip? GetToolTipContent(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker)=>marker.Tag is HookLabGlyphSummary summary ? new GlyphTextMarkerToolTip(summary.ToString()) : null;
		public FrameworkElement? GetPopupContent(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker)=>null;
	}

	sealed class HookLabGlyphMouseProcessor : GlyphTextMarkerHandlerMouseProcessorBase {
		public override void OnMouseLeftButtonUp(IGlyphTextMarkerHandlerContext context,IGlyphTextMarker marker,MouseButtonEventArgs e) { if(marker.Tag is HookLabGlyphSummary summary) { summary.Show(); e.Handled=true; } }
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.GLYPHMARGIN_GUID,Header="Show in HookLab",Group="0,16473E86-060B-4F19-849E-BBBD63390093",Order=0)]
	sealed class ShowHookLabGlyphCommand : MenuItemBase {
		public override bool IsVisible(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>() is not null;
		public override void Execute(IMenuItemContext context)=>context.Find<HookLabGlyphSummary>()?.Show();
	}
}
