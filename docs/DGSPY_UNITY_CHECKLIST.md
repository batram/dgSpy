# Mono/Unity manual checklist

The smoke script cannot cover this engine: there is no scriptable Unity target in the repo, and the
route in — `attach_endpoint` — needs a process that was started with a soft-debugger agent argument.
This is the checklist that stands in for it. Run it against UCH and record the results in
[DGSPY_STATUS.md](DGSPY_STATUS.md).

## Why `attach_endpoint` and not `attach`

`list_programs` enumerates through dnSpy's attach providers. Unity's provider finds players by
listening for the multicast beacon Unity broadcasts. A target launched with an explicit agent
argument — `--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n` —
broadcasts nothing, so no provider will ever enumerate it and `attach` has no `program_id` to take.
`attach_endpoint` connects to the address and port directly, which is the only route to it.

The two are the same kind of session either way: dnSpy's Mono engine sets `wasAttach` for a connect,
so `detach` leaves the target running exactly as it does for CorDebug.

## Preparing the target

Launch UCH with the agent argument above. `suspend=n` lets the game run and be connected to at any
time; `suspend=y` parks it until a debugger connects, and then `attach_endpoint` must be called with
`process_is_suspended: true` — get that wrong and the target either hangs forever or you miss the
start of execution.

Unity ships an unpatched `mono.dll` in some builds. If connecting fails with a message about
patching, that is the cause, and it is a target-side problem rather than a dgSpy one.

## Checklist

Record for each: pass/fail, elapsed time, and the exact `fault_message` on any failure.

1. **Connect.** `attach_endpoint { port: 55555 }` returns `state` of `running` or `paused`, never
   `attaching` or `exited`, and reports a `session_id`.
2. **Wrong port.** `attach_endpoint` against a closed port returns `state: "faulted"` with a
   `fault_message` naming the address and port. Covered automatically by the smoke script; confirm the
   message is the Mono one here.
3. **Session recovery.** `list_sessions` reports the session with `program_id` beginning `endpoint:unity:`.
4. **Second attach refused.** A second `attach_endpoint` returns `session_already_active`.
5. **Pause.** `pause` returns `paused`. `list_threads` then returns selectable thread IDs, but an
   arbitrary Unity pause can legitimately expose only native/unavailable stacks. A managed breakpoint
   stop should identify its stopping thread as current.
6. **Call stack.** `get_callstack` returns frames with a `module`, a non-zero `method_token`, and a
   formatted `name`. Note whether module paths are real files or in-memory Unity assemblies, since
   `set_il_breakpoint` takes the module path.
7. **Primitive locals.** Frames report locals with raw scalar values. Note any that come back empty —
   Mono and CorDebug differ here and the difference belongs in the capability model.
8. **Breakpoint.** `set_il_breakpoint` with a file-backed module/token and a known sequence-point
   offset binds, then `continue` + `wait_for_stop` observes the hit. A frame's current offset is not
   guaranteed to be a sequence point; check `bound` / `snapped`.
9. **Resume.** `continue` returns `running` and the game is responsive again.
10. **Detach.** `detach` returns `detached: true, terminated: false` and **the game keeps running**.
    This is the one that costs a live process when it is wrong.
11. **Reconnect.** `attach_endpoint` against the same port succeeds again after the detach.

## Results — run 2026-08-03 against UCH (Steam build, PID 43684)

All eleven steps pass. Timings from a warm machine:

| Step | Result |
|---|---|
| 1. Connect | ✅ `running` in **1120 ms**, correct PID |
| 2. Wrong port | ✅ `faulted` carrying dnSpy's address-and-port message |
| 3. Session recovery | ✅ `program_id` = `endpoint:unity:127.0.0.1:55555` |
| 4. Second attach refused | ✅ `session_already_active` |
| 5. Pause | ✅ `paused` in **48 ms**, thread selected |
| 6. Call stack | ✅ 8 frames in **434 ms**, incl. UCH's own `WorkerThread.DoWork()` in `Assembly-CSharp.dll` |
| 7. Primitive locals | ✅ e.g. `millisecondsTimeout : Int32 = 32` |
| 8. Breakpoint | ⚠️ binds **only at a sequence point** — see below |
| 9. Resume | ✅ `running` |
| 10. Detach | ✅ `detached: true, terminated: false`, **the game kept running** |
| 11. Reconnect | ✅ **415 ms** — but came back `paused`, see below |

What was unknown going in, now answered:

- **`CanDetachWithoutTerminating` is true for the Mono engine.** `detach` works normally and leaves the
  game running; `allow_terminate` is not needed. Verified twice.
- **Module identities can be either file-backed or in-memory.** One stack used
  `...\UltimateChickenHorse_Data\Managed\Assembly-CSharp.dll`; a later UI-thread stack began in
  `data-000001BE153D3040`. `set_il_breakpoint` currently consumes file-backed module paths unchanged;
  in-memory module images belong to Phase 6.
- **A failed connect does leave dnSpy's modal error dialog on screen.** It blocked nothing, as
  predicted, but it has to be clicked away by hand.

### The agent accepts exactly one connection

`server=y` listens for a single connection per game launch. A clean `detach` lets it listen again — a
reattach took 415 ms. Anything else consumes it permanently: a stray `TcpClient.Connect` "is it up?"
probe burned the endpoint twice during this work, after which `attach_endpoint` returns `faulted` with
"Couldn't connect to the debugged process" and the game must be relaunched. Never probe the port; let
`attach_endpoint` be the only thing that connects to it, and `detach` before closing dnSpy.

### The one that bites: Mono needs a sequence point

Mono's soft debugger rejects any offset that is not a sequence point — `NO_SEQ_POINT_AT_IL_OFFSET`,
swallowed into `CreateCouldNotCreateBreakpoint` in `DbgEngineImpl.Breakpoints.cs`. CorDebug accepts any
offset, which is why this never showed up in the .NET Framework tests. Measured on UCH with the event
cursor taken *before* each set:

| offset | bound | severity | hit |
|---|---|---|---|
| `Thread.Sleep+0` | true | none | yes, 6 ms |
| `Thread.Sleep+0x1A` | true | none | yes, 2 ms |
| `SleepInternal+0x30` | true | none | no |
| `DoWork+0x6E` | **false** | **error** | no |
| `DoWork+0` | true | none | no — entered once, long before |

The rule is *sequence point*, not *offset 0*: plenty of non-zero offsets qualify. A frame's own
`il_offset` often does not, which is awkward because frame identity invites feeding it straight back
in. `set_il_breakpoint` now reports `bound` and `severity`, and by default retries a refused offset at
method entry, setting `snapped` and a `warning`.

`bound` is a claim about *binding*, not a promise of a hit — a bound breakpoint in code that does not
run again stays silent, which the two `DoWork` rows show.

### Measuring a hit: take the cursor before setting the breakpoint

`Thread.Sleep` is called every ~32 ms, so a breakpoint on it fires *before* a follow-up
`get_session_state` can return. A cursor read after setting is already past the stop, and
`wait_for_stop` then reports a timeout for a breakpoint that is working perfectly. This produced a
whole table of false negatives during this work before it was spotted. `set_il_breakpoint` now returns
`cursor_event_id`, sampled before the breakpoint exists; pass that as `after_event_id`.

### Breakpoints outlive the session

Step 11 returned `paused` rather than `running` because the breakpoint from step 8 survived the detach,
rebound on reconnect, and hit immediately. dnSpy keeps breakpoints in `DbgCodeBreakpointsService`,
which is global rather than session-scoped. `remove_breakpoint` now removes one exact ID, while
`clear_breakpoints` removes the global collection.

### Not reproducible — do not trust the earlier note

One run reported "The breakpoint will not currently be hit. Can't set a breakpoint when the process is
paused" (severity `warning`). That message is dnSpy's own text, but the conclusion drawn from it — that
Mono cannot bind while paused — **does not reproduce**. Setting a breakpoint while paused binds cleanly
(`bound: true`, severity `none`) and hits on resume. The one sighting was during a session whose
connection was already broken.

### Re-run 2026-08-03, after the binding/cursor/thread-probe fixes

A second full pass, on a fresh UCH launch:

| Step | Result |
|---|---|
| `attach_endpoint` | ✅ `running` |
| `pause` | ✅ `paused` |
| `get_callstack` | ✅ 9 frames, on Unity's **UI thread** — `Image.GetDrawingDimensions`, `Image.GenerateSimpleSprite` |
| locals | ✅ `shouldPreserveAspect : Boolean = True` |
| `set_il_breakpoint` at the frame's own `il_offset` (`0x7`) | ✅ `bound: true`, `snapped: false` — a non-zero frame offset that *is* a sequence point |
| `wait_for_stop` via `cursor_event_id` | ✅ hit |
| `list_breakpoints` / `clear_breakpoints` | ✅ |
| `detach` | ✅ `detached: true, terminated: false`, game alive |

Note the first frame's module: `data-000001BE153D3040`, an in-memory module with no file path.
`set_il_breakpoint` takes a module path, so frames like that cannot currently carry a breakpoint.

### Also observed

- Headless thread selection originally took `Processes.SelectMany(p => p.Threads).First()`, which on
  Unity routinely landed on a thread with no managed frames. `list_threads` now returns metadata
  without fetching every stack, and callers select `thread_id` explicitly with `get_callstack` or
  `get_frame`. The fallback probe used when no ID is supplied is bounded because a disappearing Unity
  thread can otherwise lose the Mono `GET_FRAME_INFO` reply and wedge dnSpy's debugger thread.

### Re-run 2026-08-03, after caller selection and bounded frame fetch

- An arbitrary pause listed seven threads and returned no managed stacks, then resumed and detached
  cleanly instead of wedging dnSpy.
- A breakpoint in `AIBridgeServer.Handle` stopped the current `UE-AIBridge` thread; caller-selected
  `get_callstack` returned seven frames and primitive locals, and `get_frame(thread_id, 0)` matched.
- `remove_breakpoint` removed only the selected breakpoint, and detach returned
  `detached: true, terminated: false` with UCH still running.
