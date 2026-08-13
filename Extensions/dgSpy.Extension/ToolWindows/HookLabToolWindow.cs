using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Automation;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dgSpy.Extension.ToolWindows {
	static class HookLabUiBridge {
		static readonly object gate=new object();
		static readonly List<HookLabHookRow> hooks=new List<HookLabHookRow>();
		static readonly List<HookLabEventRow> events=new List<HookLabEventRow>();
		static Func<Task<object>>? initialize; static Func<MethodDef,string,string,int,int,Task<object>>? install; static Func<MethodDef,string,string>? generate; static Func<MethodDef,string,string,string,int,Task<object>>? installSource; static Func<string,int,string,Task>? remove; static Func<string,int,Task>? removeAll; static Action? show; static MethodDef? selectedMethod; static string status="Initialize HookLab, then select a method and use Add Hook...";
		public static event Action? Changed;
		public static void Bind(Func<Task<object>> initializeHookLab,Func<MethodDef,string,string,int,int,Task<object>> installHook,Func<MethodDef,string,string> generateSource,Func<MethodDef,string,string,string,int,Task<object>> installCustomSource,Func<string,int,string,Task> removeHook,Func<string,int,Task> removeAllHooks) { lock(gate) { initialize=initializeHookLab; install=installHook; generate=generateSource; installSource=installCustomSource; remove=removeHook; removeAll=removeAllHooks; } }
		public static void BindWindow(Action showWindow) { lock(gate) show=showWindow; }
		public static void PublishHook(string session,int process,string id,string kind,string method,string patch,int revision,bool compiled,string? source,MethodDef? methodDefinition) { Action? open; lock(gate) { hooks.RemoveAll(value=>value.Session==session && value.Process==process && value.Id==id); hooks.Add(new HookLabHookRow(session,process,id,kind,method,patch,revision,compiled,source,methodDefinition)); open=show; } Changed?.Invoke(); open?.Invoke(); }
		public static HookLabHookRow? FindCompiled(string id,string method) { lock(gate) return hooks.LastOrDefault(value=>value.Id==id && value.Method==method && value.Compiled); }
		public static MethodDef? SelectedMethod { get { lock(gate) return selectedMethod; } }
		public static void ShowMethod(MethodDef method) { Action? open; lock(gate) { selectedMethod=method; open=show; } Changed?.Invoke(); open?.Invoke(); }
		public static void RemoveHook(string session,int process,string id) { lock(gate) hooks.RemoveAll(value=>value.Session==session && value.Process==process && value.Id==id); Changed?.Invoke(); }
		public static void Clear() { lock(gate) { hooks.Clear(); events.Clear(); } Changed?.Invoke(); }
		public static void PublishEvent(long cursor,string patch,string payload,long dropped) { lock(gate) { events.Add(new HookLabEventRow(cursor,patch,payload,dropped)); if(events.Count>1024) events.RemoveRange(0,events.Count-1024); } Changed?.Invoke(); }
		public static (HookLabHookRow[] Hooks,HookLabEventRow[] Events,string Status) Snapshot() { lock(gate) return (hooks.ToArray(),events.ToArray(),status); }
		public static async Task InitializeAsync() { Func<Task<object>>? action; lock(gate) action=initialize; if(action is null) throw new InvalidOperationException("Attach to a target before initializing HookLab."); ReportStatus("Initializing HookLab..."); await action(); ReportStatus("HookLab is ready. Select a method and use Add Hook..."); }
		public static async Task InstallAsync(MethodDef method,string id,string kind,int maximumEventsPerSecond,int maximumStringLength) { Func<MethodDef,string,string,int,int,Task<object>>? action; lock(gate) action=install; if(action is null) throw new InvalidOperationException("HookLab service is unavailable."); ReportStatus(HookLabInstallPresentation.Waiting(method.DeclaringType.FullName+"."+method.Name)); await action(method,id,kind,maximumEventsPerSecond,maximumStringLength); ReportStatus("Installed "+id+"."); }
		public static string GenerateSource(MethodDef method,string template) { Func<MethodDef,string,string>? action; lock(gate) action=generate; if(action is null) throw new InvalidOperationException("HookLab service is unavailable."); return action(method,template); }
		public static async Task InstallSourceAsync(MethodDef method,string id,string kind,string source,int revision) { Func<MethodDef,string,string,string,int,Task<object>>? action; lock(gate) action=installSource; if(action is null) throw new InvalidOperationException("HookLab service is unavailable."); ReportStatus("Compiling "+id+" revision "+revision+"..."); await action(method,id,kind,source,revision); ReportStatus("Installed "+id+" revision "+revision+"."); }
		public static void ReportStatus(string value) { lock(gate) status=value; Changed?.Invoke(); }
		public static Task RemoveAsync(HookLabHookRow row) { Func<string,int,string,Task>? action; lock(gate) action=remove; return action is null ? Task.FromException(new InvalidOperationException("HookLab has not been started by this dgSpy host.")) : action(row.Session,row.Process,row.Id); }
		public static Task RemoveAllAsync(HookLabHookRow row) { Func<string,int,Task>? action; lock(gate) action=removeAll; return action is null ? Task.FromException(new InvalidOperationException("HookLab has not been started by this dgSpy host.")) : action(row.Session,row.Process); }
	}

	[ExportAutoLoaded]
	sealed class HookLabToolWindowLoader : IAutoLoaded {
		[ImportingConstructor]
		HookLabToolWindowLoader(IDsToolWindowService windows) => HookLabUiBridge.BindWindow(()=>Application.Current.Dispatcher.BeginInvoke(new Action(()=>windows.Show(HookLabToolWindowContent.GuidValue))));
	}

	sealed class HookLabHookRow { public HookLabHookRow(string session,int process,string id,string kind,string method,string patch,int revision,bool compiled,string? source,MethodDef? methodDefinition) { Session=session; Process=process; Id=id; Kind=kind; Method=method; Patch=patch; Revision=revision; Compiled=compiled; Source=source; MethodDefinition=methodDefinition; } public string Session { get; } public int Process { get; } public string Id { get; } public string Kind { get; } public string Method { get; } public string Patch { get; } public int Revision { get; } public bool Compiled { get; } public string? Source { get; } public MethodDef? MethodDefinition { get; } public string SourceKind=>Compiled?"C#":"Observer"; }
	sealed class HookLabEventRow { public HookLabEventRow(long cursor,string patch,string payload,long dropped) { Cursor=cursor; Patch=patch; Payload=payload; Dropped=dropped; } public long Cursor { get; } public string Patch { get; } public string Payload { get; } public long Dropped { get; } }

	sealed class HookLabToolWindowVM : INotifyPropertyChanged {
		public ObservableCollection<HookLabHookRow> Hooks { get; }=new ObservableCollection<HookLabHookRow>();
		public ObservableCollection<HookLabEventRow> Events { get; }=new ObservableCollection<HookLabEventRow>();
		HookLabHookRow? selected; string status="Initialize HookLab, then select a method and use Add Hook..."; bool busy;
		public HookLabHookRow? Selected { get=>selected; set { selected=value; Changed(); Changed(nameof(CanEdit)); } }
		public string Status { get=>status; private set { status=value; Changed(); } }
		public bool CanOperate=>!busy;
		public bool CanEdit=>!busy && selected?.Compiled==true && selected.Source is not null && selected.MethodDefinition is not null;
		public event PropertyChangedEventHandler? PropertyChanged;
		public HookLabToolWindowVM() { HookLabUiBridge.Changed+=Refresh; Refresh(); }
		public void Refresh() { if(Application.Current?.Dispatcher.CheckAccess()==false) { Application.Current.Dispatcher.BeginInvoke(new Action(Refresh)); return; } var snapshot=HookLabUiBridge.Snapshot(); var selectedKey=Selected is null ? null : Selected.Session+"\n"+Selected.Process+"\n"+Selected.Id; Hooks.Clear(); foreach(var value in snapshot.Hooks) Hooks.Add(value); if(selectedKey is not null) Selected=Hooks.FirstOrDefault(value=>value.Session+"\n"+value.Process+"\n"+value.Id==selectedKey); var selectedMethod=HookLabUiBridge.SelectedMethod; if(selectedMethod is not null) Selected=Hooks.FirstOrDefault(value=>value.MethodDefinition is MethodDef definition && definition.Module.Mvid==selectedMethod.Module.Mvid && definition.MDToken.Raw==selectedMethod.MDToken.Raw); Events.Clear(); foreach(var value in snapshot.Events) Events.Add(value); Status=snapshot.Status; }
		public async void Initialize() { await Run(HookLabUiBridge.InitializeAsync); }
		public void EditSelected() { var value=Selected; if(!CanEdit || value?.MethodDefinition is null) return; try { new CustomHookEditorDialog(value).ShowDialog(); } catch(Exception ex) { Status=ex.Message; MessageBox.Show(Application.Current?.MainWindow,ex.Message,"Edit Custom Hook",MessageBoxButton.OK,MessageBoxImage.Warning); } }
		public async void RemoveSelected() { var value=Selected; if(value is null) return; await Run(()=>HookLabUiBridge.RemoveAsync(value)); }
		public async void RemoveAll() { var value=Selected ?? Hooks.FirstOrDefault(); if(value is null) return; await Run(()=>HookLabUiBridge.RemoveAllAsync(value)); }
		async Task Run(Func<Task> action) {
			if(busy) return;
			try { busy=true; Changed(nameof(CanOperate)); Changed(nameof(CanEdit)); Status="Working..."; await action(); Status="Completed."; }
			catch(Exception ex) { Status=ex.Message; }
			finally { busy=false; Changed(nameof(CanOperate)); Changed(nameof(CanEdit)); }
		}
		void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
	}

	sealed class HookLabControl : Grid {
		public HookLabControl(HookLabToolWindowVM vm) {
			DataContext=vm; RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height=new GridLength(2,GridUnitType.Star) });
			var toolbar=new StackPanel { Orientation=Orientation.Horizontal }; var initialize=new Button { Content="Initialize HookLab",Margin=new Thickness(3),Padding=new Thickness(8,1,8,1) }; AutomationProperties.SetName(initialize,"Initialize HookLab"); initialize.SetBinding(IsEnabledProperty,"CanOperate"); initialize.Click+=(s,e)=>vm.Initialize(); var edit=new Button { Content="Edit",Margin=new Thickness(3),Padding=new Thickness(8,1,8,1) }; AutomationProperties.SetName(edit,"Edit selected custom HookLab hook"); edit.SetBinding(IsEnabledProperty,"CanEdit"); edit.Click+=(s,e)=>vm.EditSelected(); var remove=new Button { Content="Remove",Margin=new Thickness(3),Padding=new Thickness(8,1,8,1) }; AutomationProperties.SetName(remove,"Remove selected HookLab hook"); remove.SetBinding(IsEnabledProperty,"CanOperate"); remove.Click+=(s,e)=>vm.RemoveSelected(); var all=new Button { Content="Remove All",Margin=new Thickness(3),Padding=new Thickness(8,1,8,1) }; AutomationProperties.SetName(all,"Remove all HookLab hooks"); all.SetBinding(IsEnabledProperty,"CanOperate"); all.Click+=(s,e)=>vm.RemoveAll(); var status=new TextBlock { Margin=new Thickness(8,5,3,3),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,MaxWidth=900 }; AutomationProperties.SetName(status,"HookLab status"); status.SetBinding(TextBlock.TextProperty,"Status"); status.SetResourceReference(TextBlock.ForegroundProperty,"GridViewListViewForeground"); toolbar.Children.Add(initialize); toolbar.Children.Add(edit); toolbar.Children.Add(remove); toolbar.Children.Add(all); toolbar.Children.Add(status); Children.Add(toolbar);
			var hookList=new ListView { Margin=new Thickness(3) }; AutomationProperties.SetName(hookList,"Installed hooks"); hookList.SetBinding(ItemsControl.ItemsSourceProperty,"Hooks"); hookList.SetBinding(ListView.SelectedItemProperty,"Selected"); hookList.View=new GridView { Columns={ new GridViewColumn { Header="Hook",DisplayMemberBinding=new System.Windows.Data.Binding("Id"),Width=200 },new GridViewColumn { Header="Kind",DisplayMemberBinding=new System.Windows.Data.Binding("Kind"),Width=80 },new GridViewColumn { Header="Source",DisplayMemberBinding=new System.Windows.Data.Binding("SourceKind"),Width=70 },new GridViewColumn { Header="Revision",DisplayMemberBinding=new System.Windows.Data.Binding("Revision"),Width=65 },new GridViewColumn { Header="Method",DisplayMemberBinding=new System.Windows.Data.Binding("Method"),Width=320 } } }; SetRow(hookList,1); Children.Add(hookList);
			var eventList=new ListView { Margin=new Thickness(3) }; AutomationProperties.SetName(eventList,"Hook events"); eventList.SetBinding(ItemsControl.ItemsSourceProperty,"Events"); eventList.View=new GridView { Columns={ new GridViewColumn { Header="#",DisplayMemberBinding=new System.Windows.Data.Binding("Cursor"),Width=55 },new GridViewColumn { Header="Patch",DisplayMemberBinding=new System.Windows.Data.Binding("Patch"),Width=260 },new GridViewColumn { Header="Payload",DisplayMemberBinding=new System.Windows.Data.Binding("Payload"),Width=500 },new GridViewColumn { Header="Dropped",DisplayMemberBinding=new System.Windows.Data.Binding("Dropped"),Width=70 } } }; SetRow(eventList,2); Children.Add(eventList);
		}
	}

	sealed class AddHookDialog : WindowBase {
		readonly TextBox id=new TextBox { MinWidth=300,Margin=new Thickness(6) };
		readonly ComboBox kind=new ComboBox { Margin=new Thickness(6),ItemsSource=new[]{"Prefix","Postfix","Finalizer"},SelectedIndex=0 };
		readonly TextBox rate=new TextBox { Text="100",Margin=new Thickness(6) };
		readonly TextBox stringLength=new TextBox { Text="1024",Margin=new Thickness(6) };
		public string HookId=>id.Text.Trim(); public string Kind=>(string)kind.SelectedItem; public int MaximumEventsPerSecond=>Int32.Parse(rate.Text); public int MaximumStringLength=>Int32.Parse(stringLength.Text);
		public AddHookDialog(MethodDef method) {
			SetResourceReference(StyleProperty,"DialogWindowStyle");
			Title="Add Hook"; SizeToContent=SizeToContent.WidthAndHeight; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner; Owner=Application.Current?.MainWindow;
			id.Text=(method.DeclaringType.FullName+"."+method.Name).Replace('`','_');
			AutomationProperties.SetName(id,"Hook ID"); AutomationProperties.SetName(kind,"Hook kind"); AutomationProperties.SetName(rate,"Maximum events per second"); AutomationProperties.SetName(stringLength,"Maximum string length");
			var panel=new StackPanel { Margin=new Thickness(10) }; panel.Children.Add(new TextBlock { Text=method.FullName,Margin=new Thickness(6) }); panel.Children.Add(new TextBlock { Text=HookLabInstallPresentation.DialogExplanation,Margin=new Thickness(6),MaxWidth=620,TextWrapping=TextWrapping.Wrap }); panel.Children.Add(new TextBlock { Text="Hook ID",Margin=new Thickness(6,8,6,0) }); panel.Children.Add(id); panel.Children.Add(new TextBlock { Text="Kind",Margin=new Thickness(6,8,6,0) }); panel.Children.Add(kind); panel.Children.Add(new TextBlock { Text="Maximum events per second",Margin=new Thickness(6,8,6,0) }); panel.Children.Add(rate); panel.Children.Add(new TextBlock { Text="Maximum captured string length",Margin=new Thickness(6,8,6,0) }); panel.Children.Add(stringLength);
			var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; var add=new Button { Content="Install",IsDefault=true,MinWidth=80,Margin=new Thickness(6) }; add.Click+=(s,e)=>{ if(HookId.Length==0 || !Positive(rate.Text) || !Positive(stringLength.Text)) { MessageBox.Show(this,"Hook ID is required and capture bounds must be positive whole numbers.","Add Hook",MessageBoxButton.OK,MessageBoxImage.Warning); return; } DialogResult=true; }; var cancel=new Button { Content="Cancel",IsCancel=true,MinWidth=80,Margin=new Thickness(6) }; buttons.Children.Add(add); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content=panel;
		}
		static bool Positive(string text)=>Int32.TryParse(text,out var value) && value>0;
	}

	sealed class CustomHookEditorDialog : WindowBase {
		readonly MethodDef method;
		readonly HookLabEditorState state;
		readonly TextBox id=new TextBox { Margin=new Thickness(6) };
		readonly ComboBox template=new ComboBox { Margin=new Thickness(6),ItemsSource=new[]{"Prefix","Postfix","PrefixPostfix"} };
		readonly TextBox source=new TextBox { Margin=new Thickness(6),AcceptsReturn=true,AcceptsTab=true,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,TextWrapping=TextWrapping.NoWrap,FontFamily=new System.Windows.Media.FontFamily("Consolas"),FontSize=13 };
		readonly TextBox diagnostics=new TextBox { Margin=new Thickness(6),Height=95,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
		readonly Button install=new Button { Content="Compile & Install",IsDefault=true,MinWidth=125,Margin=new Thickness(6) };
		public CustomHookEditorDialog(MethodDef selected) : this(selected,null) { }
		public CustomHookEditorDialog(HookLabHookRow existing) : this(existing.MethodDefinition ?? throw new ArgumentException("The selected hook has no loaded method.",nameof(existing)),existing) { }
		CustomHookEditorDialog(MethodDef selected,HookLabHookRow? selectedHook) {
			SetResourceReference(StyleProperty,"DialogWindowStyle");
			method=selected; var defaultId=DefaultId(selected); var methodName=selected.DeclaringType.FullName+"."+selected.Name; var existing=selectedHook ?? HookLabUiBridge.FindCompiled(defaultId,methodName);
			state=existing is null ? new HookLabEditorState(defaultId,value=>HookLabUiBridge.GenerateSource(selected,value)) : new HookLabEditorState(existing.Id,value=>HookLabUiBridge.GenerateSource(selected,value),existing.Revision+1,existing.Source,existing.Kind);
			Title=existing is null?"Create Custom Hook":"Edit Custom Hook"; Width=920; Height=720; MinWidth=680; MinHeight=520; WindowStartupLocation=WindowStartupLocation.CenterOwner; Owner=Application.Current?.MainWindow;
			id.Text=state.HookId; id.IsReadOnly=existing is not null; template.SelectedItem=state.Template; source.Text=state.Source;
			AutomationProperties.SetName(id,"Custom hook ID"); AutomationProperties.SetName(template,"Custom hook template"); AutomationProperties.SetName(source,"Custom hook C# source"); AutomationProperties.SetName(diagnostics,"Custom hook compiler diagnostics"); AutomationProperties.SetName(install,"Compile and install custom hook");
			var layout=new Grid { Margin=new Thickness(10) }; layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
			var heading=new StackPanel(); heading.Children.Add(new TextBlock { Text=selected.FullName,Margin=new Thickness(6),TextWrapping=TextWrapping.Wrap }); heading.Children.Add(new TextBlock { Text="Edit the generated complete C# source. HookLab compiles before changing the target; a compiler failure leaves any working revision installed.",Margin=new Thickness(6),TextWrapping=TextWrapping.Wrap }); Grid.SetRow(heading,0); layout.Children.Add(heading);
			var fields=new Grid(); fields.ColumnDefinitions.Add(new ColumnDefinition()); fields.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(230) }); var idPanel=new StackPanel(); idPanel.Children.Add(new TextBlock { Text="Hook ID",Margin=new Thickness(6,3,6,0) }); idPanel.Children.Add(id); var templatePanel=new StackPanel(); templatePanel.Children.Add(new TextBlock { Text="Template",Margin=new Thickness(6,3,6,0) }); templatePanel.Children.Add(template); Grid.SetColumn(templatePanel,1); fields.Children.Add(idPanel); fields.Children.Add(templatePanel); Grid.SetRow(fields,1); layout.Children.Add(fields);
			var sourcePanel=new DockPanel(); var sourceLabel=new TextBlock { Text="C# source",Margin=new Thickness(6,3,6,0) }; DockPanel.SetDock(sourceLabel,Dock.Top); sourcePanel.Children.Add(sourceLabel); sourcePanel.Children.Add(source); Grid.SetRow(sourcePanel,2); layout.Children.Add(sourcePanel);
			var diagnosticsPanel=new DockPanel(); var diagnosticsLabel=new TextBlock { Text="Compiler diagnostics",Margin=new Thickness(6,3,6,0) }; DockPanel.SetDock(diagnosticsLabel,Dock.Top); diagnosticsPanel.Children.Add(diagnosticsLabel); diagnosticsPanel.Children.Add(diagnostics); Grid.SetRow(diagnosticsPanel,3); layout.Children.Add(diagnosticsPanel);
			var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; install.Click+=Install; var cancel=new Button { Content="Cancel",IsCancel=true,MinWidth=80,Margin=new Thickness(6) }; buttons.Children.Add(install); buttons.Children.Add(cancel); Grid.SetRow(buttons,4); layout.Children.Add(buttons); Content=layout;
			template.SelectionChanged+=(s,e)=>{ if(template.SelectedItem is string value && value!=state.Template) { state.SelectTemplate(value); source.Text=state.Source; diagnostics.Text=state.Diagnostics; } };
		}
		async void Install(object? sender,RoutedEventArgs e) {
			state.HookId=id.Text.Trim(); state.Source=source.Text;
			if(!state.TryBegin(out var error)) { diagnostics.Text=error; return; }
			install.IsEnabled=false; id.IsEnabled=false; template.IsEnabled=false; diagnostics.Text=state.Diagnostics;
			try {
				await HookLabUiBridge.InstallSourceAsync(method,state.HookId,state.Kind,state.Source,state.Revision);
				state.Complete(); diagnostics.Text=state.Diagnostics; DialogResult=true;
			}
			catch(Exception ex) {
				state.Fail(ex.Message); diagnostics.Text=state.Diagnostics; HookLabUiBridge.ReportStatus(ex.Message);
				install.IsEnabled=true; id.IsEnabled=true; template.IsEnabled=true;
			}
		}
		static string DefaultId(MethodDef method)=>(method.DeclaringType.FullName+"."+method.Name+".custom").Replace('`','_');
	}

	static class HookLabMethodContext {
		public static MethodDef? Method(IMenuItemContext context) {
			if(context.CreatorObject.Guid!=new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID)) return null;
			var reference=context.Find<TextReference>()?.Reference;
			return reference as MethodDef ?? (reference as MethodStatementReference)?.Method ?? (reference as InstructionReference)?.Method;
		}
	}

	[ExportMenuItem(Header="Add Hook...",Group="2100,67C441E9-66EC-4DA0-9693-BEE4E7E57C30",Order=0)]
	sealed class AddHookFromCodeCommand : MenuItemBase {
		readonly IDsToolWindowService windows; [ImportingConstructor] AddHookFromCodeCommand(IDsToolWindowService windows)=>this.windows=windows;
		public override bool IsVisible(IMenuItemContext context)=>HookLabMethodContext.Method(context) is not null;
		public override bool IsEnabled(IMenuItemContext context)=>HookLabMethodContext.Method(context)?.HasBody==true;
		public override async void Execute(IMenuItemContext context) {
			var method=HookLabMethodContext.Method(context); if(method is null) return; var dialog=new AddHookDialog(method); if(dialog.ShowDialog()!=true) return; windows.Show(HookLabToolWindowContent.GuidValue);
			try { await HookLabUiBridge.InstallAsync(method,dialog.HookId,dialog.Kind,dialog.MaximumEventsPerSecond,dialog.MaximumStringLength); } catch(Exception ex) { HookLabUiBridge.ReportStatus(ex.Message); }
		}
	}

	[ExportMenuItem(Header="Create Custom Hook...",Group="2100,67C441E9-66EC-4DA0-9693-BEE4E7E57C30",Order=10)]
	sealed class CreateCustomHookFromCodeCommand : MenuItemBase {
		readonly IDsToolWindowService windows; [ImportingConstructor] CreateCustomHookFromCodeCommand(IDsToolWindowService windows)=>this.windows=windows;
		public override bool IsVisible(IMenuItemContext context)=>HookLabMethodContext.Method(context) is not null;
		public override bool IsEnabled(IMenuItemContext context)=>HookLabMethodContext.Method(context)?.HasBody==true;
		public override void Execute(IMenuItemContext context) {
			var method=HookLabMethodContext.Method(context); if(method is null) return;
			try { var dialog=new CustomHookEditorDialog(method); if(dialog.ShowDialog()==true) windows.Show(HookLabToolWindowContent.GuidValue); }
			catch(Exception ex) { HookLabUiBridge.ReportStatus(ex.Message); MessageBox.Show(Application.Current?.MainWindow,ex.Message,"Create Custom Hook",MessageBoxButton.OK,MessageBoxImage.Warning); }
		}
	}

	[ExportMenuItem(OwnerGuid=MenuConstants.APP_MENU_VIEW_GUID,Header="_HookLab",Group=MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS,Order=3010)]
	sealed class ShowHookLabCommand : MenuItemBase { readonly IDsToolWindowService service; [ImportingConstructor] ShowHookLabCommand(IDsToolWindowService service)=>this.service=service; public override void Execute(IMenuItemContext context)=>service.Show(HookLabToolWindowContent.GuidValue); }
	[Export(typeof(IToolWindowContentProvider))]
	sealed class HookLabToolWindowProvider : IToolWindowContentProvider { HookLabToolWindowContent? content; public IEnumerable<ToolWindowContentInfo> ContentInfos { get { yield return new ToolWindowContentInfo(HookLabToolWindowContent.GuidValue,AppToolWindowLocation.DefaultHorizontal,10,false); } } public ToolWindowContent? GetOrCreate(Guid guid)=>guid==HookLabToolWindowContent.GuidValue ? content ??= new HookLabToolWindowContent() : null; }
	sealed class HookLabToolWindowContent : ToolWindowContent { public static readonly Guid GuidValue=new Guid("807B2567-601C-4491-A28F-4DAA0AF89591"); readonly HookLabControl control=new HookLabControl(new HookLabToolWindowVM()); public override Guid Guid=>GuidValue; public override string Title=>"HookLab"; public override object UIObject=>control; public override IInputElement FocusedElement=>control; public override FrameworkElement ZoomElement=>control; }
}
