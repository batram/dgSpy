# T05 transport acceptance evidence

Recorded 2026-08-09 on Windows x64. The executable in every integrity case was:

`C:\Users\mjb\develop\UCH-dev\dgSpy\tests\HookLab.Transport.Tests\Harness\bin\Release\net48\HookLab.Transport.Harness.exe`

The harness measures token elevation with `GetTokenInformation(TokenElevation)` and validates the
target using `PROCESS_QUERY_LIMITED_INFORMATION`, process creation time, and
`QueryFullProcessImageName`. It does not trust the requested test label.

| Case | Measured target | Measured client | Target PID | Result |
|---|---|---|---:|---|
| same integrity | medium | medium | 54748 | pass |
| elevated host to medium target | medium | elevated | 50752 | pass |
| medium host to elevated target | elevated | medium | 13996 | pass |

The harness is not built by the shipping pipeline or the gate. Build it first from a clean tree, or the
script throws "Harness not built":

```powershell
dotnet build tests\HookLab.Transport.Tests\Harness\HookLab.Transport.Harness.csproj -c Release
```

Run or repeat the matrix from a normal user PowerShell with:

```powershell
powershell -NoProfile -File tests\HookLab.Transport.Tests\run-integrity-matrix.ps1
```

The two cross-integrity cases produce UAC prompts. Each target self-terminates after a fixed bound.

## Different-account DPAPI refusal

`RYZ\CodexSandboxOffline` protected the exact bytes `cross-account-secret` with
`DataProtectionScope.CurrentUser` and the discovery-record entropy. `RYZ\mjb` then attempted to
unprotect the resulting blob. Windows refused with:

`System.Security.Cryptography.CryptographicException: Key not valid for use in specified state.`

This is separate from the record ACL test: the focused suite also proves that discovery records have
protected, non-inherited ACLs containing only the writing user's SID.

## Secret rotation: what it provides

Rotation derives the next secret as
`HMAC-SHA256(current, "rotate1" || serverChallenge || clientChallenge)`, and both challenges travel in
the clear. That one-way ratchet means a captured secret reveals nothing about earlier ones, but it does
not self-heal: an attacker holding one secret who can also observe handshakes derives every successor,
so rotation never locks them out. Reading pipe traffic already requires same-user access, which this
design does not claim to defend against, and a discovery record leaked on its own cannot be advanced
without the challenges. Recorded so rotation is not later cited as a mitigation for a disclosed secret.

## Bounded endpoint teardown, and the two defects behind it (2026-08-10)

`ProbePipeServer.Dispose()` used to block up to two seconds on its listener thread. Three defects, all
measured against the real assembly on net48 x64, not reasoned from the source:

- **The wait was reachable and common, not a corner case.** `activePipe` was published *after*
  `CreatePipe()` returned, so a `Dispose` landing in that window disposed nothing and left the listener
  parked in `WaitForConnection()` on a pipe nobody would ever connect to. 180 of 400 rapid
  construct-then-dispose cycles wedged for the full two seconds.
- **A wedged teardown could kill the debuggee.** On the timeout path `stopped.Dispose()` ran while the
  listener was still alive. Waking that listener afterwards made its `finally { stopped.Set(); }` throw
  `ObjectDisposedException` out of a thread delegate, which on .NET Framework terminates the process -
  reproduced end to end from `ProbePipeServer.Listen`, exit code `0xE0434352`.
- **No client connect is needed to wake a parked listener.** Disposing the server stream releases a
  thread already parked in `WaitForConnection()` in 0 ms with `IOException: The pipe has been ended`. A
  bounded `NamedPipeClientStream.Connect(50)` also works, in 0 ms, but is redundant once the pipe is
  published under a gate, so it is deliberately not in the fix.

`Dispose` now closes the endpoint under `pipeGate` and returns without waiting and without disposing
`stopped`; `WaitForShutdown(int)` exists for tests only. Same sweep after the fix: worst dispose 2 ms
over 400 cycles, zero wedged listeners, zero endpoints still connectable.

## The admission window inside Dispose, and the bound Cancel could take away (2026-08-10)

Second iteration of the same defect in the same method. `946f8f307` closed the window in which a
request decoded before a shutdown could still be admitted, by making admission and the gate close one
decision under one lock - but `Dispose` closed the gate *last*, after disposing the pipe and
cancelling, so the window survived at a smaller size: a listener holding a decoded request could reach
`TryEnterCommand`, find the gate open, and enter a handler that need not observe cancellation, while
`Dispose` was still running and after it returned. `TryQuiesce` was never affected - it closes the gate
and then waits, so it observes anything it admitted.

The final ordering, and why each step is where it is:

1. `CloseCommandGate()` - **first**, because it is the only step that cannot be undone by a later one
   and the only one that decides admissions. Every admission racing this shutdown now resolves as a
   refusal, whether the listener asks before, during or after the call. One lock acquisition, against
   critical sections that touch a counter and an event, so the non-blocking contract is unchanged.
2. Dispose the published pipe under `pipeGate` - unchanged, including the publication gate in `Listen`
   that fixed 180 of 400 wedged cycles.
3. `CancelCommands()` - still strictly after the endpoint is closed, so a handler woken by cancellation
   still cannot be handed a connection made in between. Moving the gate close ahead of both cannot
   reintroduce that: closing the gate only removes admissions, it never creates one.

Separately, `CancellationTokenSource.Cancel()` runs registered callbacks inline on the calling thread,
so a handler registering blocking cleanup owned both bounds in this class - `Dispose`, whose caller may
be the target's own thread inside a func-eval, and `TryQuiesce`, which would overrun the timeout its
caller chose before reaching the wait that timeout describes. The Cancel now runs on a thread the class
owns, started once. Delivery is therefore asynchronous; nothing decides anything from the token's
state, and `TryQuiesce` still answers from `commandsIdle`, so a late delivery yields the conservative
"still in flight" answer and never a false quiesced. The two bounds stay different promises: `Dispose`
does not wait at all, `TryQuiesce(n)` may wait up to n.

Measured on net48 x64 against the real assembly, harness rebuilt for each variant:

| Variant | `dispose-window` | `cancel-callback` | `dispatch-gate` | `quiescence` | `dispose-bounded` |
|---|---|---|---|---|---|
| Fixed | pass | pass, dispose 0 ms, quiesce 251 ms | pass | pass | worst 2 ms, 0 wedged |
| Gate closed last (the defect) | **fail, exit 82, iteration 0** - handler ran before `Dispose` returned | pass | pass | pass | worst 2 ms |
| Cancel inline on the caller's thread | pass | **fail, exit 91, dispose 3005 ms** | pass | pass | - |
| Never cancel at all | pass | **fail, exit 92** - callback never ran | **fail, exit 44** | - | - |

Row 2 is the measured form of "the pre-existing tests structurally cannot see this": `dispatch-gate`
case 3 releases its listener only after `Dispose` has returned, so the gate is necessarily closed by
then and the case passes under either ordering. `dispose-window` releases the listener from inside
`Dispose`, through an internal `ShutdownProbeForTest` seam placed between the pipe disposal and the
cancellation, and waits for the handler rather than sleeping, so a `Dispose` that still admits the
request loses the race deterministically. Rows 3 and 4 separate "bounded" from "skipped": the bound
alone would also be satisfied by never cancelling, which is why `cancel-callback` requires the callback
to have run, and to have run on a thread other than the disposing one.

Neither new mode calls `TryQuiesce` before its assertion, deliberately: `TryQuiesce` closes the gate
and cancels, and a single call in the wrong place supplies exactly the property being asserted. That
mistake has made an assertion in this file vacuous twice.

Transport 19/19 and Bootstrap 46/46 after the fix. Not established: no live CorDebug run - both new
modes exercise the endpoint in the harness process only, never against a real debuggee, and no test
covers a handler that both registers a blocking callback and mutates a target.

## Host-injected endpoint secret

The host may generate the 32-byte secret and pass it to `ProbePipeServer` rather than receiving one
back, so the credential never has to travel outward from the target into a report string, an activity
log, an action record, the RPC wire, or an MCP transcript. There is no redaction facility in the tree,
so not emitting it is the only available answer. Self-generation stays the default and every
pre-existing caller is unaffected. The pipe name and endpoint nonce still travel outward and are not
credentials: the nonce is only an HMAC input, and without the secret it does not let anyone compute a
proof.

## Harness incidents

The first integrity run exposed two harness defects, not transport defects:

- The net48 harness initially measured elevation through `WindowsPrincipal.IsInRole`, which threw
  `SecurityException`; it now reads `TokenElevation` directly.
- The first reverse-direction run validated an elevated target with `Process.MainModule`, was denied
  across the integrity boundary, and quarantined the record. It now uses limited-information process
  queries. Its cleanup also attempted to stop an elevated target from a medium parent; elevated targets
  now self-terminate instead.

The corrected cases above were rerun and passed.
