# Dream of roads

This is dgSpy's ordered roadmap for open product work as of 2026-08-20. It is not a release promise.
The current product already includes verified x64 CLR v4, CoreCLR, and Mono/Unity debugger bridges,
remote-host routing, immutable packaging, compiled HookLab hooks across CLR v4 and CoreCLR, its GUI
editor, and the installed standalone watcher. Preserve those foundations; do not restart completed plans.

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

1. Make runtime and architecture compatibility explicit, fast to probe, and diagnosable.
2. Add a Mono HookLab resident on that framework.
3. Add ordinary x86 debugging and x86 HookLab through a proved architecture boundary.
4. Revisit high-risk execution, editing, and scripting one workflow at a time.
5. Keep the remaining compatibility expansions parked until product scope changes.
6. Improve HookLab authoring only when a concrete failing hook exists.

## Road 1 - make runtime backends an explicit compatibility framework

CoreCLR HookLab proved that runtime differences are real but currently too distributed: backend choice,
initialization, compiler dependencies, patch-engine assets, packaging, diagnostics, and live evidence
cross several layers. Consolidate those differences without changing shipped CLR v4 or CoreCLR behavior.

Three pieces are done. The build states every resident payload's role, carrier, runtime family,
framework, architecture, identity, provenance, dependencies, and digest, and package verification
proves it against the shipped bytes in both directions. A compatibility probe drives a complete
resident lifecycle against real CLR v4 and CoreCLR targets in seconds, before any GUI gate, failing
with a named stage and payload identity; supported CoreCLR versions are declared rather than implied.
And the host now has one immutable backend row per supported runtime, naming the same payload slots the
build ships, with deterministic selection and no runtime conditionals left scattered through shared
orchestration. See [Resident payload matrix](../product/HOOKLAB.md#resident-payload-matrix),
[Resident compatibility probe](../product/HOOKLAB.md#resident-compatibility-probe), and
[HookLab runtime backends](../product/ARCHITECTURE.md#hooklab-runtime-backends).

CodeDom and Roslyn are now isolated behind a runtime-neutral compiler boundary, with a metadata test
refusing either technology in a shared signature, field, or generic instantiation - see
[Compilation boundary](../product/HOOKLAB.md#compilation-boundary).

One subslice remains:

1. Standardize backend lifecycle states and bounded stage-aware resident errors.

This road exits only when the current CLR v4 and CoreCLR packaged live hook lifecycles remain green and
the lifecycle states and diagnostics are documented in their owning product documents. The implementation-grade task and acceptance evidence live in
`docs/local/work/runtime-backend-architecture-mono-x86.md`.

## Road 2 - add a Mono HookLab resident

Ordinary Mono/Unity debugging already exists. This road adds HookLab residency without depending on
UCH, BepInEx, or another mod loader and without implying IL2CPP or AOT support.

1. Prove one safe authenticated resident lifecycle on a disposable Mono/Unity fixture, including one
   guarded prefix, event observation, removal, debugger detach, and healthy target survival.
2. Add an explicit Mono backend with exact runtime/module identity and a pinned, live-proven compiler
   and Harmony dependency policy.
3. Extend the payload manifest, compatibility probe, package, disposable Unity fixture, and hidden-
   desktop UCH live gates through retirement.

Domain reloads, generics, inlining, finalizers, reconnect/adoption, and unsupported Mono variants need
explicit supported or refused results. If safe residency requires native Mono embedding rather than
debugger-driven managed loading, record that boundary before broadening implementation.

## Road 3 - add x86 debugging and HookLab

x86 is an architecture boundary, not a build flag. First prove whether the x64 host can control an x86
CorDebug target without loading architecture-mismatched native debugger components. If it cannot, use
the smallest authenticated x86 helper/sidecar boundary justified by the prototype.

1. Prove and document the ordinary debugger architecture decision on a disposable CLR v4 x86 target.
2. Add ordinary x86 debugging with exact matching runtime components and full x64 regression gates;
   add CoreCLR x86 only for explicitly supported runtime versions.
3. Add x86 native bootstrap/payload manifest slots and HookLab backend variants, auditing pointer size,
   calling conventions, structure layout, discovery, adoption, packaging, and retirement.

No x86 combination is advertised until its packaged ordinary-debugger and, where claimed, complete
HookLab lifecycle passes. Cross-architecture discovery and adoption must fail deterministically.

## Road 4 - consider high-risk capabilities separately

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

## Road 5 - parked horizons

Native debugging, IL2CPP, reverse patches, generic hook providers, profiler/ReJIT, and Visual Basic
parity remain outside the current CLR v4, CoreCLR, and Unity/Mono scope.

Deterministic Unity fixtures, headless Mono connection-failure handling, and broader multi-session
isolation are independent quality projects. Promote one only with a concrete workflow, fixture, and
acceptance boundary.

## Road 6 - improve HookLab authoring when demanded

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
