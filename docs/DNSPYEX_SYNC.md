# dnSpyEx synchronization

`batram/dgSpy` is a genuine GitHub fork of `dnSpyEx/dnSpy`, used both to track upstream and to
contribute back. (It was created as `batram/dnSpy` and later renamed; the old URL still redirects.)

- `master` mirrors dnSpyEx and must not contain dgSpy changes.
- `dgspy` carries the dgSpy work. Its upstream baseline is dnSpyEx `3f4caa4f`, recorded in
  `tools/upstream-baseline.txt`.
- Fixes intended for dnSpyEx start from `upstream/master`, never from `dgspy`.

## Read this first

On 2026-08-04 an "integration overlay" commit, `399ed7297`, set this tree to the exact content of
an older dgSpy tree. Its stated success criterion was that the resulting tree hash matched that
older commit -- which, since the older tree was plain dnSpy plus a partial 6.6 snapshot,
*guaranteed* that every dnSpyEx improvement the snapshot lacked was discarded. 57 shared files
were reverted to a pre-dnSpyEx state.

Nothing caught it. The code compiled, and the gate only exercises dgSpy's own MCP surface, never
dnSpyEx's UI. It surfaced weeks later as a Debug Program dialog that offered only the two Unity
engines and could not start a CorDebug session at all, because a deleted MEF export took both
.NET pages down with it.

Two rules follow, and they matter more than the commands below:

- **Never synchronize by overlay, tree copy, or bulk file sync.** Making one tree's hash equal
  another's is not integration; it silently discards everything the target had that the source
  lacked. Upstream changes come in as a merge. dgSpy changes to shared files land as individual,
  reviewable commits.
- **Take whole upstream commits.** A dnSpyEx feature typically spans an interface in
  `dnSpy.Contracts.*`, its `[Export]`ed implementation in an extension, resource strings in both
  `Properties\*.resx` *and* the generated `*.Designer.cs`, and `ContentTypeDefinition` exports in a
  `ContentTypeDefinitions.cs`. Dropping any one of those compiles cleanly and then fails at
  runtime, or composes away in silence. That is exactly how the dialog broke.

## Bring dnSpyEx changes into dgSpy

```powershell
git fetch upstream
git switch dgspy
git switch -c sync/dnspyex-YYYYMMDD
git merge upstream/master
```

**Resolving conflicts.** The default answer for a file that dnSpyEx owns is *upstream wins*.
dgSpy's divergence is meant to be additive and small; `tools/upstream-drift-allowlist.txt` is the
authoritative list of files where we intentionally differ, with a reason for each. If a conflicted
file is not in that list, you should be taking the upstream side. If it is, the reason recorded
there tells you what to preserve.

**Then update the baseline and the allowlist in the same change.** Put the new upstream commit in
`tools/upstream-baseline.txt` and re-check the allowlist: a merge can legitimately resolve some
drift, and can just as easily hide new drift behind an entry that already exists.

```powershell
powershell -NoProfile -File tools\check-upstream-drift.ps1 -Report
```

## Verifying a sync

The automated gate is necessary and not sufficient.

```powershell
.\tests\run-modernization-gate.ps1 -Stage Full
```

Use `Shared`, `CorDebug`, or `Unity` while iterating; require `Full` for acceptance. The original
modernization acceptance record is in [history](history/MODERNIZATION_GATE.md).

What the gate does **not** cover is the failure mode described above. A part whose imports cannot
be satisfied does not exist, nothing is logged, and everything that imported it disappears too.
So also:

- run the composition test, which asserts that the parts most easily lost are actually present:
  ```powershell
  dotnet test tests\dgSpy.Composition.Tests\dgSpy.Composition.Tests.csproj -c Release
  ```
- open the built dnSpy and look at it. `tools\gui-inspect.ps1` drives the GUI over UI Automation
  so this can be done without a human at the keyboard; at minimum confirm the Debug Program dialog
  still offers .NET Framework, .NET, Unity and Unity (Connect).

## Outstanding divergence

`tools/upstream-drift-allowlist.txt` distinguishes deliberate divergence from tracked debt. The
entries marked `BATCH B` and `BATCH C` are unrepaired damage from `399ed7297`, not decisions --
28 Mono files awaiting a live Unity target, two files where a dgSpy hook and collateral share a
source file, and three files the overlay reinstated at paths dnSpyEx had moved away from. Removing
an entry there should mean the file was restored, not re-listed.

## Contribute a focused fix upstream

```powershell
git fetch upstream
git switch -c fix/description upstream/master
# implement and test the focused fix
git push -u origin fix/description
```

Open the pull request against `dnSpyEx/dnSpy:master`. Do not use `dgspy` as the PR branch: its
product overlay is intentionally far larger than a reviewable fix.
