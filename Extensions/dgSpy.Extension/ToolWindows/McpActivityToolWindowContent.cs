using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dgSpy.Extension.ToolWindows {
	/// <summary>Binds Ctrl+Alt+M to showing the activity window.</summary>
	[ExportAutoLoaded]
	sealed class McpActivityToolWindowLoader : IAutoLoaded {
		public static readonly RoutedCommand OpenMcpActivityWindow=new RoutedCommand("OpenMcpActivityWindow",typeof(McpActivityToolWindowLoader));

		[ImportingConstructor]
		McpActivityToolWindowLoader(IWpfCommandService wpfCommandService,IDsToolWindowService toolWindowService) {
			var commands=wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			commands.Add(OpenMcpActivityWindow,new RelayCommand(_ => toolWindowService.Show(McpActivityToolWindowContent.THE_GUID)));
			commands.Add(OpenMcpActivityWindow,ModifierKeys.Control|ModifierKeys.Alt,Key.M);
		}
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.APP_MENU_VIEW_GUID,Header="dgSpy MCP _Activity",InputGestureText="Ctrl+Alt+M",Group=MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS,Order=3000)]
	sealed class ShowMcpActivityCommand : MenuItemCommand {
		ShowMcpActivityCommand() : base(McpActivityToolWindowLoader.OpenMcpActivityWindow) { }
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class McpActivityToolWindowContentProvider : IToolWindowContentProvider {
		McpActivityToolWindowContent Content => content ??= new McpActivityToolWindowContent();
		McpActivityToolWindowContent? content;

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(McpActivityToolWindowContent.THE_GUID,McpActivityToolWindowContent.DEFAULT_LOCATION,0,false); }
		}

		// Called repeatedly, so the instance has to be cached.
		public ToolWindowContent? GetOrCreate(Guid guid) => guid==McpActivityToolWindowContent.THE_GUID ? Content : null;
	}

	/// <summary>
	/// Shows every MCP-triggered operation the extension dispatched, with its parameters, its result
	/// or error, and how long it took. Sits alongside Output and Locals in the bottom tool window
	/// group, which is where a human looks to see what just happened.
	/// </summary>
	sealed class McpActivityToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID=new Guid("4B2C7E19-6D3A-4F02-9C5E-1A7F0B8D5C31");
		public const AppToolWindowLocation DEFAULT_LOCATION=AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => "dgSpy MCP Activity";
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.SearchTextBox;
		public override FrameworkElement? ZoomElement => control;

		readonly McpActivityControl control;
		readonly McpActivityVM vm;

		public McpActivityToolWindowContent() {
			control=new McpActivityControl();
			vm=new McpActivityVM(McpActivityLog.Instance,control.Dispatcher);
			control.SetViewModel(vm);
		}

		public override void OnVisibilityChanged(ToolWindowContentVisibilityEvent visEvent) {
			// Only Added/Removed matter. The log keeps recording while the window is merely hidden
			// behind another tab, and a re-add replays the whole buffer, so nothing is lost either way.
			switch(visEvent) {
			case ToolWindowContentVisibilityEvent.Added: vm.IsEnabled=true; break;
			case ToolWindowContentVisibilityEvent.Removed: vm.IsEnabled=false; break;
			}
		}
	}
}
