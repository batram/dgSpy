# dgSpy status and verification ledger

Last updated 2026-08-04. Branch `dgspy-mcp-milestone-1`.

Scope in force: x64 only, .NET Framework CorDebug (`CLR v4.0.30319`), plus the Mono/Unity path for UCH.
CoreCLR and x86 are out. Start at [the documentation index](README.md). The completed first implementation
section is summarized in [DELIVERED_LOCAL_CORE.md](DELIVERED_LOCAL_CORE.md); this file retains the detailed
evidence and limitations that should not be compressed into the active roadmap.

## Done and verified

Verified means exercised end to end against a real dnSpy and a real target, not just compiled.

| Capability                                                                                                                    | Status                                                                                  |
| ----------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| MEF extension loads in x64 net48 dnSpy, logs version                                                                          | ✅ verified                                                                             |
| Phase 0 engine acquisition: CorDebug via discovery, endpoint-launched UCH via `attach_endpoint`                               | ✅ verified both engines                                                                |
| CorDebug smoke target is explicitly x64 and its reported architecture is asserted                                             | ✅ automated                                                                            |
| **Phase 1 closed out** — one cancellation criterion was amended; see Correctness and safety below                             | ✅ 2026-08-03                                                                           |
| **Phase 2 closed out** — attach, launch, lifecycle control, terminal cleanup                                                  | ✅ 2026-08-03                                                                           |
| **Phase 3 closed out** — normalized event stream and non-destructive waiting                                                  | ✅ 2026-08-03                                                                           |
| Loopback TCP RPC, versioned, structured errors                                                                                | ✅ verified                                                                             |
| Extension RPC port refuses connections on a non-loopback interface                                                            | ✅ verified (this machine's LAN address)                                                |
| Gateway survives a dnSpy restart without being restarted                                                                      | ✅ verified (kill, relaunch, next call succeeds)                                        |
| `get_host_info` — versions, machine, architecture, engines, live `session_id`                                                 | ✅ verified                                                                             |
| `get_capabilities` — per-operation bounds, per-engine rules, limits                                                           | ✅ verified                                                                             |
| Gateway deadlines derived from the extension's advertised bounds                                                              | ✅ unit-tested invariant, no longer a guess                                             |
| `list_programs` `provider_names` selection + `attach_providers` in each entry                                                 | ✅ verified                                                                             |
| Two runtimes at one PID yield distinct stable `program_id`s                                                                   | ✅ unit-tested (no live fixture exists in scope)                                        |
| One-command build + deploy (`build-dgspy.ps1`)                                                                                | ✅ verified                                                                             |
| `list_programs`, incl. `process_ids` / `process_names` filtering                                                              | ✅ verified (2454 ms → 55 ms)                                                           |
| `program_id` from typed fields; `runtime_guid` surfaced                                                                       | ✅ verified                                                                             |
| `attach` — waits for threads, refuses a second session                                                                        | ✅ verified                                                                             |
| `attach_endpoint` — argument validation and failure path                                                                      | ✅ verified (faults in ~2 s with dnSpy's own reason)                                    |
| `attach_endpoint` — connecting to a live Mono/Unity endpoint                                                                  | ✅ **verified against UCH** (1120 ms; reattach 415 ms)                                  |
| `launch` — CorDebug target starts through dnSpy options                                                                       | ✅ automated live                                                                       |
| `restart` — same logical session, replacement target PID                                                                      | ✅ automated live                                                                       |
| `terminate` — explicit semantics, target is gone                                                                              | ✅ automated live                                                                       |
| Unexpected target exit — terminal event with PID, reason, nonzero exit code                                                   | ✅ automated live (exit 23)                                                             |
| Mono/Unity pause and detach                                                                                                   | ✅ reverified against UCH after bounded frame-fetch fix; detach leaves the game running |
| Mono/Unity call stack and primitive locals                                                                                    | ✅ reverified on the known managed breakpoint stopping thread                           |
| `set_il_breakpoint` reports `bound` / `severity` / `message`                                                                  | ✅ verified both engines                                                                |
| Mono sequence-point snapping (`snapped`, `warning`)                                                                           | ✅ verified against UCH                                                                 |
| `list_breakpoints`, `clear_breakpoints`                                                                                       | ✅ verified (CorDebug smoke + UCH)                                                      |
| `remove_breakpoint` — exact single-ID removal                                                                                 | ✅ verified (CorDebug smoke + UCH)                                                      |
| `cursor_event_id` — cursor sampled before the breakpoint exists                                                               | ✅ verified against UCH                                                                 |
| Unity `get_callstack` thread probe — picks a thread with frames                                                               | ✅ verified against UCH (landed on the UI thread)                                       |
| `list_threads`, caller-selected `get_callstack` / `get_frame`                                                                 | ✅ verified (CorDebug smoke + UCH breakpoint stop)                                      |
| `faulted` carries `fault_message` from `MessageUserMessage`                                                                   | ✅ verified                                                                             |
| `detach` — leaves target alive, refuses unsafe detach                                                                         | ✅ verified                                                                             |
| `list_sessions` — recovers a lost `session_id`                                                                                | ✅ verified                                                                             |
| `get_session_state` — validates `session_id`                                                                                  | ✅ verified                                                                             |
| `pause` / `continue` — report the state they produced                                                                         | ✅ verified                                                                             |
| `set_il_breakpoint` by module + token + IL offset                                                                             | ✅ verified                                                                             |
| `wait_for_stop` — cursor-based, non-destructive                                                                               | ✅ verified                                                                             |
| `wait_for_event`, event-kind filters, bounded timeout                                                                         | ✅ automated live                                                                       |
| Normalized breakpoint stop — process, thread, breakpoint, module/token/offset                                                 | ✅ automated live                                                                       |
| Concurrent waits before resume receive the same next stop                                                                     | ✅ automated live                                                                       |
| Event truncation cursor and waiter cancellation recovery                                                                      | ✅ unit-tested                                                                          |
| `get_callstack` — method names, frame identity, primitive locals                                                              | ✅ verified                                                                             |
| Gateway `Origin` validation + `X-dgSpy-Token`, fails closed                                                                   | ✅ verified                                                                             |
| Evaluation off the dispatcher (`EvaluationQueue`)                                                                             | ✅ built and regression-tested, benefit not directly observable                         |
| Response serialization off the dispatcher                                                                                     | ✅ built, not directly observable                                                       |
| Extension split into entry point, RPC, debugger, events, and identity boundaries                                              | ✅ built                                                                                |
| `dgSpy.Extension.Tests` identity, state, event-cursor, stale-frame, and detach coverage                                      | ✅ 18 tests                                                                             |
| Event-kind and stop-reason vocabularies advertised in `get_capabilities`                                                      | ✅ verified both engines                                                                |
| Every kind the Mono engine actually emits is in the advertised vocabulary                                                     | ✅ cross-checked against a live UCH session                                             |
| An unknown `kinds` value is rejected rather than silently matching nothing                                                    | ✅ verified both engines                                                                |
| **Phase 4 closed out** — conditions, hit counts, tracepoints, exception breakpoints, stepping                                 | ✅ automated CorDebug + live Mono                                                       |
| `update_breakpoint` — enabled, condition, hit count, trace; empty string clears                                               | ✅ automated CorDebug + live Mono behavior                                              |
| A continuing tracepoint warns that it produces no stop                                                                        | ✅ automated CorDebug + live Mono                                                       |
| `set_exception_breakpoint` / `list_exception_breakpoints`, first-chance by default and bounded                                | ✅ automated CorDebug + equivalent policy lifecycle/live stop on Mono                   |
| `step_into` / `step_over` / `step_out`, completion through `wait_for_stop` with `stop_reason: "step"`                         | ✅ automated CorDebug + live Mono                                                       |
| **Phase 5 closed out** — evaluation, member expansion, assignment, watches, modules                                           | ✅ automated CorDebug + live Mono                                                       |
| `evaluate` — raw scalar and display text separate; arithmetic, not just lookup                                                | ✅ automated CorDebug + live Mono                                                       |
| `has_raw_value` separates `null` from optimized-away/unavailable                                                              | ✅ unit-tested + live (`this` in a static method)                                       |
| `get_members` — one level, paged, `total` / `truncated`, member expressions round-trip                                        | ✅ automated CorDebug + live Mono                                                       |
| `set_value` — assigns in the target, reads back, reports `compiler_error`                                                     | ✅ automated CorDebug + live Mono reversible assignment                                |
| `add_watch` / `list_watches` / `remove_watch`; a failing watch does not fail the call                                         | ✅ automated CorDebug + live Mono                                                       |
| `list_modules` — `can_set_breakpoint` follows the engine's published module identity                                         | ✅ automated CorDebug + live Mono                                                       |
| Dynamic and in-memory modules — metadata, IL, C#, raw image, breakpoints, and a frame naming one                              | ✅ automated CorDebug + live Mono binding                                                |
| **Phase 6 closed out** — symbols, decompilation, text search, analysis, metadata, raw modules                                 | ✅ 2026-08-04 (CorDebug + UCH)                                                          |
| `list_documents` / `list_types` / `list_members` — paged, tokens included                                                     | ✅ automated live                                                                       |
| `search_symbols` — name to module + token, bounded                                                                            | ✅ automated live                                                                       |
| `get_il` — offsets, operands, and `is_sequence_point` per instruction                                                         | ✅ automated live                                                                       |
| `get_csharp` — method and whole-type decompilation                                                                            | ✅ automated live                                                                       |
| `search_text` / `find_references` / `find_implementations` — bounded analysis with symbol identities                          | ✅ automated CorDebug + live UCH                                                        |
| `get_metadata` / `get_raw_module` — token facts and paged image with SHA-256                                                  | ✅ automated CorDebug + live UCH                                                        |
| `set_breakpoint` by type + method name, same path as `set_il_breakpoint`                                                      | ✅ automated live                                                                       |
| **Phase 7 closed out** — explicit invocation, memory, disassembly, capabilities, set-IP, hard func-eval timeout               | ✅ automated CorDebug + live Mono                                                       |
| `invoke_method` / `create_object` — separate side-effecting tools with audit ids                                              | ✅ automated CorDebug + live Mono                                                       |
| `read_memory` / `write_memory` — bounded target access; writes visibly side-effecting                                         | ✅ automated CorDebug + live Mono idempotent write                                      |
| `get_disassembly` — managed IL and capability-gated native blocks                                                             | ✅ CorDebug managed/native + Mono managed/unsupported-native contract                   |
| `get_registers` — explicit `capability_unsupported` on this dnSpy contract                                                    | ✅ verified both engines                                                               |
| `set_instruction_pointer` — current-frame/method and engine validation, audited                                               | ✅ automated CorDebug + live Mono same-location mutation                                |
| **Phase 8 complete** — debugger completeness                                                                                  | ✅ CorDebug end to end + applicable Mono surface verified                              |
| Object IDs across resume/release/detach and structured C# Autos                                                               | ✅ automated CorDebug + live Mono                                                       |
| Bounded cursor-based debugger output, separate from stop events                                                               | ✅ unit + automated live                                                                |
| Module load/unload breakpoint filters                                                                                         | ✅ automated CorDebug load + unload stops; live Mono unload stop                        |
| Canonical bounded breakpoint export/import; merge/replace and dry-run contracts                                               | ✅ automated live replace + dry-run                                                     |
| Exception categories, flags, module conditions, removal and reset                                                             | ✅ actual CorDebug + Mono first-chance stops and policy lifecycle                        |
| Value export chunks and host writes below `DGSPY_EXPORT_ROOT`                                                                 | ✅ CorDebug automated + live Mono write/reparse refusal                                  |
| `analyze_symbol` typed caller/callee/field/construction/override/implementation/attribute/event edges with a hard scan budget | ✅ automated live across all listed edge kinds                                          |

Test suites, all green:

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj   # 29 checks, wire + capability contract
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj     # 182 checks, access control + transport + deadlines
dotnet test .\tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj # 18 checks, extension core
.\tests\run-milestone1-smoke.ps1                                       # 365 checks, end to end
```

All four suites are green as of 2026-08-04: 29 / 182 / 18 unit
checks and 365 live smoke checks. The event-vocabulary work and complete Phase 6 surface were additionally
verified against live UCH — see
[DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md). The durable UCH pass now verifies the remaining
applicable Phase 4, 5, and 7 behavior as well: conditioned and hit-counted stops, tracepoint semantics,
all step kinds, assignment, watches, invocation/construction, memory, disassembly capability behavior,
register failure, and set-IP.
Phase 8 has a dedicated 45-check UCH pass, including an actual module-unload breakpoint stop, an actual
categorized first-chance exception stop, successful host value export and reparse-point refusal, and
detach-driven object-ID cleanup.

## Closed gaps and explicit boundaries

### Closed: frames can name a module that has no file

A UCH stack contained `data-000001BE153D3040` — an in-memory or dynamic module. **Metadata for such a
module is reachable**: `DbgMetadataService` resolves it, so `get_csharp`, `get_il`, `list_types` and
`list_members` all work against it. **Breakpoints now work as well.** dgSpy imports dnSpy's engine
`DbgModuleIdProvider` instances and uses their full runtime identity rather than a path-only identity.
`list_modules.can_set_breakpoint` reports whether an engine provider published that identity.

Verified on CorDebug as of 2026-08-04. The fixture now carries two file-less modules of its own — an
assembly loaded from bytes and a Reflection.Emit dynamic assembly — each reached only through a callback
into the target, so a breakpoint in the callee leaves the file-less module's frame at index 1. The smoke
drives metadata, IL, C#, raw image, breakpoint binding and a real stop, plus that frame for both.
What the live run corrected:

- **An in-memory module reports a bare assembly name as its filename**, not an empty string. File paths
  are therefore display data, not breakpoint identity; the engine's `DbgModuleIdProvider` is authoritative.
- **dnSpy reports a dynamic module as in-memory as well.** `is_dynamic` is what separates the two.
- Both breakpoint entry points now resolve a loaded module through the same runtime identity. An
  unloaded file-backed path retains dnSpy's pending-breakpoint behavior.

**Mono verified 2026-08-04** against live UCH — see [DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md).
A modded UCH carries metadata-backed MonoMod/Harmony/Unity modules plus transient `eval-*` modules;
metadata, IL, C#, and raw image behave as on
CorDebug. Breakpoint binding is live-verified on three metadata-backed file-less Mono modules. The
`eval-*` modules publish neither metadata nor a stable identity, so reads refuse with
`metadata_unavailable` and `can_set_breakpoint` stays false. The one thing still not reproduced on Mono is a _frame_ whose module
has no file: Harmony patch frames report the original file-backed module, and the single historical
sighting was a rendering UI-thread stack.

### Correctness and safety

1. **Cancellation cannot abort in-flight work.** An expired deadline abandons the _wait_; a queued
   dispatcher callback or a started evaluation runs to completion, because dnSpy exposes no way to
   cancel either. Commented at both call sites in `ExtensionEntryPoint.cs`. This is the one Phase 1
   exit criterion that was amended rather than met; it is now advertised to callers as
   `limits.cancels_in_flight_work: false` instead of being left to be discovered. Func-eval has its
   own hard engine timeout; a dispatcher callback already executing remains an upstream constraint.
2. **The `stale_handle` path has deterministic contract coverage.** `FrameSnapshotGuard` is called
   immediately before frame reads and evaluation, and its exact error code is unit-tested. The physical
   resume-at-that-instruction race remains intentionally unsuitable as a deterministic live fixture.
3. **A connect failure leaves a modal dnSpy error dialog on screen.** dnSpy's own UI subscribes to
   `MessageUserMessage` and shows a message box. It runs on the UI thread, so it blocks neither the
   debugger dispatcher nor RPC — dgSpy keeps working around it — but nothing headless dismisses it, and
   they accumulate. dgSpy no longer _adds_ to this: routine client disconnects used to go to
   `WriteMessage(ErrorUser, …)`, which is dnSpy's message-box channel, and the gateway opens a fresh
   connection per request. Those are now silent, with real faults going to `Output`.
4. **Single global session.** One `sessionId` field, one target. `host_id` routing and multi-session
   ownership are Phase 9; nothing in the code anticipates them.
5. ~~**The event-kind vocabulary is undiscoverable.**~~ Fixed 2026-08-03. The kinds lived as string
   literals at the emitting call sites and were advertised nowhere, so a `kinds` filter with a near miss
   like `breakpoint` for `breakpoint_hit` returned a clean empty result — indistinguishable from "the
   event never happened", the same false-negative shape as reading the event cursor too late. The
   vocabulary is now `dgSpy.Protocol.EventKinds`, every call site names a constant from it,
   `get_capabilities` serves `event_kinds` and `stop_reasons`, and an unknown kind is rejected with
   `invalid_argument` naming the valid set.

### Quality and structure

1. **`RpcHost` remains one logical owner split across focused partials.** Physical responsibilities are
   separated into RPC, debugger, evaluation, decompiler, events, and identity files; independently
   testable policy lives in pure helpers. dnSpy dispatcher objects deliberately do not cross that owner.
2. **Streamable HTTP compatibility is closed.** `POST /mcp` negotiates supported protocol revisions,
   validates subsequent version headers, and returns 202 for notifications. `GET /mcp` returns the
   specification-defined 405 because dgSpy has no server-initiated SSE messages. The transport is
   deliberately stateless and therefore does not mint optional `MCP-Session-Id` values; debugger
   ownership continues to use explicit tool `session_id` arguments.
3. ~~**Per-tool gateway deadlines are a hardcoded table.**~~ Fixed 2026-08-03. The bounds live in
   `dgSpy.Protocol.CapabilityCatalog`, the extension serves them through `get_capabilities`, and
   `ToolCatalog.DeadlineSeconds` derives each deadline from the matching bound plus a margin. A gateway
   unit test fails if any advertised tool loses that headroom or names an operation the catalog does not
   know. Remaining wrinkle: the catalog's bounds are maintained by hand against the waits in `RpcHost`,
   so a new wait needs its bound updated with it.

## Hard-won facts worth not rediscovering

- **Closing dnSpy with a session attached terminates the target.** Observed, cost a live PowerShell
  process. `detach` is the only safe exit.
- **`attach` must wait for threads, not just a process.** A pause inside that window yields a stop with
  no current thread and an empty call stack, and it does not recover until the target runs again.
- **The call stack follows `DbgManager.CurrentThread`**, which only dnSpy's UI or a thread-carrying
  stop sets. Headless callers must select a thread themselves.
- **`RuntimeId` has no string form** — only `Equals`/`GetHashCode`. Compose identity from typed fields.
- **Both supported engines share one `RuntimeKindGuid`**; only `RuntimeGuid` separates them.
- **`AttachableProcess.Attach()` is `DbgManager.Start(GetOptions())` with the error string discarded.**
  Calling `Start` directly is the same attach and reports why a rejected one was rejected.
- **`UnityAttachToProgramOptions` is internal** to dnSpy's Mono engine and unreachable from an
  extension. `UnityConnectStartDebuggingOptions` is public, and `DbgEngineProviderImpl` /
  `DbgEngineImpl.StartCore` treat the two identically, `wasAttach` included.
- **Mono only accepts a breakpoint at a sequence point.** `vm.CreateBreakpointRequest(method, offset)`
  throws `NO_SEQ_POINT_AT_IL_OFFSET` otherwise, and dnSpy turns that into an unbound breakpoint with
  "Could not create the breakpoint". CorDebug accepts any IL offset, so nothing in the .NET Framework
  tests could have caught this. The rule is _sequence point_, not _offset 0_ — `Thread.Sleep+0x1A` hits
  fine. Feeding a frame's own `il_offset` straight back in usually does not.
- **A Mono `server=y` endpoint accepts one connection per launch.** A clean `detach` lets it listen
  again; anything else consumes it and the target must be relaunched. A one-line `TcpClient.Connect`
  liveness probe burned it twice during development — do not probe the port.
- **Read the event cursor before setting a breakpoint.** On a method called every ~32 ms the hit lands
  before a follow-up `get_session_state` can return, so a cursor taken afterwards is already past the
  stop and `wait_for_stop` times out on a working breakpoint. This produced an entire table of false
  negatives before it was caught. `set_il_breakpoint` returns `cursor_event_id` for this.
- **A dnSpy settings write does not take effect on the dispatcher hop that makes it.**
  `DbgCodeBreakpointImpl.Settings` does not assign — it calls `DbgCodeBreakpointsServiceImpl.Modify`,
  which posts `ModifyCore` back to the dispatcher _even when the caller is already on it_. Describing
  the breakpoint in the same callback therefore returns the previous settings, and the write looks like
  it silently did nothing. Write on one hop, read back on the next; dispatcher delivery is FIFO, so the
  queued work runs in between. The exception settings service behaves the same way. This produced six
  simultaneous false failures the first time `update_breakpoint` was exercised.
- **dnSpy stops on second chance for essentially every .NET exception it ships.** Listing "everything
  that stops" returned ~2500 stock entries identical on every machine. `list_exception_breakpoints`
  therefore defaults to first-chance only, which is the set someone actually configured, and is bounded
  with `total` and `truncated`.
- **A tracepoint that continues produces no stop event at all**, so `wait_for_stop` on one waits
  forever. `update_breakpoint` returns a warning when it sets one.
- **`DbgMessageThreadExitedEventArgs` upstream drops its own `exitCode`.** The constructor takes the
  parameter and never assigns the property, so thread exit codes were always null. Fixed in the fork —
  the only edit dgSpy makes to dnSpy's own sources, recorded in `DGSPY_BASELINE.md`.
- **dnSpy's breakpoints are global, not session-scoped.** They survive `detach` and rebind on the next
  attach, so a fresh session can stop immediately on a breakpoint from the previous one.
- **Thread handles can go stale between `list_threads` and inspection.** UCH creates and exits worker
  threads during an ordinary pause. `get_callstack` returns `thread_not_found` for that exact stale
  handle instead of silently switching threads; refresh `list_threads` and retry another returned ID.
- **Mono's asynchronous `GET_FRAME_INFO` can lose its reply when a Unity thread exits.** The upstream
  `ThreadMirror.GetFrames()` waited forever, monopolizing dnSpy's single Mono debugger thread so Pause,
  Continue and Detach all queued behind it. The wait is now bounded to three seconds, and
  `list_threads` returns metadata without probing every thread. A 2026-08-03 live UCH stress run walked
  all seven listed threads, resumed, and detached successfully instead of leaving dnSpy stuck Running.
- **An arbitrary manual Unity pause can expose only native/unavailable stacks.** One live pause listed
  seven threads but returned no managed frames; this is not a debugger wedge. At a known managed
  breakpoint stop, the current `UE-AIBridge` thread returned seven selected frames and primitive locals.
- **`DbgManager.Start` returning null does not mean the target is connected.** It only means an engine
  was built. A Mono connect then retries the socket for the whole connection timeout and reports
  failure through `MessageUserMessage`, not through the `Start` return value.
- **Unity's attach providers are skipped unless named explicitly.** `UnityPlayerAttachProgramOptionsProviderFactory.Create(allFactories)`
  returns null when `allFactories` is true, so an unfiltered `list_programs` never runs multicast
  discovery — the ~2.4 s cost is the CorDebug providers probing every process, not Unity.
- **The extension needs `Newtonsoft.Json.dll` beside it**; dnSpy does not ship it, and a missing
  dependency makes dnSpy drop the extension silently.
- **A stale extension copy in dnSpy's bin root is composed twice.** `build-dgspy.ps1` removes them.
- **Every `list_programs` call replaces the set of valid `program_id` values — including one that
  returns nothing.** A provider-filtered listing that matches no process empties the cache, and the
  next `attach` fails with `program_not_found` on an id that was valid seconds earlier. This bit the
  smoke script itself when provider filtering was added; it re-lists before attaching.
- **`AttachableProcess` does not say which provider produced it.** The provider names in
  `attach_providers` are derived from the runtime GUID, which is why Unity reports both `UnityEditor`
  and `UnityPlayer`.
- **`@(...)` around a `ConvertFrom-Json` array can produce a one-element array holding the collection.**
  `$list.Count` then reads 1 while the payload plainly contains several items, and a `Where-Object`
  filter over it matches nothing. Pipe through `ForEach-Object { $_ }` to flatten. This cost a full
  smoke cycle chasing a watch-evaluation "bug" whose JSON was correct all along. It bit a second time in
  `list_modules`: a filter that matched the nested array passed the _whole_ collection through, so two
  module assertions were green on some other module's flags until a module with a legitimately false
  `can_set_breakpoint` was added and one of them flipped. A passing check proves nothing if the filter
  never narrowed anything.
- **Windows PowerShell 5.1 is .NET Framework**: no `RandomNumberGenerator.GetBytes(int)`, no
  `Convert.ToHexString`; `-match` against a collection returns matches rather than a boolean; and
  `ConvertFrom-Json '[]'` does not survive `.Count` checks. All three cost debugging cycles in the test
  harness — the smoke script has comments where each bit.

## Local PowerShell scratchpads

Reusable agent-side debugger scripts live in `ps_scratch/`. The directory is deliberately ignored by
Git because these scripts contain machine-local paths, ports, process ownership, and transient session
workflows; inspect and reuse them before writing another temporary RPC harness. Current helpers cover
starting the repo-built dnSpy, calling the extension RPC safely, listing/detaching sessions, and the
live UCH pause/call-stack/breakpoint-cursor check. Keep durable product tests in `tests/`, not here.
