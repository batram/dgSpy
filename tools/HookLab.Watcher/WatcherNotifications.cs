namespace HookLab.Watcher;

internal sealed record WatchNotification(string? ProfileId,string DefinitionId,int ProcessId,string Status,string? Message);
internal interface IWatchNotificationSink { void Publish(WatchNotification notification); }
internal sealed class ConsoleWatchNotificationSink : IWatchNotificationSink {
	public void Publish(WatchNotification notification)=>Console.Error.WriteLine("HookLab notification "+notification.Status+" "+notification.DefinitionId+" for PID "+notification.ProcessId+(notification.Message is null?"":": "+notification.Message.Replace('\r',' ').Replace('\n',' ')));
}
