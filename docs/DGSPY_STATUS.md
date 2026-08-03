# dgSpy status and handoff

Last updated 2026-08-03. Branch `dgspy-mcp-milestone-1`; the milestone 1 slice is committed as
`f1e9e35a1`.

Scope in force: x64 only, .NET Framework CorDebug (`CLR v4.0.30319`), plus the Mono/Unity path for UCH.
CoreCLR and x86 are out. See [DGSPY_BASELINE.md](DGSPY_BASELINE.md) for the toolchain and thread rules,
[DGSPY_MILESTONE1.md](DGSPY_MILESTONE1.md) for the tool surface,
[DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md) for the manual Mono/Unity pass,
`IMPLEMENTATION_PLAN.md` for the roadmap.

## Done and verified

Verified means exercised end to end against a real dnSpy and a real target, not just compiled.

| Capability | Status |
|---|---|
| MEF extension loads in x64 net48 dnSpy, logs version | ✅ verified |
| Loopback TCP RPC, versioned, structured errors | ✅ verified |
| One-command build + deploy (`build-dgspy.ps1`) | ✅ verified |
| `list_programs`, incl. `process_ids` / `process_names` filtering | ✅ verified (2454 ms → 55 ms) |
| `program_id` from typed fields; `runtime_guid` surfaced | ✅ verified |
| `attach` — waits for threads, refuses a second session | ✅ verified |
| `attach_endpoint` — argument validation and failure path | ✅ verified (faults in ~2 s with dnSpy's own reason) |
| `attach_endpoint` — connecting to a live Mono/Unity endpoint | ✅ **verified against UCH** (1120 ms; reattach 415 ms) |
| Mono/Unity pause, call stack, primitive locals, detach | ✅ verified against UCH; detach leaves the game running |
| `set_il_breakpoint` reports `bound` / `severity` / `message` | ✅ verified both engines |
| Mono sequence-point snapping (`snapped`, `warning`) | ✅ verified against UCH |
| `list_breakpoints`, `clear_breakpoints` | ✅ verified (CorDebug smoke + UCH) |
| `cursor_event_id` — cursor sampled before the breakpoint exists | ✅ verified against UCH |
| Unity `get_callstack` thread probe — picks a thread with frames | ✅ verified against UCH (landed on the UI thread) |
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

Test suites, all green:

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj   # 8 checks, wire contract
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj     # 13 checks, access control
.\tests\run-milestone1-smoke.ps1                                       # 54 checks, end to end
```

## Remaining gaps

### Blocks the UCH goal

1. **Frames can name a module that has no file.** A UCH stack contained
   `data-000001BE153D3040` — an in-memory or dynamic module — and `set_il_breakpoint` takes a module
   *path*, so no breakpoint can be set on such a frame. Phase 6's in-memory module image is the fix.
2. **No `remove_breakpoint`.** `clear_breakpoints` is all-or-nothing, and it also removes breakpoints
   set by hand in dnSpy's UI, because dnSpy keeps one global collection and dgSpy does not own a
   subset of it.
3. **Thread selection is still not the caller's.** `get_callstack` now probes for a thread that has
   managed frames instead of taking the first one blindly — which is what made Unity stacks usable —
   but there is no way to ask for a particular thread, and which thread you get is incidental.
   `list_threads` and a thread argument are Phase 5.

### Correctness and safety

4. **Cancellation cannot abort in-flight work.** An expired deadline abandons the *wait*; a queued
   dispatcher callback or a started evaluation runs to completion, because dnSpy exposes no way to
   cancel either. Commented at both call sites in `ExtensionEntryPoint.cs`. Phase 1 asks for more than
   this delivers.
5. **The `stale_handle` path is untested.** `DescribeFrame` rejects a snapshot whose frame closed
   mid-evaluation, but with `NoFuncEval` evaluations are milliseconds and the race cannot be triggered
   reliably. Revisit when func-eval makes evaluations long enough to manipulate.
6. **A connect failure leaves a modal dnSpy error dialog on screen.** dnSpy's own UI subscribes to
   `MessageUserMessage` and shows a message box. It runs on the UI thread, so it blocks neither the
   debugger dispatcher nor RPC — dgSpy keeps working around it — but nothing headless dismisses it, and
   they accumulate. dgSpy no longer *adds* to this: routine client disconnects used to go to
   `WriteMessage(ErrorUser, …)`, which is dnSpy's message-box channel, and the gateway opens a fresh
   connection per request. Those are now silent, with real faults going to `Output`.
7. **Single global session.** One `sessionId` field, one target. `host_id` routing and multi-session
   ownership are Phase 9; nothing in the code anticipates them.

### Quality and structure

8. **`ExtensionEntryPoint.cs` is one flat file** (~230 dense lines) against the plan's
   `Debugger/ Rpc/ Events/ Handles/` layout. Worth splitting before it grows further.
9. **No `dgSpy.Extension.Tests`.** All extension logic is covered only through the smoke script, which
   needs a real dnSpy and a real target. Extraction of the pure logic (state computation, identity
   composition, event cursor) would make it unit-testable.
10. **Gateway implements only the POST half of Streamable HTTP** — no `Mcp-Session-Id` handling, no
   SSE/GET. Fine for our client; a strict MCP client may object. `protocolVersion` is hardcoded.
11. **Per-tool gateway deadlines are a hardcoded table** (`ToolCatalog.DeadlineSeconds`). They exist
   because the gateway's old flat 8 s was shorter than the extension's own 10 s attach wait, so a
   successful attach could be abandoned by the caller. The extension should advertise its bound rather
   than the gateway guessing it.
12. **`dnSpy\dnSpy\bin\Release\net48` contains a nested `bin\bin`** from `build.ps1` having run more
    than once. Harmless, pre-existing, confusing when hunting deploy problems.

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
- **`DbgManager.Start` returning null does not mean the target is connected.** It only means an engine
  was built. A Mono connect then retries the socket for the whole connection timeout and reports
  failure through `MessageUserMessage`, not through the `Start` return value.
- **Unity's attach providers are skipped unless named explicitly.** `UnityPlayerAttachProgramOptionsProviderFactory.Create(allFactories)`
  returns null when `allFactories` is true, so an unfiltered `list_programs` never runs multicast
  discovery — the ~2.4 s cost is the CorDebug providers probing every process, not Unity.
- **The extension needs `Newtonsoft.Json.dll` beside it**; dnSpy does not ship it, and a missing
  dependency makes dnSpy drop the extension silently.
- **A stale extension copy in dnSpy's bin root is composed twice.** `build-dgspy.ps1` removes them.
- **Windows PowerShell 5.1 is .NET Framework**: no `RandomNumberGenerator.GetBytes(int)`, no
  `Convert.ToHexString`; `-match` against a collection returns matches rather than a boolean; and
  `ConvertFrom-Json '[]'` does not survive `.Count` checks. All three cost debugging cycles in the test
  harness — the smoke script has comments where each bit.

## Suggested order for the next session

1. One clean UCH run to verify the thread probe and `cursor_event_id` (gap 1). Relaunch the game first;
   the endpoint is single-use.
2. `remove_breakpoint` (gap 2), then Phase 5's `list_threads` plus a caller-chosen thread (gap 3).
3. Split `ExtensionEntryPoint.cs` and add `dgSpy.Extension.Tests` before the tool surface grows (8, 9).
4. Then Phase 3/4 proper: full event stream, breakpoint conditions and hit counts, stepping.
