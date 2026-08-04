# Delivered local debugger core

This is the permanent completion record for the first implementation section, formerly Phases 0–8 of
`IMPLEMENTATION_PLAN.md`. It was completed on 2026-08-04. Detailed per-tool evidence and known gaps remain
in [DGSPY_STATUS.md](DGSPY_STATUS.md); operational semantics remain in
[DGSPY_REFERENCE.md](DGSPY_REFERENCE.md).

## Delivered capability groups

1. **Foundation and local transport** — repeatable net48 extension build/deploy, MEF composition,
   dispatcher-safe scheduling, typed RPC, loopback-only extension transport, MCP gateway origin/token
   checks, discovery, host information, and capability advertisement.
2. **Lifecycle and events** — discover, attach, endpoint-attach, launch, pause, continue, restart, safe
   detach, explicit terminate, session recovery, normalized bounded events, non-destructive waits, terminal
   cleanup, and multi-process selection within the single logical session.
3. **Breakpoints and execution control** — IL and name-based code breakpoints, conditions, hit counts,
   tracepoints, exception policies, module load/unload breakpoints, breakpoint interchange, stepping, and
   state-version guards.
4. **Inspection and evaluation** — threads, frames, arguments/locals, expression evaluation, one-level
   member expansion, assignment, watches, Autos, persistent object IDs, invocation/construction, value
   export, and bounded debugger output.
5. **Code and metadata navigation** — documents, modules, types, members, metadata, IL, C# decompilation,
   raw module images, symbol/text search, references, implementations, and typed analyzer relationships.
6. **Low-level operations** — bounded memory read/write, managed and native disassembly where supported,
   instruction-pointer changes with validation, explicit unsupported-register behavior, audit IDs, and
   hard engine func-eval timeouts where exposed by dnSpy.

## Completion boundary

- CorDebug is the complete automated reference path for the delivered local surface.
- Mono/Unity has live verification for attach, lifecycle, stacks/locals, breakpoints, decompilation,
  metadata, analysis, object IDs, Autos, exports, policies, output, and safe detach.
- Some unsafe or fixture-dependent Mono scenarios remain verification gaps: actual module-unload and
  categorized-exception stops, teardown-driven object-ID cleanup, Phase 7 mutation/low-level operations,
  and a file-less module appearing in a Mono frame. These are not silently claimed as verified.
- `get_registers` intentionally returns `capability_unsupported`; the pinned dnSpy public contracts expose
  no register service.
- In-flight dispatcher work cannot generally be cancelled. The client deadline is bounded and the
  limitation is advertised as `cancels_in_flight_work: false`.
- The delivered deployment is local and single-host. Stable remote hosts, multi-client authorization,
  encrypted transport, and session ownership are not part of this completed section.

## Evidence baseline

The completion run recorded 29 Protocol checks, 174 Gateway checks, 16 Extension checks, and 363
CorDebug live-smoke checks. Mono/Unity evidence is recorded separately because it uses a live game
fixture and engine behavior differs materially.

The documentation-reorganization sanity check passed on 2026-08-04: Protocol 29/29, Gateway 174/174,
and Extension 16/16. It does not replace the archived 363-check live evidence; dnSpy and UCH were not
relaunched solely for a documentation edit.

## Retained compatibility patches

The delivered baseline contains two deliberate upstream-source fixes documented in
[DGSPY_BASELINE.md](DGSPY_BASELINE.md): preservation of thread exit codes and a bounded Unity
`ThreadMirror.GetFrames()` wait. The latter is a local submodule commit and must be preserved or reapplied
during dependency modernization until equivalent behavior is proven upstream.
