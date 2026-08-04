# dgSpy status and handoff

Last updated 2026-08-04. Branch `dgspy-mcp-milestone-1`.

Scope in force: x64 only, .NET Framework CorDebug (`CLR v4.0.30319`), plus the Mono/Unity path for UCH.
CoreCLR and x86 are out. See [DGSPY_BASELINE.md](DGSPY_BASELINE.md) for the toolchain and thread rules,
[DGSPY_MILESTONE1.md](DGSPY_MILESTONE1.md) for the tool surface,
[DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md) for the manual Mono/Unity pass,
`IMPLEMENTATION_PLAN.md` for the roadmap.

**Phase 0 is complete.** All exit criteria are verified, including both supported engine acquisition
paths and the automated x64 target assertion.

## Done and verified

Verified means exercised end to end against a real dnSpy and a real target, not just compiled.

| Capability | Status |
|---|---|
| MEF extension loads in x64 net48 dnSpy, logs version | ✅ verified |
| Phase 0 engine acquisition: CorDebug via discovery, endpoint-launched UCH via `attach_endpoint` | ✅ verified both engines |
| CorDebug smoke target is explicitly x64 and its reported architecture is asserted | ✅ automated |
| **Phase 1 closed out** — see `IMPLEMENTATION_PLAN.md` for the one amended criterion | ✅ 2026-08-03 |
| **Phase 2 closed out** — attach, launch, lifecycle control, terminal cleanup | ✅ 2026-08-03 |
| **Phase 3 closed out** — normalized event stream and non-destructive waiting | ✅ 2026-08-03 |
| Loopback TCP RPC, versioned, structured errors | ✅ verified |
| Extension RPC port refuses connections on a non-loopback interface | ✅ verified (this machine's LAN address) |
| Gateway survives a dnSpy restart without being restarted | ✅ verified (kill, relaunch, next call succeeds) |
| `get_host_info` — versions, machine, architecture, engines, live `session_id` | ✅ verified |
| `get_capabilities` — per-operation bounds, per-engine rules, limits | ✅ verified |
| Gateway deadlines derived from the extension's advertised bounds | ✅ unit-tested invariant, no longer a guess |
| `list_programs` `provider_names` selection + `attach_providers` in each entry | ✅ verified |
| Two runtimes at one PID yield distinct stable `program_id`s | ✅ unit-tested (no live fixture exists in scope) |
| One-command build + deploy (`build-dgspy.ps1`) | ✅ verified |
| `list_programs`, incl. `process_ids` / `process_names` filtering | ✅ verified (2454 ms → 55 ms) |
| `program_id` from typed fields; `runtime_guid` surfaced | ✅ verified |
| `attach` — waits for threads, refuses a second session | ✅ verified |
| `attach_endpoint` — argument validation and failure path | ✅ verified (faults in ~2 s with dnSpy's own reason) |
| `attach_endpoint` — connecting to a live Mono/Unity endpoint | ✅ **verified against UCH** (1120 ms; reattach 415 ms) |
| `launch` — CorDebug target starts through dnSpy options | ✅ automated live |
| `restart` — same logical session, replacement target PID | ✅ automated live |
| `terminate` — explicit semantics, target is gone | ✅ automated live |
| Unexpected target exit — terminal event with PID, reason, nonzero exit code | ✅ automated live (exit 23) |
| Mono/Unity pause and detach | ✅ reverified against UCH after bounded frame-fetch fix; detach leaves the game running |
| Mono/Unity call stack and primitive locals | ✅ reverified on the known managed breakpoint stopping thread |
| `set_il_breakpoint` reports `bound` / `severity` / `message` | ✅ verified both engines |
| Mono sequence-point snapping (`snapped`, `warning`) | ✅ verified against UCH |
| `list_breakpoints`, `clear_breakpoints` | ✅ verified (CorDebug smoke + UCH) |
| `remove_breakpoint` — exact single-ID removal | ✅ verified (CorDebug smoke + UCH) |
| `cursor_event_id` — cursor sampled before the breakpoint exists | ✅ verified against UCH |
| Unity `get_callstack` thread probe — picks a thread with frames | ✅ verified against UCH (landed on the UI thread) |
| `list_threads`, caller-selected `get_callstack` / `get_frame` | ✅ verified (CorDebug smoke + UCH breakpoint stop) |
| `faulted` carries `fault_message` from `MessageUserMessage` | ✅ verified |
| `detach` — leaves target alive, refuses unsafe detach | ✅ verified |
| `list_sessions` — recovers a lost `session_id` | ✅ verified |
| `get_session_state` — validates `session_id` | ✅ verified |
| `pause` / `continue` — report the state they produced | ✅ verified |
| `set_il_breakpoint` by module + token + IL offset | ✅ verified |
| `wait_for_stop` — cursor-based, non-destructive | ✅ verified |
| `wait_for_event`, event-kind filters, bounded timeout | ✅ automated live |
| Normalized breakpoint stop — process, thread, breakpoint, module/token/offset | ✅ automated live |
| Concurrent waits before resume receive the same next stop | ✅ automated live |
| Event truncation cursor and waiter cancellation recovery | ✅ unit-tested |
| `get_callstack` — method names, frame identity, primitive locals | ✅ verified |
| Gateway `Origin` validation + `X-dgSpy-Token`, fails closed | ✅ verified |
| Evaluation off the dispatcher (`EvaluationQueue`) | ✅ built and regression-tested, benefit not directly observable |
| Response serialization off the dispatcher | ✅ built, not directly observable |
| Extension split into entry point, RPC, debugger, events, and identity boundaries | ✅ built |
| `dgSpy.Extension.Tests` identity, state, and event-cursor coverage | ✅ 15 tests |
| Event-kind and stop-reason vocabularies advertised in `get_capabilities` | ✅ verified both engines |
| Every kind the Mono engine actually emits is in the advertised vocabulary | ✅ cross-checked against a live UCH session |
| An unknown `kinds` value is rejected rather than silently matching nothing | ✅ verified both engines |
| **Phase 4 closed out** — conditions, hit counts, tracepoints, exception breakpoints, stepping | ✅ 2026-08-03 |
| `update_breakpoint` — enabled, condition, hit count, trace; empty string clears | ✅ automated live |
| A continuing tracepoint warns that it produces no stop | ✅ automated live |
| `set_exception_breakpoint` / `list_exception_breakpoints`, first-chance by default and bounded | ✅ automated live |
| `step_into` / `step_over` / `step_out`, completion through `wait_for_stop` with `stop_reason: "step"` | ✅ automated live |
| **Phase 5 closed out** — evaluation, member expansion, assignment, watches, modules | ✅ 2026-08-03 (CorDebug) |
| `evaluate` — raw scalar and display text separate; arithmetic, not just lookup | ✅ automated live |
| `has_raw_value` separates `null` from optimized-away/unavailable | ✅ unit-tested + live (`this` in a static method) |
| `get_members` — one level, paged, `total` / `truncated`, member expressions round-trip | ✅ automated live |
| `set_value` — assigns in the target, reads back, reports `compiler_error` | ✅ automated live |
| `add_watch` / `list_watches` / `remove_watch`; a failing watch does not fail the call | ✅ automated live |
| `list_modules` — `can_set_breakpoint` false for path-less modules | ✅ automated live |
| **Phase 6 closed out** — symbols, decompilation, text search, analysis, metadata, raw modules | ✅ 2026-08-04 (CorDebug + UCH) |
| `list_documents` / `list_types` / `list_members` — paged, tokens included | ✅ automated live |
| `search_symbols` — name to module + token, bounded | ✅ automated live |
| `get_il` — offsets, operands, and `is_sequence_point` per instruction | ✅ automated live |
| `get_csharp` — method and whole-type decompilation | ✅ automated live |
| `search_text` / `find_references` / `find_implementations` — bounded analysis with symbol identities | ✅ automated CorDebug + live UCH |
| `get_metadata` / `get_raw_module` — token facts and paged image with SHA-256 | ✅ automated CorDebug + live UCH |
| `set_breakpoint` by type + method name, same path as `set_il_breakpoint` | ✅ automated live |
| **Phase 7 closed out** — explicit invocation, memory, disassembly, capabilities, set-IP, hard func-eval timeout | ✅ 2026-08-04 (CorDebug) |
| `invoke_method` / `create_object` — separate side-effecting tools with audit ids | ✅ automated live |
| `read_memory` / `write_memory` — bounded target access; writes visibly side-effecting | ✅ automated live |
| `get_disassembly` — managed IL and CorDebug JIT-native blocks | ✅ automated live |
| `get_registers` — explicit `capability_unsupported` on this dnSpy contract | ✅ automated live failure contract |
| `set_instruction_pointer` — current-frame/method and engine validation, audited | ✅ automated live |

Test suites, all green:

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj   # 27 checks, wire + capability contract
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj     # 130 checks, access control + deadline bounds
dotnet test .\tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj # 15 checks, extension core
.\tests\run-milestone1-smoke.ps1                                       # 249 checks, end to end
```

All four suites are green as of the Phase 7 close-out on 2026-08-04: 27 / 130 / 15 unit
checks and 249 live smoke checks. The event-vocabulary work and complete Phase 6 surface were additionally
verified against live UCH — see
[DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md). **Phases 4 and 5 are verified on CorDebug only.**
Stepping, conditions, evaluation, and Phase 7 mutation/low-level operations have not been exercised against Mono/Unity, and Mono differs enough
elsewhere (sequence points, asynchronous frame fetch) that this is a real gap rather than a formality.

## Remaining gaps

### Partly addressed: frames can name a module that has no file

A UCH stack contained `data-000001BE153D3040` — an in-memory or dynamic module. **Metadata for such a
module is now reachable**: `DbgMetadataService` resolves it, so `get_csharp`, `get_il`, `list_types` and
`list_members` all work against it. **Breakpoints still cannot be set on one**, because dnSpy's code
location factory addresses modules by path. That is now an explicit `module_has_no_path` error and a
`can_set_breakpoint: false` flag on `list_modules` rather than a breakpoint that silently never binds.
Neither behaviour has been verified against a real file-less module — the CorDebug fixture has none, and
the only known instance is on a UCH stack.

### Correctness and safety

1. **Cancellation cannot abort in-flight work.** An expired deadline abandons the *wait*; a queued
   dispatcher callback or a started evaluation runs to completion, because dnSpy exposes no way to
   cancel either. Commented at both call sites in `ExtensionEntryPoint.cs`. This is the one Phase 1
   exit criterion that was amended rather than met; it is now advertised to callers as
   `limits.cancels_in_flight_work: false` instead of being left to be discovered. Revisit when
   func-eval makes evaluations long enough for the difference to bite.
2. **The `stale_handle` path is untested.** `DescribeFrame` rejects a snapshot whose frame closed
   mid-evaluation, but with `NoFuncEval` evaluations are milliseconds and the race cannot be triggered
   reliably. Revisit when func-eval makes evaluations long enough to manipulate.
3. **A connect failure leaves a modal dnSpy error dialog on screen.** dnSpy's own UI subscribes to
   `MessageUserMessage` and shows a message box. It runs on the UI thread, so it blocks neither the
   debugger dispatcher nor RPC — dgSpy keeps working around it — but nothing headless dismisses it, and
   they accumulate. dgSpy no longer *adds* to this: routine client disconnects used to go to
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

1. **Milestone 1 operations still share one partial `RpcHost`.** That intentionally preserves ownership
   of dnSpy dispatcher-bound objects, but physical responsibilities are now separated into `Rpc/`,
   `Debugger/`, `Events/`, and `Identity/`. Add Phase 3/4 families as focused
   `RpcHost.<Family>.cs` partials; if a family gains independently testable policy, extract that policy
   behind an interface rather than passing dnSpy objects through the transport layer.
2. **Gateway implements only the POST half of Streamable HTTP** — no `Mcp-Session-Id` handling, no
   SSE/GET. Fine for our client; a strict MCP client may object. `protocolVersion` is hardcoded.
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
  tests could have caught this. The rule is *sequence point*, not *offset 0* — `Thread.Sleep+0x1A` hits
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
  which posts `ModifyCore` back to the dispatcher *even when the caller is already on it*. Describing
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
  smoke cycle chasing a watch-evaluation "bug" whose JSON was correct all along.
- **Windows PowerShell 5.1 is .NET Framework**: no `RandomNumberGenerator.GetBytes(int)`, no
  `Convert.ToHexString`; `-match` against a collection returns matches rather than a boolean; and
  `ConvertFrom-Json '[]'` does not survive `.Count` checks. All three cost debugging cycles in the test
  harness — the smoke script has comments where each bit.

## Suggested order for the next session

Phases 0 through 3 are closed and Milestone 1's full vertical slice is delivered. The separate
dnSpy-window shutdown path remains a host-lifecycle concern: closing dnSpy with an attachment has been
observed to terminate the target, so callers must use `detach`.

0. **Exercise Phase 4 against UCH.** Stepping, conditions and hit counts are verified on CorDebug only.
   Mono's stepping is a different implementation and its sequence-point rule already bit breakpoints.
1. **Verify the file-less module path against UCH**, which is the only place a real one has been seen.
   Metadata should now resolve for it; `set_breakpoint` should refuse it with `module_has_no_path`.
2. Phase 7 or Phase 9, whichever the workflow needs first.

## Local PowerShell scratchpads

Reusable agent-side debugger scripts live in `ps_scratch/`. The directory is deliberately ignored by
Git because these scripts contain machine-local paths, ports, process ownership, and transient session
workflows; inspect and reuse them before writing another temporary RPC harness. Current helpers cover
starting the repo-built dnSpy, calling the extension RPC safely, listing/detaching sessions, and the
live UCH pause/call-stack/breakpoint-cursor check. Keep durable product tests in `tests/`, not here.
