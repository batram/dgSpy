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

## dnlib 4.5.0 accepted 2026-08-04

`DnSpyCommon.props` now selects dnlib 4.5.0 and `dnSpy/dnSpy/app.config` redirects compatible 3.x-4.5
loads to assembly version 4.5.0. The deployed DLL reported file version 4.5.0.0. The isolated source
adaptations are the resource compatibility changes from dnSpyEx `f9ab0e16c`:

- binary serialized resources now pass `SerializationFormat.BinaryFormatter` explicitly;
- empty resource sets use `ResourceElementSet.CreateForResourceReader(module)` so their reader format
  is defined; and
- regenerated edited sets clone the original `ResourceElementSet`, preserving its format metadata.

No Roslyn, target-framework, debugger-engine, or other dependency version changed. The initial compile
was intentionally used to enumerate the old API call sites; no behavior was accepted as a silent empty
result. Acceptance commands and results:

```powershell
.\tests\run-modernization-gate.ps1 -Stage Shared
# full net48 host plus dgSpy build/deploy; Protocol 29, Gateway 184, Extension 18

.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild
# 368 checks: raw file/in-memory/dynamic modules, metadata, IL, C#, analyzer, export,
# SHA-256, paging, truncation, malformed requests, and engine/module identity

.\tests\run-modernization-gate.ps1 -Stage Unity -SkipHostBuild
# 15 checks: attach, Unity identity, file-backed metadata/IL/C#/search/raw-module,
# SHA-256, paging/truncation, file-less identity/raw-module, safe detach, target liveness
```

The live Unity fixture exposed both the required file-backed path and a file-less module. The latter
retained its Mono in-memory/dynamic identity and serialized to a hashed PE raw-module image. All
required checks passed with no accepted behavior changes and unchanged capability/tool snapshots.

## Roslyn 5.6 evaluator slice accepted 2026-08-04

This records the section 2.5 boundary before the later section 2.6 host adoption below.

The accepted source baseline is dnSpyEx `3f4caa4f`, with dependency update `1ee406cbd`,
`Roslyn.ExpressionCompiler` `e127e791` (5.6 source import `6e759dc` from Roslyn `c0573ed0`), and the
debugger custom-type-information contract from `f92d13a19`. The full `dnSpy/Roslyn` integration was
adopted as one coordinated source slice so the expression compiler, formatter/value-node layer, and
Roslyn editor assemblies all use package version 5.6.0.

The integration was adapted only where this fork's supported host boundary differs:

- `DnSpyRoslyn.props` retains `net48;net5.0-windows`, because this build passes the target framework
  globally and both the extension and current host remain supported;
- custom type information is propagated through aliases, expression results, and value-node creation
  without importing unrelated debugger-host changes;
- the new target-type-match glyph uses the existing `Reference` image, and the text-element call passes
  the existing string content-type contract; and
- the current MSBuild gate selects Visual Studio 18 when installed, supplies the installed dotnet SDK
  resolver path for that VS instance, and retains the VS 2019 fallback.

The dnSpyEx .NET 10 host target is intentionally omitted and deferred to section 2.6. The aligned
`Mono.Debugger.Soft` `d12451c` branch was also trialed and intentionally omitted. Its source still
required reapplying the three-second `GetFrames()` bound, and two live Unity Phase 8 attempts failed at
the temporary AppDomain unload: no `module_unloaded` event or module-breakpoint stop arrived, and the
unload did not complete. Restoring the durable bounded fork at `888ded0f` immediately restored all 45
checks, so no capability downgrade or Unity behavior regression was accepted.

Accepted evidence:

```powershell
.\tests\run-modernization-gate.ps1 -Stage Shared
# full net48 host plus dgSpy build/deploy; Protocol 29, Gateway 184, Extension 18

.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild
# 368 checks, including watches, assignment, invocation/construction, compiler errors,
# func-eval timeout/recovery, object IDs, frame selection, and endpoint reattach

.\tests\run-modernization-gate.ps1 -Stage Unity -SkipHostBuild
# 15 checks: bounded attach, metadata/IL/C#/raw module, safe detach, target liveness

.\tests\run-uch-phase8.ps1  # with an isolated dgSpy host on port 7351
# 45 checks: managed stop/frames, conditions, stepping, assignment, watches, invocation,
# construction, object IDs, exception/module breakpoints, continue, reattach, safe detach
```

Capability and MCP tool snapshots remained unchanged. The accepted slice therefore changes evaluator
implementation and type-information fidelity without changing advertised engine behavior or wire
contracts.

## dnSpyEx 6.6 host adopted 2026-08-04

The permanent host baseline is dnSpyEx snapshot `3f4caa4f`. Its coordinated host, contracts, UI,
decompiler, CorDebug, package, and submodule updates are adopted together, with `net48` and
`net10.0-windows` as supported host targets. The net48 build preserves dgSpy extension compatibility;
the self-contained net10 x64 publish proves the modern host target.

The following proven fork boundaries remain intentional:

- dgSpy's headless window-activation integration and extension sources;
- `Mono.Debugger.Soft` `888ded0f`, including bounded three-second frame retrieval;
- the pre-adoption Mono engine and shared-debugger orchestration, with only required dnSpyEx contract
  and API adaptations; and
- engine-specific dgSpy running-state handling: CorDebug uses selected/per-process state, while
  endpoint Mono retains manager aggregate state because its process flag can lag after a stop.

The aligned Mono library `d12451c` failed two 45-check Unity attempts at AppDomain unload. A later trial
of newer Mono engine/shared-debugger internals reproducibly failed seven Phase 8 checks after resume.
A generic per-process running-state adaptation reproduced those seven failures; making it CorDebug-only
restored Unity without losing CorDebug's running-target safety check.

Accepted evidence:

```powershell
.\tests\run-modernization-gate.ps1 -Stage Shared
# net48 host; Protocol 29, Gateway 184, Extension 18

.\build.ps1 net-x64 -NoMsbuild
# self-contained net10.0-windows x64 publish

.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild
# 368 checks

.\tests\run-modernization-gate.ps1 -Stage Unity -SkipHostBuild
# 15 checks

.\tests\run-uch-phase8.ps1  # isolated host on port 7351
# 45 checks
```
