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

1. Complete the dgSpy-to-watcher package journey with verified enrollment and explicit enablement.
2. Preserve intentional watcher operating state across installation and upgrade.
3. Let dgSpy and the watcher safely share one resident HookLab generation.
4. Add an honest unelevated operator surface for watcher operations that really exist.
5. Resolve controller continuity and independent safe stimulus during bounded waits.
6. Fix narrowly reproduced MCP correctness and usability defects.
7. Improve HookLab authoring only when concrete hooks require it.
8. Revisit high-risk execution, editing, and scripting one workflow at a time.
9. Keep compatibility expansions parked until product scope changes.

## Road 1 - complete the hook export journey

`export_hook_package` already freezes one installed compiled hook into a canonical watcher deployment
with a disabled profile. Complete the operator journey:

1. Live-verify export through RPC against an installed dgSpy package and the retained VMConnect hook.
   Read back the exact package, manifest, target guards, source revision, digest, and disabled profile.
2. Add atomic enrollment for canonical exports. Before publication, verify the digest, closed inventory,
   owner and ACL, absence of reparse points, and same-ID conflicts.
3. Keep enablement separate and explicit; export and enrollment must never patch future processes.
4. Prove catalog-generation readback and last-good rollback. Failed enrollment must leave the previous
   tree, catalog, profiles, targets, and resident hooks untouched.

Exit evidence: one real hook travels from dgSpy authoring through export, enrollment, explicit enable,
new-process application, disable, and readback without rebuilding or replacing the watcher.

## Road 2 - preserve watcher operating intent

Close the remaining installer lifecycle ambiguity:

1. Preserve whether the exact verified watcher/task was running or intentionally stopped before upgrade.
2. Define first-install start policy explicitly.
3. Before reporting success, read back the new PID, creation identity, installed image, catalog
   generation, and health; otherwise roll back.
4. Preserve the invariant that upgrade, uninstall, watcher exit, and task failure do not stop targets,
   remove resident hooks, or modify target binaries.

Exit evidence: live upgrades from both running and intentionally stopped states, plus injected failure
that proves rollback and target preservation.

## Road 3 - share one resident between two controllers

Make dgSpy and the watcher adopt either side's valid resident instead of injecting competing generations:

1. Discover, authenticate, and inventory a resident before adoption or injection.
2. Preserve unknown and foreign-owned hooks.
3. Define ownership for edit, enable, disable, and removal; require an owning capability or explicit
   transfer. `remove_all_hooks` must not erase another controller's work.
4. Return stable adoption and per-resident inspection failures without collapsing watcher status.
5. Round-trip the exact case-sensitive CLR assembly simple name used at hook creation.

Exit evidence: live adoption in both directions while the original hook remains active, followed by
restart, inventory, addition of a second hook, and removal of only the new owner's hook.

## Road 4 - add an honest watcher operator surface

Add an unelevated notification-area companion while keeping the elevated watcher headless. Show exact
running, paused, error, stale, and stopped states; expose pause/resume, profile enable/disable, installed
task start/restart, and the bounded audit log.

Expose only implemented operations. Exiting the companion must not stop the watcher. Stopping the
watcher remains explicit and must not remove resident hooks. Omit package editing and arbitrary hook
removal until their ownership contracts exist.

Exit evidence: main-desktop human acceptance and hidden-desktop automation of every exposed operation,
including stale-state and privilege-boundary failures.

## Road 5 - resolve control-plane continuity and concurrency

### 5A. Choose the session recovery contract

Current recovery uses expiry or inspected `claim_session(force=true)` takeover. Decide whether that is
sufficient or whether rightful-owner continuity requires a persisted, non-listable, revocable claim
capability. Either choice needs restart, contention, expiry, release, transfer, and audit-redaction tests.

### 5B. Permit safe stimulus during a bounded wait

Trace MCP client, Gateway, and host dispatch to locate the serialization boundary. Preserve mutation and
func-eval serialization while allowing a bounded `run_to_*`/wait and an independent non-conflicting
stimulus to overlap.

Exit evidence: the stimulus reaches the host before the deadline and completes the wait; timeout,
cancellation, and disconnect strand neither request.

## Road 6 - fix reproduced MCP quality defects

Treat each item as an independent reproduction-led fix:

1. Contain the intermittent wildcard `list_programs` access violation and make `doctor` report the fault.
2. Add operation/correlation context and a recovery boundary to raw `internal_error` responses without
   leaking arbitrary exception details.
3. Recheck GUI-versus-MCP mutation races and enforce guards at the dispatcher only if reproduced.
4. Explain unsafe func-eval stops and offer bounded candidate guidance without probing every thread.
5. Document conditional-breakpoint risk in locks, hot callbacks, and retry loops.
6. Clarify raw-module offsets, IL code-size semantics, and instrumented native disassembly; change code
   only where a fixture proves the contract wrong.
7. Add a public exact-MVID resolver only if real callers show the existing filtered discovery workflow
   is too costly. Zero and ambiguous resolution must remain explicit failures.

Do not reopen delivered neighbors: filtered member listings, session-scoped module identity, the
development transcript, and explicit controller takeover already exist.

## Road 7 - improve HookLab authoring when demanded

Pull these only from a concrete failing hook: generic methods and declaring types, additional resident
compiler references, a larger source boundary, Roslyn editor assistance, natural collision-safe
parameter names, or richer package management.

Each slice needs a target fixture that fails before it, compilation and runtime rollback coverage, and
the existing exact MVID, token, signature, and IL identity guarantees.

## Road 8 - consider high-risk capabilities separately

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

## Road 9 - parked horizons

CoreCLR, x86, native debugging, broad Mono HookLab support, reverse patches, generic hook providers,
profiler/ReJIT, and Visual Basic parity remain outside the current x64 CLR v4 and Unity scope. Useful
CoreCLR evidence is retained under `docs/local/work/backlog/coreclr-debugger.md`.

Deterministic Unity fixtures, headless Mono connection-failure handling, and broader multi-session
isolation are independent quality projects. Promote one only with a concrete workflow, fixture, and
acceptance boundary.

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
