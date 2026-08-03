# dgSpy status and handoff

Last updated 2026-08-03. Branch `master`; **all dgSpy work is uncommitted** (`IMPLEMENTATION_PLAN.md`,
`build-dgspy.ps1`, `docs/`, `tests/`, `dgSpy.Protocol/`, `dgSpy.Gateway/`, `Extensions/dgSpy.Extension/`).

Scope in force: x64 only, .NET Framework CorDebug (`CLR v4.0.30319`), plus the Mono/Unity path for UCH.
CoreCLR and x86 are out. See [DGSPY_BASELINE.md](DGSPY_BASELINE.md) for the toolchain and thread rules,
[DGSPY_MILESTONE1.md](DGSPY_MILESTONE1.md) for the tool surface, `IMPLEMENTATION_PLAN.md` for the roadmap.

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
.\tests\run-milestone1-smoke.ps1                                       # 38 checks, end to end
```

## Remaining gaps

### Blocks the UCH goal

1. **`attach_endpoint` does not exist.** A target launched with
   `--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n` emits no
   multicast beacon, so no attach provider enumerates it and `list_programs` can never reach it. Needs
   `UnityAttachToProgramOptions { Address, Port }` passed to `DbgManager.Start`. Planned in Phase 2.
2. **The Mono/Unity engine is completely unexercised.** Every verified item above is CorDebug only.
   This is an unmet Phase 0 exit criterion, not merely future work.

### Correctness and safety

3. **Cancellation cannot abort in-flight work.** An expired deadline abandons the *wait*; a queued
   dispatcher callback or a started evaluation runs to completion, because dnSpy exposes no way to
   cancel either. Commented at both call sites in `ExtensionEntryPoint.cs`. Phase 1 asks for more than
   this delivers.
4. **The `stale_handle` path is untested.** `DescribeFrame` rejects a snapshot whose frame closed
   mid-evaluation, but with `NoFuncEval` evaluations are milliseconds and the race cannot be triggered
   reliably. Revisit when func-eval makes evaluations long enough to manipulate.
5. **`faulted` is inferred from a timeout**, not from a dnSpy attach-failure message. A genuinely slow
   attach and a failed attach are indistinguishable after 10 s.
6. **Single global session.** One `sessionId` field, one target. `host_id` routing and multi-session
   ownership are Phase 9; nothing in the code anticipates them.

### Quality and structure

7. **`ExtensionEntryPoint.cs` is one flat file** (~200 dense lines) against the plan's
   `Debugger/ Rpc/ Events/ Handles/` layout. Worth splitting before it grows further.
8. **No `dgSpy.Extension.Tests`.** All extension logic is covered only through the smoke script, which
   needs a real dnSpy and a real target. Extraction of the pure logic (state computation, identity
   composition, event cursor) would make it unit-testable.
9. **Gateway implements only the POST half of Streamable HTTP** — no `Mcp-Session-Id` handling, no
   SSE/GET. Fine for our client; a strict MCP client may object. `protocolVersion` is hardcoded.
10. **`dnSpy\dnSpy\bin\Release\net48` contains a nested `bin\bin`** from `build.ps1` having run more
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

1. `attach_endpoint` + a manual UCH checklist — unblocks the actual goal (gaps 1, 2).
2. Commit the current work; it is all untracked.
3. Split `ExtensionEntryPoint.cs` and add `dgSpy.Extension.Tests` before the tool surface grows (7, 8).
4. Then Phase 3/4 proper: full event stream, breakpoint management (`list`/`update`/`remove`), stepping.
