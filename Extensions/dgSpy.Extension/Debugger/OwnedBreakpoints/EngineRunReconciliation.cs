using System;
using System.Threading;
using System.Threading.Tasks;

namespace dgSpy.Extension.Debugger.OwnedBreakpoints {
	static class EngineRunReconciliation {
		internal static async Task RunAsync(bool supported,Action<Action<string?>> begin,CancellationToken cancellationToken) {
			if (!supported) throw new NotSupportedException("Engine state reconciliation is unavailable for this debugger engine.");
			if (begin is null) throw new ArgumentNullException(nameof(begin));
			cancellationToken.ThrowIfCancellationRequested();
			var completed=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			begin(error=>completed.TrySetResult(error));
			await WaitWithCancellationAsync(completed.Task,cancellationToken).ConfigureAwait(false);
			var error=await completed.Task.ConfigureAwait(false);
			if (error is not null) throw new InvalidOperationException(error);
		}

		static async Task WaitWithCancellationAsync(Task task,CancellationToken cancellationToken) {
			if (!cancellationToken.CanBeCanceled) { await task.ConfigureAwait(false); return; }
			var canceled=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(()=>canceled.TrySetCanceled()))
				await await Task.WhenAny(task,canceled.Task).ConfigureAwait(false);
		}
	}
}
