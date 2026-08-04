# dnSpyEx synchronization

`batram/dnSpy` is the genuine GitHub fork used to track and contribute to `dnSpyEx/dnSpy`.

- `master` mirrors dnSpyEx and must not contain dgSpy changes.
- `dgspy` contains the dgSpy integration. Its first upstream baseline is dnSpyEx `3f4caa4f`.
- General fixes intended for dnSpyEx start from `upstream/master`, not from `dgspy`.

## Bring dnSpyEx changes into dgSpy

```powershell
git fetch upstream
git switch dgspy
git switch -c sync/dnspyex-YYYYMMDD
git merge upstream/master
```

Resolve conflicts by preserving the documented dgSpy boundaries: the headless host integration,
net48 extension compatibility, bounded `Mono.Debugger.Soft` fork, retained Mono/shared-debugger
orchestration, and engine-specific running-state behavior. Then run the current regression gate before
merging the synchronization branch into `dgspy`:

```powershell
.\tests\run-modernization-gate.ps1 -Stage Full
```

Use `Shared`, `CorDebug`, or `Unity` while iterating, but require `Full` for acceptance. The original
modernization acceptance record remains in [history](history/MODERNIZATION_GATE.md).

## Contribute a focused fix upstream

```powershell
git fetch upstream
git switch -c fix/description upstream/master
# implement and test the focused fix
git push -u origin fix/description
```

Open the pull request with `dnSpyEx/dnSpy:master` as its base. Do not use the `dgspy` branch for an
upstream pull request: its product-specific overlay is intentionally much larger than a reviewable fix.
