using System;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.Debugger;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		/// <summary>Recovery text for a dispatcher that will never run another callback. Shared with
		/// get_host_info so the diagnostic and the error say the same thing.</summary>
		internal const string DispatcherUnavailableRecovery="The dnSpy debugger thread has shut down; this host cannot run debugger operations again. Any session it reports is dead. Close this host and call launch_local_host with replace=true, then attach again.";

		Task<T> OnDebuggerAsync<T>(Func<T> callback,CancellationToken cancellationToken=default) {
			var tcs=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			Action work=()=>{ try { tcs.TrySetResult(callback()); } catch(Exception ex) { tcs.TrySetException(ex); } };
			// A shut-down dispatcher silently discards the callback, so the completion source is never
			// completed and the caller waits out its whole deadline for every single operation. That is
			// how a host stayed registered while being unusable: reads served from cached state answered
			// normally, everything that needed the debugger thread just stopped responding, and nothing
			// told the caller which of the two it was looking at. Enqueue and check atomically, because
			// asking first and posting afterwards loses the race that matters.
			if (manager.Dispatcher is IDbgDispatcherDiagnostics diagnostics) {
				if (!diagnostics.TryBeginInvoke(work))
					return Task.FromException<T>(new RpcException("dispatcher_unavailable",DispatcherUnavailableRecovery));
			}
			else
				manager.Dispatcher.BeginInvoke(work);
			if (!cancellationToken.CanBeCanceled) return tcs.Task;
			var registration=cancellationToken.Register(()=>tcs.TrySetCanceled(cancellationToken));
			return tcs.Task.ContinueWith(t=>{ registration.Dispose(); return t.GetAwaiter().GetResult(); },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
		}
	}
}
