using System.Management;

namespace HookLab.Watcher;

internal interface IProcessStartSignal : IDisposable {
	string Mode { get; }
	Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation);
	/// <summary>Narrows wake-ups to starts of these image names; an empty set wakes on every start.</summary>
	void Observe(IEnumerable<string> fileNames) { }
}

internal sealed class WmiProcessStartSignal : IProcessStartSignal {
	readonly object gate=new(); readonly SemaphoreSlim signal=new(0,1); ManagementEventWatcher watcher; string[] watched=Array.Empty<string>(); bool disposed;
	WmiProcessStartSignal() { watcher=Subscribe(watched); }
	// The event is only a wake-up and its payload is never touched: ManagementBaseObject is a 64-byte
	// managed shell over several MB of native memory that the GC never feels, so undisposed events piled
	// up to ~1.3 GB per gen0 cycle (measured 2026-09-18). Reading even one property re-leaks the same
	// amount despite Dispose, so the image-name filter lives in the WQL WHERE clause instead.
	void OnEvent(object sender,EventArrivedEventArgs eventArguments) {
		try { eventArguments.NewEvent.Dispose(); } catch(Exception) { }
		try { signal.Release(); } catch(SemaphoreFullException) { } catch(ObjectDisposedException) { }
	}
	public string Mode=>"subscription+polling";
	public Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation)=>signal.WaitAsync(TimeSpan.FromMilliseconds(pollingMilliseconds),cancellation);
	public void Observe(IEnumerable<string> fileNames) {
		var next=fileNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value=>value,StringComparer.OrdinalIgnoreCase).ToArray();
		lock(gate) {
			if(disposed||next.SequenceEqual(watched,StringComparer.OrdinalIgnoreCase)) return;
			ManagementEventWatcher replacement;
			try { replacement=Subscribe(next); } catch(Exception ex) when(ex is ManagementException or UnauthorizedAccessException) { Console.Error.WriteLine("HookLab process-start subscription could not narrow to the watched images; keeping the current subscription: "+ex.Message.Replace('\r',' ').Replace('\n',' ')); return; }
			Unsubscribe(watcher); watcher=replacement; watched=next;
		}
	}
	internal static string Query(IReadOnlyList<string> fileNames)=>"SELECT * FROM Win32_ProcessStartTrace"+(fileNames.Count==0?"":" WHERE "+String.Join(" OR ",fileNames.Select(name=>"ProcessName = '"+name.Replace("\\","\\\\").Replace("'","\\'")+"'")));
	ManagementEventWatcher Subscribe(string[] fileNames) {
		var value=new ManagementEventWatcher(new WqlEventQuery(Query(fileNames))); value.EventArrived+=OnEvent;
		try { value.Start(); } catch { value.EventArrived-=OnEvent; value.Dispose(); throw; }
		return value;
	}
	void Unsubscribe(ManagementEventWatcher value) { value.EventArrived-=OnEvent; try { value.Stop(); } catch(ManagementException) { } catch(InvalidOperationException) { } value.Dispose(); }
	public void Dispose() { lock(gate) { if(disposed) return; disposed=true; Unsubscribe(watcher); signal.Dispose(); } }
	public static IProcessStartSignal Create() { try { return new WmiProcessStartSignal(); } catch(Exception ex) when(ex is ManagementException or UnauthorizedAccessException or PlatformNotSupportedException) { Console.Error.WriteLine("HookLab process-start subscription unavailable; retaining polling: "+ex.Message.Replace('\r',' ').Replace('\n',' ')); return new PollingProcessStartSignal(); } }
}

internal sealed class PollingProcessStartSignal : IProcessStartSignal {
	public string Mode=>"polling";
	public async Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation) { await Task.Delay(pollingMilliseconds,cancellation); return false; }
	public void Dispose() { }
}
