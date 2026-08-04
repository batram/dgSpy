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

From PowerShell:

```powershell
$uch = 'S:\SteamLibrary\steamapps\common\Ultimate Chicken Horse'
Start-Process -FilePath (Join-Path $uch 'UltimateChickenHorse.exe') -WorkingDirectory $uch -ArgumentList '--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n'
```

Quote the argument as one single-quoted string. PowerShell splits an unquoted argument on its commas
and passes three separate tokens, and Unity ignores a malformed `--debugger-agent` silently: the game
starts normally, nothing listens on 55555, and `attach_endpoint` faults with a connection message that
looks exactly like a target-side problem.

`-WorkingDirectory` matters too — Doorstop resolves BepInEx relative to the process working directory,
so launching from elsewhere gives an unmodded game and none of the plugin modules to set breakpoints in.

The equivalent from a Cygwin/bash shell in the game directory, which is the same launch:

```bash
./UltimateChickenHorse.exe --debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n
```

Confirm the agent is actually listening by attaching, **not** by probing the port: `server=y` accepts
one connection per launch, and a bare TCP connect consumes it. See the warning at the bottom of this
document.

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

### Reconnect-state race before breakpoint binding

The warning "The breakpoint will not currently be hit. Can't set a breakpoint when the process is
paused" is reproducible immediately after some Mono reconnects, even when dgSpy's session snapshot
already says `running`. The session state and Mono's breakpoint engine have not converged yet. A full
`pause` -> `continue` transition followed by a short settle makes binding deterministic; the next
breakpoint binds with severity `none` and hits normally. This is a reconnect-state race, not a general
rule that Mono cannot bind while paused: ordinary paused-session binding still works.

Re-run on 2026-08-03 after the clean build/deploy fix: the unsynchronized first bind reproduced the
warning, while the synchronized retry bound `AIBridgeServer.Handle`, observed event 18 after cursor 17,
returned seven caller-selected frames on `UE-AIBridge` with primitive locals, and detached without
terminating UCH.

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

### Re-run 2026-08-04: dynamic and in-memory modules

The Mono half of the file-less module work. CorDebug is covered automatically now (the fixture carries
an in-memory and a dynamic module of its own); this is the engine the original
`data-000001BE153D3040` sighting came from. Scripts: `ps_scratch\Test-UchFilelessModules.ps1` (34
checks), `Test-UchFilelessFrames.ps1` (8), `Test-UchHarmonyCallerFrame.ps1` (5). All green.

A modded UCH carries **16 file-less modules**, so this is the normal case here, not an edge case:

| Module | Kind | Metadata |
|---|---|---|
| `MonoMod.Utils.Cil.ILGeneratorProxy` | in-memory | ✅ |
| `MonoMod.Utils.GetManagedSizeHelper` | in-memory | ✅ |
| `UnityEngine.CoreModule` (a second, in-memory copy) | in-memory | ✅ |
| `HarmonyDTFAssembly1`–`4` | in-memory | ✅ |
| `eval-0`–`eval-8` (Mono func-eval scratch assemblies) | dynamic | ❌ none published |

Verified against each of the first three: `get_metadata`, `list_types`, `list_members`, `get_il`,
`get_csharp` and `get_raw_module` all resolve **by module name alone**, and `set_breakpoint` refuses
with `module_has_no_path`. The `eval-*` modules refuse every read with `metadata_unavailable` rather
than returning empty data, and are still listed rather than dropped.

What this pass corrected:

- **`data-<hex>` is the display name, not the filename.** An in-memory Mono module reports that same
  string as its *filename* too — it is not empty, and it is not a path. `is_dynamic` / `is_in_memory`
  are the reliable test; emptiness is not. The CorDebug `set_breakpoint` guard keyed off emptiness and
  therefore accepted such a module; both now share one predicate.
- **A dynamic module is reported as in-memory as well.** `is_dynamic` is what separates the two.
- **Harmony patches do not produce a file-less frame on Mono.** Measured over two passes, graphical
  and headless: 36 breakpoints bound in plugin patch methods, every hit reporting a *file-backed*
  caller (`Assembly-CSharp.dll`, `UnityEngine.UI.dll`). HarmonyX detours through MonoMod and the soft
  debugger attributes the frame to the original method's module, not to `HarmonyDTFAssemblyN`.

**Still not reproduced on Mono: a frame whose module has no file.** Eight sampled pauses and three
breakpoint stops with all-thread stack scans found none. The one historical sighting was a rendering
UI-thread stack, and `-nographics` removes that thread's work entirely. The behaviour is covered
deterministically on CorDebug by the fixture; on Mono it remains observed-once.

### Running UCH without it stealing focus

`-batchmode -nographics` — a minimized window is not enough, Unity restores and focuses it during
startup. `ps_scratch\Restart-UchBackground.ps1` does this. The trade-off is that `-nographics` removes
the UI thread, which is exactly where the file-less frame was originally seen, so use a graphical
launch when hunting for that specifically.

dnSpy needs `--dgspy-no-window-activation` for the same reason; see
[DGSPY_BASELINE.md](DGSPY_BASELINE.md). Verified with `ps_scratch\Watch-ForegroundWindow.ps1`: across
36 breakpoint binds and 3 stops the foreground never changed.

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

### Re-run 2026-08-03, after the event-vocabulary change

A fresh UCH launch, 20 checks, all green (`ps_scratch\Test-UchEventVocabulary.ps1`):

| Step | Result |
|---|---|
| `get_capabilities` | ✅ advertises `event_kinds` and `stop_reasons`; `breakpoint_hit` present, the near miss `breakpoint` absent |
| `attach_endpoint` | ✅ `running` in 752 ms |
| Emitted-vs-advertised cross-check | ✅ every kind the Mono engine actually produced — `session_started`, `attached`, `continued`, `process_created`, `runtime_created`, `module_loaded`, `thread_created` — is in the advertised vocabulary |
| `wait_for_event` with `kinds: ["breakpoint"]` | ✅ `invalid_argument` naming `breakpoint_hit`, instead of a silent timeout |
| `get_events` with `["stopped","nonsense"]` | ✅ rejected, naming the offending value |
| `get_events` with `["module_loaded"]` | ✅ returns only that kind |
| `wait_for_event` with `["step_completed"]` | ✅ times out on merit — a real kind milestone 1 never emits |
| `pause` → `list_threads` | ✅ `paused`, 9 threads |
| Normalized stop | ✅ `stop_reason: "pause"`, a value from the advertised set; `get_stop_reason` agrees |
| `detach` | ✅ `detached: true, terminated: false`, game alive |

The cross-check is the one worth keeping: it is what proves the catalog is complete on the Mono path
rather than merely self-consistent.

### Re-run 2026-08-04: Phase 8 debugger completeness

`tests\run-uch-phase8.ps1` passes **20/20 checks** against the headless Steam build. It attaches
through the Mono endpoint, reaches a managed breakpoint in UltimateGlorpExplorer, and verifies:

- C# Autos on a selected Mono frame;
- object-ID create, survival across resume, evaluate, and release using `System.AppDomain.CurrentDomain`;
- hashed scalar value export and bounded `analyze_symbol` execution;
- module-breakpoint filter round-trip;
- canonical breakpoint export plus merge dry-run;
- exception flags, module conditions, and custom-policy removal;
- debugger output access separate from stop events;
- clean detach without terminating UCH.

The preceding Phase 4–6 synchronization pass also completed managed evaluation and stepping, but reproduced
the known reconnect race on its first breakpoint: dnSpy returned "Can't set a breakpoint when the process
is paused." Twelve subsequently armed breakpoints bound and one hit, so this remains the documented initial
binding race rather than a Phase 8 regression.

Not yet exercised on UCH: an actual module-unload breakpoint stop, a categorized exception stop, host value
export, object-ID disposal caused by runtime exit/detach, breakpoint `replace`, and multi-target behavior.
