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

## Harness incidents

The first integrity run exposed two harness defects, not transport defects:

- The net48 harness initially measured elevation through `WindowsPrincipal.IsInRole`, which threw
  `SecurityException`; it now reads `TokenElevation` directly.
- The first reverse-direction run validated an elevated target with `Process.MainModule`, was denied
  across the integrity boundary, and quarantined the record. It now uses limited-information process
  queries. Its cleanup also attempted to stop an elevated target from a medium parent; elevated targets
  now self-terminate instead.

The corrected cases above were rerun and passed.
