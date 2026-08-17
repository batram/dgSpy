# Delivered local debugger core

This is the permanent completion record for the first implementation section, formerly Phases 0–8 of
`IMPLEMENTATION_PLAN.md`. It was completed on 2026-08-04. Detailed per-tool evidence is recorded in
[DGSPY_STATUS.md](DGSPY_STATUS.md); current operational semantics are recorded in
[the tool reference](../reference/DGSPY_REFERENCE.md).

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

## Verified completion

- CorDebug is the complete automated reference path for the delivered local surface.
- Mono/Unity has live verification for attach, lifecycle, stacks/locals, breakpoints and all step kinds,
  evaluation/assignment/watches, invocation/construction, memory, disassembly capability behavior, set-IP,
  decompilation, metadata, analysis, object IDs, Autos, exports, policies, output, module-unload and
  categorized-exception stops, detach cleanup, reparse refusal, and safe detach.
- The completed deployment is the documented local x64, single-host debugger core.
- Unsupported engine features return explicit capability results rather than empty successful responses.

## Evidence baseline

The current baseline records 29 Protocol checks, 182 Gateway checks, 18 Extension checks, and 365
CorDebug live-smoke checks. Mono/Unity evidence is recorded separately because it uses a live game
fixture and engine behavior differs materially.

The completion evidence has subsequently been refreshed as the contracts gained coverage; the current
counts above supersede the earlier 29 / 174 / 16 / 363 documentation-reorganization snapshot.

## Retained compatibility patches

The delivered baseline contains two deliberate upstream-source fixes documented in
[the build baseline](../reference/DGSPY_BASELINE.md): preservation of thread exit codes and a bounded Unity
`ThreadMirror.GetFrames()` wait.
