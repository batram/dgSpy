# dgSpy status and handoff

Last updated 2026-08-03. Branch `dgspy-mcp-milestone-1`.

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
| `get_callstack` — method names, frame identity, primitive locals | ✅ verified |
| Gateway `Origin` validation + `X-dgSpy-Token`, fails closed | ✅ verified |
| Evaluation off the dispatcher (`EvaluationQueue`) | ✅ built and regression-tested, benefit not directly observable |
| Response serialization off the dispatcher | ✅ built, not directly observable |
| Extension split into entry point, RPC, debugger, events, and identity boundaries | ✅ built |
| `dgSpy.Extension.Tests` identity, state, and event-cursor coverage | ✅ 11 tests |

Test suites, all green:

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj   # 15 checks, wire + capability contract
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj     # 51 checks, access control + deadline bounds
dotnet test .\tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj # 11 checks, extension core
.\tests\run-milestone1-smoke.ps1                                       # 89 checks, end to end
```

## Remaining gaps

### Deferred Phase 6 limitation

**Frames can name a module that has no file.** A UCH stack contained
   `data-000001BE153D3040` — an in-memory or dynamic module — and `set_il_breakpoint` takes a module
   *path*, so no breakpoint can be set on such a frame. This does not block the current UCH debugger
   workflow: file-backed modules work. Phase 6 owns navigation and breakpoint identity for in-memory
   module images.

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
- **Windows PowerShell 5.1 is .NET Framework**: no `RandomNumberGenerator.GetBytes(int)`, no
  `Convert.ToHexString`; `-match` against a collection returns matches rather than a boolean; and
  `ConvertFrom-Json '[]'` does not survive `.Count` checks. All three cost debugging cycles in the test
  harness — the smoke script has comments where each bit.

## Suggested order for the next session

Phases 0 and 1 are closed. Phase 2 is largely covered by the attach/detach work already verified;
`launch`, `terminate`, and `restart` remain unimplemented, as does dnSpy's own shutdown path.

1. Phase 3/4 proper: full event stream, breakpoint conditions and hit counts, stepping. Follow
   `Extensions/dgSpy.Extension/README.md` and add each family in its owning partial file.

## Local PowerShell scratchpads

Reusable agent-side debugger scripts live in `ps_scratch/`. The directory is deliberately ignored by
Git because these scripts contain machine-local paths, ports, process ownership, and transient session
workflows; inspect and reuse them before writing another temporary RPC harness. Current helpers cover
starting the repo-built dnSpy, calling the extension RPC safely, listing/detaching sessions, and the
live UCH pause/call-stack/breakpoint-cursor check. Keep durable product tests in `tests/`, not here.
