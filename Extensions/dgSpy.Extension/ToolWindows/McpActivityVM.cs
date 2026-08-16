using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using dnSpy.Contracts.MVVM;

namespace dgSpy.Extension.ToolWindows {
	/// <summary>One row in the activity grid. Immutable: entries are never edited after they land.</summary>
	sealed class McpActivityItemVM {
		readonly McpActivityEntry entry;
		public McpActivityItemVM(McpActivityEntry entry) => this.entry=entry;

		public long Sequence => entry.Sequence;
		public string Time => entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff",CultureInfo.CurrentCulture);
		public string Operation => entry.Operation;
		public string Status => entry.Status;
		public bool Failed => entry.Failed;
		public string Duration => entry.DurationMs.ToString("0.#",CultureInfo.CurrentCulture)+" ms";
		public string RequestId => entry.RequestId;

		/// <summary>Grid cells get a single line; the details pane below shows the real thing.</summary>
		public string ArgumentsSummary => Summarize(entry.Arguments);
		public string ResultSummary => entry.Failed ? entry.ErrorMessage ?? "" : Summarize(entry.Result);

		public string ArgumentsText => entry.Arguments;
		public string ResultText => entry.Failed
			? entry.ErrorDetails
			: entry.Result;

		/// <summary>Everything the filter box matches against.</summary>
		public bool Matches(string text) =>
			Operation.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0 ||
			Status.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0 ||
			entry.Arguments.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0 ||
			entry.Result.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0 ||
			(entry.ErrorMessage?.IndexOf(text,StringComparison.OrdinalIgnoreCase)>=0);

		static string Summarize(string json) {
			if(json.Length==0) return "";
			var builder=new System.Text.StringBuilder(Math.Min(json.Length,200));
			var wasSpace=false;
			foreach(var c in json) {
				var isSpace=c=='\r' || c=='\n' || c=='\t' || c==' ';
				if(isSpace) { if(!wasSpace && builder.Length>0) builder.Append(' '); wasSpace=true; continue; }
				wasSpace=false;
				builder.Append(c);
				if(builder.Length>=200) { builder.Append("..."); break; }
			}
			return builder.ToString();
		}
	}

	sealed class McpActivityVM : ViewModelBase {
		readonly McpActivityLog log;
		readonly Dispatcher dispatcher;
		readonly ObservableCollection<McpActivityItemVM> items=new ObservableCollection<McpActivityItemVM>();
		readonly ICollectionView view;
		bool subscribed;

		public McpActivityVM(McpActivityLog log,Dispatcher dispatcher) {
			this.log=log;
			this.dispatcher=dispatcher;
			view=CollectionViewSource.GetDefaultView(items);
			view.Filter=o => o is McpActivityItemVM item && (filterText.Length==0 || item.Matches(filterText));
			ClearCommand=new RelayCommand(_ => log.Clear());
		}

		public ICollectionView Items => view;
		public ICommand ClearCommand { get; }

		/// <summary>Set by the tool window as it is added and removed. Nothing is dropped while detached:
		/// the log keeps recording, and <see cref="Reload"/> replays it on the next attach.</summary>
		public bool IsEnabled {
			get => subscribed;
			set {
				if(subscribed==value) return;
				if(value) { log.Recorded+=OnRecorded; log.Cleared+=OnCleared; subscribed=true; Reload(); }
				else { log.Recorded-=OnRecorded; log.Cleared-=OnCleared; subscribed=false; items.Clear(); }
			}
		}

		public bool AutoScroll {
			get => autoScroll;
			set { if(autoScroll==value) return; autoScroll=value; OnPropertyChanged(nameof(AutoScroll)); }
		}
		bool autoScroll=true;

		public string FilterText {
			get => filterText;
			set {
				var text=value ?? string.Empty;
				if(filterText==text) return;
				filterText=text;
				OnPropertyChanged(nameof(FilterText));
				view.Refresh();
				OnPropertyChanged(nameof(CountText));
			}
		}
		string filterText=string.Empty;

		public string CountText {
			get {
				if(filterText.Length==0) return items.Count+" calls";
				var shown=0;
				foreach(var _ in view) shown++;
				return shown+" of "+items.Count+" calls";
			}
		}

		public McpActivityItemVM? SelectedItem {
			get => selectedItem;
			set {
				if(ReferenceEquals(selectedItem,value)) return;
				selectedItem=value;
				OnPropertyChanged(nameof(SelectedItem));
				OnPropertyChanged(nameof(HasSelection));
				OnPropertyChanged(nameof(SelectedArguments));
				OnPropertyChanged(nameof(SelectedResult));
				OnPropertyChanged(nameof(SelectedHeader));
			}
		}
		McpActivityItemVM? selectedItem;

		public bool HasSelection => selectedItem is not null;
		public string SelectedArguments => selectedItem?.ArgumentsText ?? string.Empty;
		public string SelectedResult => selectedItem?.ResultText ?? string.Empty;
		public string SelectedHeader => selectedItem is null
			? string.Empty
			: "#"+selectedItem.Sequence+"  "+selectedItem.Operation+"  ("+selectedItem.Status+", "+selectedItem.Duration+", request "+selectedItem.RequestId+")";

		/// <summary>Raised after a row is appended so the view can scroll to it.</summary>
		public event Action<McpActivityItemVM>? ItemAppended;

		void Reload() {
			items.Clear();
			foreach(var entry in log.Snapshot()) items.Add(new McpActivityItemVM(entry));
			OnPropertyChanged(nameof(CountText));
		}

		// Recorded fires on whichever RPC thread completed the call.
		void OnRecorded(McpActivityEntry entry) => dispatcher.BeginInvoke(new Action(() => Append(entry)));
		void OnCleared() => dispatcher.BeginInvoke(new Action(() => { items.Clear(); SelectedItem=null; OnPropertyChanged(nameof(CountText)); }));

		void Append(McpActivityEntry entry) {
			if(!subscribed) return;
			var item=new McpActivityItemVM(entry);
			items.Add(item);
			// Mirror the log's own bound so the grid cannot outgrow it.
			while(items.Count>log.Capacity) items.RemoveAt(0);
			OnPropertyChanged(nameof(CountText));
			ItemAppended?.Invoke(item);
		}
	}
}
