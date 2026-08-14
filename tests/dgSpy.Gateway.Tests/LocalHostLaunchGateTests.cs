using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Gateway;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class LocalHostLaunchGateTests {
	[Fact]
	public async Task Concurrent_clients_enter_the_host_launch_workflow_one_at_a_time() {
		var gate=new LocalHostLaunchGate();
		var active=0;
		var maximum=0;
		var calls=Enumerable.Range(0,32).Select(_=>gate.RunAsync(async ()=>{
			var entered=Interlocked.Increment(ref active);
			int observed;
			do { observed=Volatile.Read(ref maximum); }
			while(entered>observed && Interlocked.CompareExchange(ref maximum,entered,observed)!=observed);
			await Task.Delay(2);
			Interlocked.Decrement(ref active);
			return true;
		},CancellationToken.None)).ToArray();
		Assert.All(await Task.WhenAll(calls),Assert.True);
		Assert.Equal(1,maximum);
	}
}
