# Dream of roads

This is dgSpy's ordered roadmap for open product work as of 2026-08-17. It is not a release promise.
The current product already includes the verified x64 CLR v4 and Mono/Unity debugger bridge, remote-host
routing, immutable packaging, compiled HookLab hooks, its GUI editor, and the installed standalone
watcher. Preserve those foundations; do not restart completed plans.

## How to travel the roadmap

The numbered roads are open product outcomes, ordered from most concrete to most speculative. Start a
later road only when evidence or an explicit product decision justifies changing that order.

Before starting a road:

- reconcile its motivating documents and old reports with the current code and package;
- reproduce old defects before treating them as open work;
- create or update one bounded task under the separate `docs/local/work` repository.

While working:

- keep tasks, investigations, handoffs, measurements, and live evidence in `docs/local`;
- keep supported behavior, architecture, operation, and public contracts in the main `docs`;
- update architecture, reference, or baseline documents only when their owned truth changes;
- prefer one verified end-to-end slice over several partially real layers.

A road is complete only when its exit evidence is satisfied, not when implementation merely exists.
On completion:

1. Promote durable behavior to the owning product, guide, reference, or baseline document.
2. Close or archive the bounded task and retain detailed evidence in `docs/local`.
3. Remove the completed road and its route-at-a-glance entry; do not add a completion diary.
4. Renumber the remaining roads and repair ordering references.

If only part of an outcome is complete, rewrite the road around what remains.

## Route at a glance

1. Establish a reliable CoreCLR debugger boundary from the retained Barnyard failures.
2. Revisit high-risk execution, editing, and scripting one workflow at a time.
3. Keep compatibility expansions parked until product scope changes.
4. Improve HookLab authoring only when a concrete failing hook exists.

## Road 1 - establish the CoreCLR debugger boundary

Promote the retained Barnyard evidence into three ordered, independently verified slices:

1. Resolve `mscordbi.dll` and `mscordaccore.dll` by the filename, architecture, PE timestamp, and
   `SizeOfImage` identity supplied by `ICLRDebuggingLibraryProvider3`. Search adjacent files, a private
   verified cache, and all installed x64 `Microsoft.NETCore.App` runtimes without selecting one from a
   single-file apphost's version. Keep downloads optional, explicit, atomic, and outside the dispatcher.
2. Reproduce and capture the managed callback that leaves Barnyard suspended while the session reports
   running. Enforce one observable terminal disposition per accepted callback without an unconditional
   broad-`finally` continue that could corrupt counters, queued callbacks, intentional stops, or detach.
3. Only after ordinary CoreCLR attach, pause, continue, breakpoint, evaluation, detach, and failure
   behavior have their own live gate, design CoreCLR HookLab as a separate runtime backend. Do not weaken
   the proven CLR v4 resident, identity, guard, authentication, revision, or ownership boundaries.

The current product boundary remains x64 CLR v4 and Mono/Unity until those gates pass. The active
evidence, investigation order, and acceptance criteria live in
`docs/local/work/coreclr-debugger.md`.

## Road 2 - consider high-risk capabilities separately

General target C# execution, assembly editing or project export, live method-body replacement, and
dnSpy-host scripting are separate trust domains. HookLab's bounded compiler authorizes none of them.

If a named workflow justifies one, proceed roughly in this order:

1. Tighten the existing expression and invocation tier per engine.
2. Add deterministic copy-on-write artifact or project export before overwrite support.
3. Consider a digest-verified target payload against a disposable x64 CLR v4 fixture.
4. Consider live replacement only with exact paused-target and original-body guards.
5. Leave dnSpy-host scripting last because it runs with the host user's authority and may not admit
   honest hard cancellation.

Each capability requires its own permission, bounds, audit policy, remote-default-off behavior,
side-effect reporting, and end-to-end refusal tests.

## Road 3 - parked horizons

x86, native debugging, broad Mono HookLab support, reverse patches, generic hook providers,
profiler/ReJIT, and Visual Basic parity remain outside the current x64 CLR v4 and Unity scope.

Deterministic Unity fixtures, headless Mono connection-failure handling, and broader multi-session
isolation are independent quality projects. Promote one only with a concrete workflow, fixture, and
acceptance boundary.

## Road 4 - improve HookLab authoring when demanded

There is currently no failing base hook that justifies an authoring slice. Generic methods and declaring
types, additional resident compiler references, a larger source boundary, Roslyn editor assistance,
natural collision-safe parameter names, and richer package management are known possibilities, not
open defects. The historical compact VmConnect source proves past pressure but still compiled and ran;
it must be reproduced against the current package before it can motivate a source-boundary change.

Pull one item forward only from a concrete hook that fails on the current verified package. That slice
needs a target fixture that fails before it, compilation and runtime rollback coverage, and the existing
exact MVID, token, signature, and IL identity guarantees.

## Invariants for every road

- Preserve exact session-scoped `module_id` and MVID identity; fail on zero or ambiguity.
- Never turn disconnect into implicit resume, detach, terminate, restart, or hook removal.
- Preserve unknown and foreign-owned resident hooks.
- Keep elevated inputs immutable, closed, verified, and separate from editable policy and state.
- Treat harness failures as possible harness defects until independently observed.
- Distinguish source coverage, package verification, and engine-specific live evidence.
- Run Protocol, Gateway, Extension, composition, pipeline, and applicable live gates in supported order.
- Keep dnSpyEx synchronization routine, bounded, and reviewable; it is not a product road.

The goal is not every imaginable debugger button. It is a product whose next mile has exact identity,
explicit authority, honest evidence, and reversible behavior wherever reality permits it.
