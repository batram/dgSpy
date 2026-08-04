using System;
using System.Threading;
using System.Threading.Tasks;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		Task<T> OnDebuggerAsync<T>(Func<T> callback,CancellationToken cancellationToken=default) {
			var tcs=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			manager.Dispatcher.BeginInvoke(()=>{ try { tcs.TrySetResult(callback()); } catch(Exception ex) { tcs.TrySetException(ex); } });
			if (!cancellationToken.CanBeCanceled) return tcs.Task;
			var registration=cancellationToken.Register(()=>tcs.TrySetCanceled(cancellationToken));
			return tcs.Task.ContinueWith(t=>{ registration.Dispose(); return t.GetAwaiter().GetResult(); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
		}
	}
}
