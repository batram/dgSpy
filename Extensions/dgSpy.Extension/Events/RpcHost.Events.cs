using System;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		async Task<WaitResult> WaitAsync(RpcRequest req,CancellationToken cancellationToken) {
			CheckSession(req);
			long after=(long?)req.Arguments["after_event_id"] ?? 0;
			int timeout=Math.Min(10000,Math.Max(1,(int?)req.Arguments["timeout_ms"] ?? 5000));
			var end=DateTime.UtcNow.AddMilliseconds(timeout);
			while (DateTime.UtcNow<end) {
				lock(sync) {
					var found=events.FindAfter(after,"stopped");
					if (found.Length!=0) return new WaitResult { Events=found,OldestEventId=events.OldestEventId };
				}
				await Task.Delay(50,cancellationToken).ConfigureAwait(false);
			}
			lock(sync) return new WaitResult { TimedOut=true,OldestEventId=events.OldestEventId };
		}

		void Record(string kind) { lock(sync) events.Add(kind,++stateVersion); }
		void CheckSession(RpcRequest req) { var id=(string?)req.Arguments["session_id"]; if(sessionId is null || id!=sessionId) throw new RpcException("session_not_found","The session_id is not active."); }
		void CheckVersion(RpcRequest req) { var expected=(long?)req.Arguments["expected_state_version"]; if(expected.HasValue && expected.Value!=stateVersion) throw new RpcException("stale_state",$"Expected state {expected.Value}, current state is {stateVersion}."); }
	}
}
