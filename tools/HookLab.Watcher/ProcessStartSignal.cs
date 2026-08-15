using System.Management;

namespace HookLab.Watcher;

internal interface IProcessStartSignal : IDisposable {
	string Mode { get; }
	Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation);
}

internal sealed class WmiProcessStartSignal : IProcessStartSignal {
	readonly ManagementEventWatcher watcher; readonly SemaphoreSlim signal=new(0,1);
	WmiProcessStartSignal() { watcher=new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace")); watcher.EventArrived+=OnEvent; watcher.Start(); }
	void OnEvent(object sender,EventArrivedEventArgs eventArguments) { try { signal.Release(); } catch(SemaphoreFullException) { } catch(ObjectDisposedException) { } }
	public string Mode=>"subscription+polling";
	public Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation)=>signal.WaitAsync(TimeSpan.FromMilliseconds(pollingMilliseconds),cancellation);
	public void Dispose() { watcher.EventArrived-=OnEvent; try { watcher.Stop(); } catch(ManagementException) { } catch(InvalidOperationException) { } watcher.Dispose(); signal.Dispose(); }
	public static IProcessStartSignal Create() { try { return new WmiProcessStartSignal(); } catch(Exception ex) when(ex is ManagementException or UnauthorizedAccessException or PlatformNotSupportedException) { Console.Error.WriteLine("HookLab process-start subscription unavailable; retaining polling: "+ex.Message.Replace('\r',' ').Replace('\n',' ')); return new PollingProcessStartSignal(); } }
}

internal sealed class PollingProcessStartSignal : IProcessStartSignal {
	public string Mode=>"polling";
	public async Task<bool> WaitAsync(int pollingMilliseconds,CancellationToken cancellation) { await Task.Delay(pollingMilliseconds,cancellation); return false; }
	public void Dispose() { }
}
