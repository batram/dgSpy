# Dream of roads

This is dgSpy's ordered roadmap for open product work as of 2026-08-21. It is not a release promise.
The current product already includes verified x64 CLR v4, CoreCLR, and Mono/Unity debugger bridges,
remote-host routing, immutable packaging, compiled HookLab hooks across CLR v4, CoreCLR and Mono -
including inside an unmodified shipped Unity player - its GUI editor, and the installed standalone
watcher. Preserve those foundations; do not restart completed plans.

Runtime backends are now an explicit compatibility framework, and the next roads build on it rather than
rebuilding it: a build-generated resident payload matrix proved against the shipped bytes, a
compatibility probe that drives a full resident lifecycle on each runtime family in seconds, one
immutable backend row per supported runtime naming the payload slots the build ships, CodeDom and Roslyn
behind a runtime-neutral compiler boundary, and refusal reports that name their stage. See
[Resident payload matrix](../product/HOOKLAB.md#resident-payload-matrix),
[Resident compatibility probe](../product/HOOKLAB.md#resident-compatibility-probe),
[Compilation boundary](../product/HOOKLAB.md#compilation-boundary),
[Resident stages and refusal reports](../product/HOOKLAB.md#resident-stages-and-refusal-reports), and
[HookLab runtime backends](../product/ARCHITECTURE.md#hooklab-runtime-backends).

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

1. Answer Mono's last two unknowns - domain reloads and finalizers during retirement - with live
   evidence, or a fixture that shows why they cannot yet be measured.
2. Add ordinary x86 debugging and x86 HookLab through a proved architecture boundary.
3. Revisit high-risk execution, editing, and scripting one workflow at a time.
4. Keep the remaining compatibility expansions parked until product scope changes.
5. Improve HookLab authoring only when a concrete failing hook exists.

## Road 1 - domain reloads and finalizers, the last two Mono unknowns

Mono is a supported HookLab runtime, standalone and embedded alike. One payload set across both Mono
builds, arrival, and a compiled hook installed by the pinned Harmony **inside an unmodified shipped
Unity player** are all product behaviour now, gated by `tests\run-unity-hooklab-smoke.ps1` (20 checks
against a player with nothing added to its `Managed` directory) and `tests\run-mono-hooklab-smoke.ps1`
(22 checks on a fixture this repository builds).

Four of the six questions this road carried are answered, each with a supported-or-refused result and a
gate behind it - `tests\run-mono-hooklab-boundaries.ps1`, 19 checks, plus unit coverage:

- **Generic carriers are refused, by name.** A generic method definition or a method on an open generic
  type is not one runtime method, so there is nothing to intercept. It was already impossible; the
  refusal now names the carrier and the reason instead of surfacing Harmony's
  `NotSupportedException: Specified method is not supported.` A closed instantiation stays patchable.
- **Inlinable carriers are supported**, measured with an `AggressiveInlining` carrier on Mono 6.12.
  Interception of call sites already compiled is not claimed, and the gate keeps measuring rather than
  asserting a guarantee.
- **Reconnect and adoption work end to end.** A hook survives with no debugger attached, the soft
  debugger accepts a second session after a detach, `initialize_hooklab` adopts the existing resident
  rather than byte-loading a second generation, and the new session can remove the old session's hook.
- **A runtime that cannot open a named pipe is refused as unsupported**, naming the reason. Unity's own
  `mono.exe` is the measured example; it is x86-only, so the architecture guard refuses it before the
  endpoint is reached and the classification is asserted where it is decided rather than live.

See [Hook shapes, and what is refused](../product/HOOKLAB.md#hook-shapes-and-what-is-refused).

Two remain, and both need something this repository cannot currently produce:

1. **Domain reloads.** The *stated* behaviour already exists and is unit-tested: the resident's target
   identity includes the application domain, so an operation after a reload is refused by an
   `appdomain_id` guard mismatch - the resident does not follow a reload. What is missing is live
   evidence, and it needs a target that actually reloads its scripting domain. The `uch-debug-target`
   player does not, so this needs either a fixture built to reload one or a player that does. Until
   then the claim is "guarded", not "measured".
2. **Finalizers during retirement.** Retirement waits for its listener on Mono. What a CLR finalizer
   thread that is mid-hook does at that moment is untested, and constructing that race deterministically
   is the whole difficulty. `tests\run-mono-hooklab-stress.ps1` is the instrument if it turns out to be
   intermittent rather than constructible.

Neither blocks the supported claims above; both are cases where "we do not know" is the honest current
answer, and each needs a fixture before it needs a fix.

Two boundaries stay fixed while this proceeds. No mod loader - UCH, BepInEx, or another - may become a
dependency. And none of this implies IL2CPP or AOT support.

## Dream - stop carrying a compiler into the target

Not a road: nothing here is required, and the current arrangement works on every runtime measured,
including an unmodified stripped Unity player. It is written down because the *reason* the payload is
16 MB and needs a fallback axis at all is one decision - **Roslyn runs inside the target**.

**The blocking half of this dream is spent.** It used to carry two items: remove the `netstandard`
reference, which no target could be made to satisfy, and shrink the payload. The first is done and
lives in the product now - see
[No payload may need the netstandard facade](../product/HOOKLAB.md#no-payload-may-need-the-netstandard-facade).
What is left is size, which is an optimisation rather than a requirement, and one alternative
architecture.

**Merge the compiler group.** Roslyn's remaining external surface is seven implementation assemblies -
`System.Collections.Immutable`, `System.Reflection.Metadata`, `System.Memory`, `System.Buffers`,
`System.Runtime.CompilerServices.Unsafe`, `System.Threading.Tasks.Extensions`,
`System.Text.Encoding.CodePages` - real code, and mergeable with ILRepack or a pass over dnlib, which
dgSpy already depends on. One `HookLab.Compiler.dll` would take twelve bootstrap payloads to about four
and roughly 16 MB to roughly 5, and the fallback axis for the compiler group would stop being
necessary. It was deliberately not done with the facade fix: merging 11.3 MB of Roslyn brings
type-identity collisions, strong-name loss on the merged identity, resource and `InternalsVisibleTo`
handling, and a probe that must compile against the merged assembly - all risk in exchange for bytes,
none of it removing a requirement. Do it when payload size is the problem someone actually has.

**Compile on the host instead.** dgSpy compiles the hook in its own process, which already has
everything, and injects only the finished assembly. Removes the compiler group outright rather than
shrinking it, collapses the CodeDom-versus-Roslyn selector, and makes the payload byte-identical on
CLR v4, CoreCLR and Mono. The work is references: the host must compile against the *target's* own
assemblies, which for a player are the DLLs in `Managed\` - exact, on disk, and already enumerated by
`list_modules`. The existing guards (module MVID, IL SHA-256, signature) keep a stale compile from being
installed. The real cost is evidence: the compatibility probe's compile stage and every "compiled hook"
proof on all three runtimes would have to be re-earned. That cost is why it was not the way to land a
facade fix, and it is unchanged - this is a separate product decision, on its own schedule.

A footnote, not a third option: `Mono.CSharp` - the mcs compiler as a single net4x library - would
sidestep both, at the price of a C# 6/7-era language version and a compiler nobody maintains. For
Prefix/Postfix snippets that may well be enough; it is a product-quality decision rather than a
technical one.

**Neither is urgent.** In-target Roslyn was measured arriving in a real stripped Unity player in 2.9
seconds of resident work, carrying all twelve payloads because the player supplies none of them - and
now with nothing placed in that player by hand.

## Road 2 - add x86 debugging and HookLab

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

## Road 3 - consider high-risk capabilities separately

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

## Road 4 - parked horizons

Native debugging, IL2CPP, reverse patches, generic hook providers, profiler/ReJIT, and Visual Basic
parity remain outside the current CLR v4, CoreCLR, and Unity/Mono scope.

Deterministic Unity fixtures, headless Mono connection-failure handling, and broader multi-session
isolation are independent quality projects. Promote one only with a concrete workflow, fixture, and
acceptance boundary.

## Road 5 - improve HookLab authoring when demanded

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
