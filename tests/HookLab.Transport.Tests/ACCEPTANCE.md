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

The harness is not built by `build-dgspy.ps1` or the gate. Build it first from a clean tree, or the
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
