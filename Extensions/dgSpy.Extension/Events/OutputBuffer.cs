using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	sealed class OutputBuffer {
		readonly int capacity; readonly List<OutputMessage> values=new List<OutputMessage>(); readonly object sync=new object();
		TaskCompletionSource<bool> changed=NewSignal(); long lastId;
		public OutputBuffer(int capacity=256) { this.capacity=capacity; }
		public void Add(string category,string message,int? processId=null,string? runtimeId=null) { TaskCompletionSource<bool> wake; lock(sync) { values.Add(new OutputMessage { OutputId=++lastId,TimestampUtc=DateTime.UtcNow,Category=category,Message=message,ProcessId=processId,RuntimeId=runtimeId }); if(values.Count>capacity) values.RemoveAt(0); wake=changed; changed=NewSignal(); } wake.TrySetResult(true); }
		public void Reset() { TaskCompletionSource<bool> wake; lock(sync) { values.Clear(); lastId=0; wake=changed; changed=NewSignal(); } wake.TrySetResult(true); }
		public OutputSnapshot Snapshot(long after) { lock(sync) { var oldest=values.Count==0 ? lastId+1 : values[0].OutputId; var cursor=Math.Max(0,oldest-1); return new OutputSnapshot { Messages=values.Where(v=>v.OutputId>after).ToArray(),Oldest=oldest,Cursor=cursor,Last=lastId,Truncated=after<cursor }; } }
		public Task WaitAsync(long observed,CancellationToken token) { Task signal; lock(sync) { if(lastId!=observed) return Task.CompletedTask; signal=changed.Task; } return token.CanBeCanceled ? WaitCore(signal,token) : signal; }
		static async Task WaitCore(Task signal,CancellationToken token) { var cancel=Task.Delay(Timeout.Infinite,token); if(await Task.WhenAny(signal,cancel).ConfigureAwait(false)==cancel) token.ThrowIfCancellationRequested(); await signal.ConfigureAwait(false); }
		static TaskCompletionSource<bool> NewSignal()=>new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
	}
	sealed class OutputSnapshot { public OutputMessage[] Messages=Array.Empty<OutputMessage>(); public long Oldest; public long Cursor; public long Last; public bool Truncated; }
}
