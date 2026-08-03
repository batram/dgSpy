using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Steppers;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		// One stepper at a time, held only while a step is in flight. dnSpy's stepper is a live debugger
		// object with a Close() that cancels the step, so an abandoned one keeps the engine in a stepping
		// state that the next Pause has to fight. Every terminal path calls CloseStepper.
		DbgStepper? activeStepper;

		static DbgStepKind ParseStepKind(string kind) => kind switch {
			StepKinds.Into => DbgStepKind.StepInto,
			StepKinds.Over => DbgStepKind.StepOver,
			StepKinds.Out => DbgStepKind.StepOut,
			_ => throw new RpcException("invalid_argument",$"Unknown step kind '{kind}'. Valid: {string.Join(", ",StepKinds.All)}."),
		};

		// The step is issued here and *completes* on the event stream, exactly like a breakpoint hit: the
		// same stopped event, with stop_reason "step". This call waits briefly so a short step can be
		// reported synchronously, but a step that runs long is not an error — it is why cursor_event_id is
		// returned. Reading the cursor before issuing the step matters for the same reason it does for a
		// breakpoint on a hot method: a step over a fast call completes before a follow-up state read
		// returns, and a cursor taken afterwards has already missed the stop.
		async Task<StepResult> StepAsync(RpcRequest req,string kindName,CancellationToken cancellationToken) {
			CheckSession(req);
			var kind=ParseStepKind(kindName);
			var requestedThreadId=(string?)req.Arguments["thread_id"];
			long cursor; lock(sync) cursor=events.LastEventId;

			var completion=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var threadId=await OnDebuggerAsync(()=>{
				CheckVersion(req);
				if (manager.IsRunning!=false) throw new RpcException("not_paused","Pause the session before stepping.");
				var all=manager.Processes.SelectMany(p=>p.Threads).ToArray();
				DbgThread? thread;
				if (!string.IsNullOrEmpty(requestedThreadId)) {
					thread=all.FirstOrDefault(t=>ThreadId(t)==requestedThreadId)
						?? throw new RpcException("thread_not_found",$"Thread {requestedThreadId} is not active. Refresh list_threads and use an exact thread_id.");
				}
				// Stepping the wrong thread is worse than refusing to guess: it resumes the target and
				// stops somewhere unrelated. Fall back only to the thread that carried the stop.
				else thread=manager.CurrentThread.Current
					?? throw new RpcException("invalid_arguments","No thread is current, so there is nothing to step. Pass thread_id from list_threads.");

				CloseStepper();
				var stepper=thread.CreateStepper();
				if (!stepper.CanStep) { stepper.Close(); throw new RpcException("cannot_step","The engine will not step this thread in its current state."); }
				// autoClose disposes the stepper when the step completes, which is the common path. The
				// tracked reference exists for the paths where it never completes: detach, terminate, or
				// the target exiting mid-step.
				stepper.StepComplete += (_,e) => { lock(sync) activeStepper=null; completion.TrySetResult(e.Error); };
				activeStepper=stepper;
				stepper.Step(kind,autoClose:true);
				return ThreadId(thread);
			},cancellationToken).ConfigureAwait(false);

			// A step that has not landed within the grace period is reported as in flight, not as a
			// failure. The caller waits on the event stream from cursor_event_id, which is where the stop
			// was always going to arrive.
			var landed=await Task.WhenAny(completion.Task,Task.Delay(TimeSpan.FromSeconds(10),cancellationToken)).ConfigureAwait(false);
			var completed=landed==completion.Task;
			var error=completed ? await completion.Task.ConfigureAwait(false) : null;
			return new StepResult {
				SessionId=sessionId ?? "",ThreadId=threadId,StepKind=kindName,CursorEventId=cursor,
				Completed=completed,Error=string.IsNullOrEmpty(error) ? null : error,StateVersion=stateVersion,
			};
		}

		/// <summary>Closes an in-flight stepper. Safe to call when there is none, and safe to call after
		/// autoClose already disposed one, because the StepComplete handler clears the reference first.
		/// Call it on the debugger dispatcher: closing a stepper cancels a step, which is engine work.</summary>
		void CloseStepper() {
			DbgStepper? stepper; lock(sync) { stepper=activeStepper; activeStepper=null; }
			if (stepper is null) return;
			try { stepper.Close(); } catch (Exception) { /* the session is going away; a stepper that is already closed is the desired state */ }
		}
	}
}
