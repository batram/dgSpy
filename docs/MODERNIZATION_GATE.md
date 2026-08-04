# Modernization regression gate

Run modernization work on a dedicated branch. The gate checks output-locking processes before builds,
builds/deploys in the documented order, runs the three unit suites sequentially, and keeps shared,
CorDebug, and Mono/Unity failures distinct.

```powershell
.\tests\run-modernization-gate.ps1 -Stage Shared
.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild
.\tests\run-modernization-gate.ps1 -Stage Unity -SkipHostBuild
```

`Full` runs both live engines after the shared gate. `Unity` starts an isolated hidden dnSpy host and
always uses safe `detach`; its modernization subset is attach, file-backed module selection, metadata,
IL, C#, bounded decompiled-text search, detach, and target-liveness verification.

## Required fixtures by dependency stage

| Stage | Pre-change fixture and contract | Pass/fail command |
|---|---|---|
| ILSpy-v2 / NRefactory | `Milestone1Target` ordinary async, parameterless generic extension, nested namespaced type; malformed-property patch inspection; existing file-backed, in-memory, and dynamic decompilation | CorDebug and Unity commands above |
| dnlib | Existing deferred, in-memory, dynamic, raw-module, metadata identity, paging, truncation, and SHA-256 checks | `-Stage CorDebug`; `-Stage Unity` |
| Roslyn/evaluator | Existing valid/broken watches, assignment, invocation, construction, hard timeout/recovery, and safe detach | `-Stage CorDebug`; full `run-uch-phase8.ps1` when that stage begins |

`tests/dgSpy.Gateway.Tests/Snapshots` captures the complete MCP `tools/list` schemas and static
`get_capabilities` contract. Snapshot failures are wire-contract drift; live fixture failures are
behavior/output drift. Update snapshots only after review of an intentional contract change:

```powershell
.\tests\run-modernization-gate.ps1 -Stage Shared -UpdateSnapshots
```

## Decompiler batch accepted 2026-08-04

The selected ILSpy branch is `batram/ILSpy:dgspy-modernization` at `8f6c0812`. It is based on the
verified `3fa5701e` pin and contains three bounded ports:

- dgSpy `27bf49c7`, adapted from dnSpyEx `0b052bb5`: runtime-async return handling without taking the
  newer dnlib line. The exact `MethodImplAttributes.Async` metadata shape is observational because the
  pinned net48 C# compiler cannot emit it; the ordinary async method remains an executable no-crash
  compatibility probe.
- dnSpyEx `a5dd6d5b` (cherry-picked as `cc5ec509`): generic extension methods without parameters.
- dnSpyEx `68b487ee` (cherry-picked as `8f6c0812`): malformed property definitions no longer hit two
  null dereferences. Invalid property-table metadata is observational here because the supported C#
  compiler cannot produce it without adding a metadata-corruption generator.

The NRefactory URL now resolves through `batram/NRefactory:dgspy-modernization` at the existing
`79d99d6f` pin. Advancing NRefactory alone failed against old ILSpy's VB visitor, while the dnSpyEx
ILSpy head required dnlib APIs reserved for section 2.4. Stack-safety and variable-naming ports also
require intervening prerequisites, so they were not silently mixed into this batch.

Accepted evidence: full net48 build; dgSpy build/deploy; Protocol 29, Gateway 184, Extension 18; CorDebug
368 checks; Unity read-only modernization subset 9 checks; unchanged capability and MCP tool snapshots.
