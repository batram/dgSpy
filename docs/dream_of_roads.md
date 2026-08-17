# Dream of roads

This is dgSpy's aspirational roadmap as of 2026-08-17: one ordered path through work that is still
open, grounded in the current source tree rather than accumulated phase prose. It is deliberately not
a release promise. The nearer a road is to the top, the more concrete its evidence and acceptance
boundary; the farther away it is, the more it requires a fresh product decision before implementation.

The present foundation is already substantial. dgSpy has a verified x64 CLR v4 and Mono/Unity debugger
bridge, authenticated local and outbound remote-host routing, ownership and optimistic mutation guards,
immutable packaging, HookLab's resident compiled Prefix/Postfix/Finalizer/Transpiler path, a real GUI
editor, and an installed per-user standalone HookLab watcher. Do not rebuild those foundations from old
plans. Preserve their tests and use them as the starting line.

## The route at a glance

1. Close the dgSpy-to-watcher package journey with live evidence and explicit enrollment.
2. Make watcher installation and upgrades preserve intentional operating state.
3. Make dgSpy and the watcher safely share one resident HookLab generation.
4. Turn the watcher into an operable product without inventing controls the backend cannot perform.
5. Resolve controller continuity and the remaining concurrency boundary.
6. Burn down narrowly reproduced MCP correctness and usability defects.
7. Improve HookLab authoring only when real hooks demand it.
8. Revisit high-risk execution, editing, and scripting capabilities one workflow at a time.
9. Keep compatibility expansions parked until the product scope changes.

## Road 0 - keep the map honest

Before and during every slice, reconcile the document that motivated it with the code and move completed
or superseded narrative to `docs/history/`. In particular:

- `IMPLEMENTATION_PLAN.md` still proposes claim-token recovery, while current code exposes inspected,
  explicit `claim_session(force=true)` takeover. That is an unresolved product choice, not an absent
  reclaim mechanism.
- `FUTURE_CAPABILITIES.md` still describes mutation hooks and custom HookLab C# as outside the first
  version. They are delivered for the bounded HookLab resident; only *general* target-code execution
  remains future work.
- `HOOKLAB_WATCHER_IMPLEMENTATION_PLAN.md` phases 0-4 and the local H1/H2 narratives describe delivered
  work. Their useful remaining material is the interoperability list and completion evidence.
- Dated `docs/local` investigations, review logs, evidence folders, and `history_fail` plans are evidence,
  not a backlog. Reproduce an issue against the current package before promoting it here.

For each road below, update the architecture/reference/baseline only when the public contract or supported
operation changes. Keep live measurements in a dated evidence or local status document rather than
turning them into timeless guarantees.

## Road 1 - finish the hook export journey

The first export slice exists: `export_hook_package` freezes one installed compiled hook into a canonical,
closed watcher deployment and creates its profile disabled. What remains is to prove and complete the
operator journey.

1. Run a live RPC acceptance against an installed dgSpy package and a retained VMConnect compiled hook.
   Read back the exported package, manifest, target guards, source revision, and disabled profile from
   the exact produced directory.
2. Add an integrity-checked, atomic enrollment operation for canonical exported packages. Enrollment
   must stage and verify the digest, ACL/owner, absence of reparse points, closed inventory, and same-ID
   conflicts before publishing a new catalog generation.
3. Keep enablement a separate explicit decision. Export and enrollment must never silently cause an
   elevated watcher to patch future processes.
4. Prove last-good rollback and watcher catalog-generation readback. A failed import must leave the
   prior executable tree, catalog, profiles, targets, and resident hooks untouched.

Exit evidence: one real hook moves from dgSpy GUI/MCP authoring through export, enrollment, explicit
enable, new-process application, disable, and readback without rebuilding or replacing the watcher.

## Road 2 - make installation preserve operator intent

The immutable H2 installer, scheduled task, verification, rollback, and uninstall exist. Close the
remaining lifecycle ambiguity:

1. Record whether the exact verified watcher/task was running before upgrade.
2. After a successful replacement, restart it only when it was previously running; preserve an
   intentionally stopped watcher as stopped.
3. Make first-install start policy explicit instead of relying on the next logon.
4. Read back the new PID, creation identity, installed image path, catalog generation, and healthy
   status before reporting upgrade success. Roll back if that boundary cannot be established.
5. Retain the existing invariant that upgrade, uninstall, watcher exit, and task failure do not stop
   targets, remove resident hooks, or modify target binaries.

Exit evidence: live upgrades in both running and intentionally stopped states, plus failure injection
that demonstrates rollback and target preservation.

## Road 3 - one resident, two trustworthy controllers

Today the standalone watcher can adopt its own resident, and dgSpy can operate its own resident, but the
two directions are not yet one supported interoperability story. This is the most important structural
work after enrollment.

1. Make dgSpy discover, authenticate, and adopt a valid watcher-created resident before attempting any
   injection. Read its generation and hook inventory first; preserve unknown and watcher-owned hooks.
2. Make the watcher adopt a dgSpy-created resident using the same authoritative injector/adoption
   boundary. Neither side may inject a competing generation merely because ownership differs.
3. Define hook ownership and mutation rules. Listing is shared; edit, enable/disable, and removal must
   require a real owning capability or an explicit transfer operation. `remove_all_hooks` must never
   become permission to erase another controller's work.
4. Return stable, actionable failures. A valid but unauthenticatable resident should produce
   `resident_not_adoptable`, not a generic worker timeout. Watcher `status` should preserve the watcher
   lifecycle and return a structured per-resident inspection error rather than collapsing globally.
5. Round-trip the exact case-sensitive CLR assembly simple name used by hook creation. Do not infer it
   from a filename, display name, or path.

Exit evidence: live tests in both ownership directions, with the original hook visibly active throughout
adoption, restart, inventory, addition of a second hook, and removal of only the new owner's hook.

## Road 4 - an operator surface for the watcher

Once enrollment and interoperability are stable, add the unelevated management companion already
outlined in the watcher plan. Keep the elevated watcher headless and narrowly privileged.

The first useful surface is a notification-area companion that can show exact running, paused, error,
stale, and stopped states; pause/resume; enable/disable profiles; start or restart the installed task;
and open the bounded audit log. A fuller window may list installed packages/profiles and their latest
sanitized results.

Only expose operations that really exist. Exiting the tray closes the companion, not the watcher.
Stopping the watcher is explicit and still does not remove resident hooks. Package editing, arbitrary
hook removal, and suggestive disabled controls stay absent until their backend contracts and ownership
rules are implemented.

Exit evidence: main-desktop human acceptance plus hidden-desktop automation of status and every exposed
operation, including stale-state and privilege-boundary failures.

## Road 5 - controller continuity and concurrent waits

Two control-plane questions should be resolved together only at their shared protocol boundary, not as
one large rewrite.

### 5A. Decide session ownership continuity

Current behavior is explicit and usable: an active controller blocks normal claims; an operator can
inspect it and intentionally take over with `force=true`; expiry permits ordinary reclaim. The older
claim-token proposal would instead prove continuity across MCP or Gateway restart. Decide which product
property is wanted before coding:

- If deliberate operator takeover is sufficient for equally trusted loopback clients, retire the token
  proposal and document forced takeover as the supported recovery contract.
- If automatic rightful-owner continuity matters, design a persisted, non-listable, revocable claim
  capability. It must survive the intended restart, never widen access policy, distinguish token reclaim
  from expiry/force in audit, and be invalidated on deliberate release or transfer.

Either choice needs restart, contention, expiry, release, forced transfer, and audit-redaction tests.

### 5B. Permit independent safe stimulus during a bounded wait

Instrument the MCP-client to Gateway to host path to locate the serialization that prevents a
long-running `run_to_*`/wait from overlapping an independent request. Preserve intentional mutation and
func-eval serialization, but allow a bounded wait and a non-conflicting stimulus to be in flight. Do not
assign blame to the client, Gateway, or host before the trace proves where dispatch stops.

Exit evidence: the stimulus reaches the host before the wait deadline, the resulting breakpoint/event
completes the wait, and timeout, cancellation, or disconnect strands neither request.

## Road 6 - focused MCP quality work

Treat each item as its own reproduction-led fix. Recheck old local reports first; several neighboring
friction points have already been fixed or deliberately declined.

Priority candidates:

1. Reproduce and contain the intermittent `list_programs(process_names=[wildcard])` access violation.
   If an operation contains an AV, `doctor` must record a contained fault rather than immediately report
   an entirely healthy host.
2. Make raw `internal_error` responses carry an operation/correlation context and a useful recovery
   boundary without leaking arbitrary exception details.
3. Revisit GUI-versus-MCP control arbitration and duplicate resume races against the current action
   guards. If still reproducible, enforce at the dispatcher-side mutation point and keep stock dnSpy
   behavior when no dgSpy guard is active.
4. Improve func-eval discoverability: explain why a selected stop/thread is unsafe and offer bounded
   candidate guidance without probing every thread by default.
5. Document conditional-breakpoint risk inside locks, hot callbacks, and retry loops.
6. Clarify raw-module file offsets, IL code-size semantics, and breakpoint-instrumented native
   disassembly. Fix implementation defects only where a fixture proves the contract is wrong.
7. Consider a public exact-MVID module resolver only if real non-test callers or measurements show the
   existing filtered `list_modules` plus `get_metadata` workflow is too costly. Ambiguity must remain an
   explicit failure; never select the first name match.

Already-delivered neighbors should not return as roadmap work: symbol/member listings have filtering,
session-scoped module identity has a shared exact-MVID test resolver, the development transcript exists,
and explicit controller takeover exists.

## Road 7 - HookLab authoring when pressure arrives

These are good improvements, but they should be pulled by a concrete hook rather than by a desire to
make the surface larger:

- generic methods and methods on generic declaring types;
- broader resident compiler references when the current target-AppDomain set fails on a real target;
- a larger source boundary if the bootstrap parameter limit is actually reached;
- Roslyn semantic highlighting, completion, signature help, and advisory diagnostics while retaining
  target-side compilation as authority;
- natural generated parameter names with collision-safe Harmony reserved-name handling;
- richer package management after the scriptable lifecycle is settled.

Each slice needs a target fixture that fails before it, compilation/runtime rollback coverage, and the
same exact MVID/token/signature/IL identity guarantees as the current path.

## Road 8 - distant high-risk capabilities

General target C# execution, assembly editing/project export, live method-body replacement, and dnSpy-host
C# scripting remain separate trust domains. HookLab's bounded source compiler does not authorize any of
them. Start one only for a named workflow after policy can grant it independently and audit it without
recording source, expressions, values, memory, or secrets.

The rough order, if demand arrives, is:

1. Tighten and document the existing expression/invocation tier per engine.
2. Add deterministic copy-on-write artifact/project export before any overwrite path.
3. Consider a digest-verified target payload against a disposable x64 CLR v4 fixture.
4. Consider live method-body replacement only with exact paused-target and original-body guards.
5. Leave dnSpy-host scripting last; in-process Roslyn code runs with the host user's authority and may
   not admit honest hard cancellation.

Every capability needs its own permission, bounded queues/deadlines/sizes, remote-default-off policy,
possible-side-effect reporting, and end-to-end refusal tests.

## Road 9 - parked horizons

CoreCLR, x86, native debugging, broad Mono HookLab support, reverse patches, generic hook providers,
profiler/ReJIT, and Visual Basic parity are not part of the current x64 CLR v4/Unity product scope.
`DNSPY_CORECLR_TODOS.md` records useful CoreCLR failure evidence, but it is a parked investigation, not
the next milestone. If scope changes, begin with deterministic engine fixtures and capability truth;
do not generalize the CorDebug implementation by assumption.

Likewise, deterministic Unity fixtures, headless Mono connection-failure handling, and broader
multi-session isolation are independent quality projects. Promote one only with a concrete workflow,
fixture, and acceptance boundary.

## Rules of the road

- Prefer one end-to-end vertical slice over several partially real layers.
- Preserve exact session-scoped `module_id` and MVID identity; fail on zero or ambiguous resolution.
- Never weaken disconnect invariants: no implicit resume, detach, terminate, restart, or hook removal.
- Preserve unknown and foreign-owned resident hooks.
- Keep elevated inputs immutable, closed, verified, and separate from editable policy/state.
- Treat harness failures as possible harness defects until the product path is independently observed.
- Run Protocol, Gateway, Extension, composition, pipeline, and applicable live gates in the supported
  order; do not claim a live boundary from source-only tests.
- Keep dnSpyEx synchronization routine and reviewable. A new upstream baseline is not a product phase.
- Update this road map by deleting roads that are complete, not by piling completion diaries onto them.

The dream is not a debugger with every imaginable button. It is a debugger whose next mile is always
real: exact identity, explicit authority, reversible operations where reality permits them, and enough
light on the road that an agent can tell evidence from hope.
