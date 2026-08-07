using System.Windows.Controls;

namespace dgSpy.Extension.ToolWindows {
	sealed partial class McpActivityControl : UserControl {
		McpActivityVM? vm;

		public McpActivityControl() => InitializeComponent();

		public TextBox SearchTextBox => searchTextBox;

		/// <summary>Auto-scroll cannot live in the view model: only the view owns the ListView.</summary>
		public void SetViewModel(McpActivityVM viewModel) {
			if(vm is not null) vm.ItemAppended-=OnItemAppended;
			vm=viewModel;
			DataContext=viewModel;
			vm.ItemAppended+=OnItemAppended;
		}

		void OnItemAppended(McpActivityItemVM item) {
			if(vm is null || !vm.AutoScroll)
				return;
			// The row may be filtered out, in which case ScrollIntoView is a no-op.
			listView.ScrollIntoView(item);
		}
	}
}
