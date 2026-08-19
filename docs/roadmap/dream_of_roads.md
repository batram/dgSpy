# Dream of roads

This is dgSpy's ordered roadmap for open product work as of 2026-08-19. It is not a release promise.
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

1. Make the target environment an explicit contract instead of an ambient assumption.
2. Make runtime and architecture compatibility explicit, fast to probe, and diagnosable.
3. Add a Mono HookLab resident on that framework.
4. Add ordinary x86 debugging and x86 HookLab through a proved architecture boundary.
5. Revisit high-risk execution, editing, and scripting one workflow at a time.
6. Keep the remaining compatibility expansions parked until product scope changes.
7. Improve HookLab authoring only when a concrete failing hook exists.

## Road 1 - make the target environment an explicit contract

HookLab initialization currently assumes, without ever stating it, that the debugger and its target
share a Windows identity, and that the machine carries whatever the injected payload happens to link
against. Neither assumption is written down, and both are discovered at the same place: a remote
`LoadLibraryW` that returns NULL and a single message that cannot distinguish a denied path from a
missing dependency from a wrong image.

A live IIS worker running under a domain service account broke three of these at once. The engine,
transport, breakpoints, and func-eval were healthy throughout; only initialization failed, and the
failure named none of its causes. The next environment that differs slightly - a session-0 service, a
machine without a redistributable we later depend on, a target whose token is more restricted than its
owner - reproduces the same undifferentiated failure and the same investigation.

This road does not loosen any security boundary. Every fix below makes an authority relationship
explicit rather than wider.

Travel this road in independently implemented, live-tested, retired, and committed subslices:

1. Assert the dependency surface of every artifact injected into a target. A native artifact whose
   direct or delay-load imports leave a reviewed allowlist fails the build, not a customer machine.
2. Capture the real reason a remote load failed. The `CreateRemoteThread(LoadLibraryW)` exit code is a
   truncated module handle, so every cause collapses to zero; an x64 bootstrap stub that also returns
   the target's last-error code makes access denied, missing dependency, and bad image distinguishable.
   The target-side loader stays authoritative for load failure; no preflight replaces it.
3. Consolidate the two native injector implementations behind the extracted injector core, so the
   remaining subslices land once rather than twice.
4. Replace ambient identity and ambient temporary paths with one exchange area derived from the
   controller and target identities, used by staging, completion, and any later shared artifact.
5. Derive endpoint access control from that same identity pair rather than from whichever process
   happens to create the endpoint.
6. Put the resident where the target's code actually is. Native initialization enters through
   `ExecuteInDefaultAppDomain`, so on any host that runs its code in a secondary application domain -
   every IIS worker - the resident lands somewhere that can never see the application's assemblies.
   Address the domain as an explicit part of the target, and key residency by it.
7. Compute a target contract before anything mutates, and refuse with the exact failing precondition.
   Report each precondition as satisfied, failed, or not provable before the attempt, so nothing that
   was merely unevaluable is reported as a pass. Expose the same computation as a read-only readiness
   probe that leaves the target process untouched.
8. Vary identity, and application domain, in the live gates. Every current HookLab smoke runs debugger
   and target as the same user, in the default domain, on a developer machine - an environment that
   satisfies every one of these assumptions silently.

Road 1 exits only when the packaged CLR v4 and CoreCLR hook lifecycles remain green, a target whose
identity and application domain both differ from the debugger's completes both of those lifecycles, and
every precondition enumerated by subslice 7 is reachable as its own named refusal. Loader failures that
only the target can decide stay stage-specific initialization failures and are not counted as
preconditions. The implementation-grade task, the precondition matrix, and acceptance evidence live in
`docs/local/work/target-environment-contract.md`.

Distributed component skew - host build against Gateway build and protocol - is an adjacent contract
and stays with deployment and versioning, not this road.

## Road 2 - make runtime backends an explicit compatibility framework

CoreCLR HookLab proved that runtime differences are real but currently too distributed: backend choice,
initialization, compiler dependencies, patch-engine assets, packaging, diagnostics, and live evidence
cross several layers. Consolidate those differences without changing shipped CLR v4 or CoreCLR behavior.

Travel this road in independently implemented, live-tested, retired, and committed subslices:

1. Generate and verify an authoritative payload matrix with exact runtime, architecture, framework,
   dependency, provenance, identity, and hash data.
2. Add fast real-process resident compatibility probes for CLR v4 and each supported CoreCLR range;
   keep packaged live gates authoritative.
3. Introduce an immutable backend descriptor and narrow explicit CLR v4/CoreCLR backend contract.
4. Isolate CodeDom and Roslyn behind a runtime-neutral compiler ABI.
5. Standardize backend lifecycle states and bounded stage-aware resident errors.

Road 2 exits only when the current CLR v4 and CoreCLR packaged live hook lifecycles remain green and
the new manifest, probe, contract, compiler boundary, lifecycle, and diagnostics are documented in
their owning product documents. The implementation-grade task and acceptance evidence live in
`docs/local/work/runtime-backend-architecture-mono-x86.md`.

## Road 3 - add a Mono HookLab resident

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

## Road 4 - add x86 debugging and HookLab

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

## Road 5 - consider high-risk capabilities separately

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

## Road 6 - parked horizons

Native debugging, IL2CPP, reverse patches, generic hook providers, profiler/ReJIT, and Visual Basic
parity remain outside the current CLR v4, CoreCLR, and Unity/Mono scope.

Deterministic Unity fixtures, headless Mono connection-failure handling, and broader multi-session
isolation are independent quality projects. Promote one only with a concrete workflow, fixture, and
acceptance boundary.

## Road 7 - improve HookLab authoring when demanded

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
