# Dream of roads

This is dgSpy's ordered roadmap for open product work as of 2026-08-20. It is not a release promise.
The current product already includes verified x64 CLR v4, CoreCLR, and Mono/Unity debugger bridges,
remote-host routing, immutable packaging, compiled HookLab hooks across CLR v4 and CoreCLR, its GUI
editor, and the installed standalone watcher. Preserve those foundations; do not restart completed plans.

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

1. Settle the Mono payload set across BCL variants, then give the Mono engine owned internal breakpoints.
2. Add ordinary x86 debugging and x86 HookLab through a proved architecture boundary.
3. Revisit high-risk execution, editing, and scripting one workflow at a time.
4. Keep the remaining compatibility expansions parked until product scope changes.
5. Improve HookLab authoring only when a concrete failing hook exists.

## Road 1 - Mono works: one payload set across its builds, and arrival

Mono is a supported HookLab runtime, standalone and embedded alike. The opt-in `--runtime mono` probe leg drives a
complete lifecycle against both Mono builds that matter - mono-project 6.12 x64 and the runtime and
byte-identical BCL profile a Unity 2021.3 player embeds - Roslyn compiles, the pinned desktop Harmony
patches, behaviour changes, events arrive, removal restores, the resident retires, the target survives. `mono` is a real family in the payload matrix; the resident serves only the slots
declared valid on the runtime it is in; and the Mono backend row exists and is verified against the
shipped matrix. Five things blocked it and all five are fixed:

- **The control endpoint had no statable protection.** Mono implements neither
  `WindowsIdentity.GetCurrent().User` nor `PipeSecurity.AddAccessRule`. The descriptor is now built
  through `advapi32`/`CreateNamedPipeW` instead and is byte-identical to the managed one, verified by
  reading it back off the kernel object. CLR v4 and CoreCLR keep their managed construction.
- **The target did not survive retirement.** Mono crashes at process exit if a listener thread is still
  unwinding out of a disposed `NamedPipeServerStream`. Retirement now waits for the listener where the
  runtime requires it and reports `listener_teardown`; `Dispose` still does not wait, so the func-eval
  bound that made it non-blocking is untouched, and CLR v4 and CoreCLR take the `not_required` path.
- **Asynchronous pipes fault on Mono 6.12**, in the overlapped completion callback, taking the target
  with them. The endpoint is created synchronously now, which is all a single blocking listener thread
  ever needed. Unity 6.13 tolerated the async version, which is why it survived this long.
- **Disposing a synchronous pipe does not release a parked listener on Mono**, so retirement hung rather
  than crashed. `Dispose` wakes the listener with one connect to its own endpoint first.
- **Compilation chose CodeDom.** The selector keyed on the corlib name, and Mono's is also `mscorlib`,
  so a Mono target took the .NET Framework path - and Mono's CodeDom shells out to an `mcs` that is not
  there to shell to. Mono now selects the payload's own Roslyn, which needed four more declared slots and
  exposed a missing binding unification in the resident's resolver.

See [Supported runtimes](../product/HOOKLAB.md#supported-runtimes) and
[The Mono leg](../product/HOOKLAB.md#the-mono-leg).

The family is `mono`, not `unity`: the runtime is Mono, a player merely embeds it, and the resident's
own test (`Type.GetType("Mono.Runtime")`) cannot tell the two apart anyway. The backend row matches both
runtime GUIDs, because dnSpy reports one soft-debugger engine under two names.

The payload-set question is answered, and both Mono builds now pass. A payload set is a fallback for
what a runtime lacks, and what a runtime lacks is a property of a *build*: mono-project's 6.12 has
`System.Memory` 4.0.1.1 - too old for the pinned Roslyn - and no `System.Buffers`, while Unity 2021.3's
6.13 supplies both at 4.0.99.0 in `Facades`. Carrying them unconditionally put two `System.Memory`
assemblies in one Unity AppDomain, split `ReadOnlySpan<T>` into two types, and made Roslyn's own
`ImmutableArray.Create<T>(ReadOnlySpan<T>)` unfindable.

The matrix now carries a second, narrower axis beside the family flags: a slot may declare a **fallback
set**, and on those families the resident asks the binder for the payload's exact declared identity
before byte-loading anything. If nothing can satisfy it the embedded bytes are served as before; if the
runtime can, the embedded copy is never loaded and its manifest entry is removed, so no later bind can
produce a duplicate. Confined by the parser to `compiler-support`, so dgSpy's contracts, resident,
compiler and patch engine can never defer; never a filesystem search, only one question to the binder
using the identity the packaging tool proves against the shipped bytes; and never silent - every start
reports `payload_deferrals` with the identity that answered. `clrv4` and `coreclr` declare no fallback
and take a code path with no new branch. Three consecutive probe runs each: mono-project green with
`payload_deferrals=none`, Unity green deferring both facades. See
[Carried, or a fallback for what the runtime lacks](../product/HOOKLAB.md#carried-or-a-fallback-for-what-the-runtime-lacks)
and [2026-08-21](../local/evidence/2026-08-21-road1-payload-fallback-axis.md).

**Arrival works, and Mono is advertised.** `HookLabBackends.Pending` is empty and the Mono row is in
`All`. The new gate is `tests\run-mono-hooklab-smoke.ps1`: a fixture this repository builds, launched
under a Mono the caller supplies, driven through the whole product lifecycle - attach, readiness,
arrival by debugger evaluation, the resident's own control channel, a Roslyn-compiled hook installed by
the pinned Harmony, live behaviour change, an atomic revision replacement, removal restoring the
original, detach, and a clean exit. 22 checks. Green on mono-project 6.12 x64 three times out of three
and on Unity 2021.3's embedded 6.13 with its byte-identical player BCL five times out of six - the sixth
being item 1 below, which now fails in 30 seconds by name instead of hanging the run.

Deliberately the general case first, with Unity as a variant of it, rather than the other way round.
Mono is the runtime; a player embeds it. `tests\run-unity-hooklab-smoke.ps1` keeps the one thing a
fixture cannot supply - a real player - and no longer needs a Unity licence to answer whether HookLab
works on Mono at all.

Four things blocked arrival, and none was a missing capability:

- **Nobody could ask the soft debugger.** It places engine breakpoints for every stepper, through the
  same callback shape CorDebug's bridge uses. The owned-breakpoint contract simply lived in the CorDebug
  contracts assembly and was imported as a singleton, so there was one implementation by construction.
  It is now engine-neutral, imported `ImportMany`, and each engine exports a provider.
- **Three func-eval deadlocks, all one bug.** An event whose handler suspends the VM and waits for a Run
  cannot be raised by a func-eval: the only thread that could issue that Run is the one parked inside
  the invoke. The engine already knew this for exceptions, and not for `AssemblyLoad`, `ThreadStart` or
  `UserLog` - which is to say, not for loading a payload, starting a resident's threads, or an ordinary
  `Debug.WriteLine`. Each was found by measurement, one at a time, each hiding the next.
- **The application domain identity was the debugger's, not the runtime's.** dnSpy's Mono engine numbers
  domains from its own counter and says so; Mono's root domain is 0, not the CLR's 1. Arrival refused on
  its own guard, correctly, over a value the host had no business asserting.
- **A target that hosted a resident could not exit.** CLR v4 and CoreCLR abandon a parked background
  listener at process exit; Mono does not. The resident now releases its endpoint on `ProcessExit`.

See [Supported runtimes](../product/HOOKLAB.md#supported-runtimes),
[Arrival on Mono](../product/HOOKLAB.md#arrival-on-mono) and
[2026-08-21](../local/evidence/2026-08-21-road1-mono-arrival.md).

What remains:

1. **Finish arrival on a real player.** Measured 2026-08-21 against a live `uch-debug-target` player,
   which is the first time any of this ran outside a console fixture, and it moved the boundary twice.

   A shipped player's `Managed` directory holds nine non-Unity assemblies and **no `Facades` directory
   at all** - Unity ships only what the game references. The editor's `unityjit-win32` profile *does*
   have Facades, so every earlier "Unity" measurement was taken against a richer runtime than any player
   has, and the claim that the two are byte-identical is wrong in exactly that way. Three more fallback
   rows followed (`System.Numerics.Vectors`, `System.Threading.Tasks.Extensions`,
   `System.Text.Encoding.CodePages`), and on a player all twelve payloads are carried with
   `payload_deferrals` empty - the fallback axis behaving exactly as designed.

   With those, and with `netstandard` supplied to the player by hand, **the resident arrives: `status=ok`
   in 2.9 seconds**, patch engine loaded, control pipe published. Two things remain before this leg can
   be a gate:

   - `netstandard` cannot be shipped - see the dream below, which removes the requirement rather than
     satisfying it.
   - `initialize_hooklab` still returns `deadline_exceeded` against its 130 s bound **even though the
     resident finished in 4.3 s and wrote a healthy completion report**. So the remaining problem is on
     the host side, after a successful arrival, and it is a fresh, well-scoped bug rather than anything
     to do with Mono payloads.

The intermittent that stood here is closed, and the answer was humbling: an unhandled `IOException`
thrown by **dgSpy's own test fixture**, whose `File.Delete`/`File.Move` swap collided with the gate
polling the same file every 100 ms. An unhandled exception stops a debugged target, a stopped target
cannot answer its resident, and the resulting stall looked like a product fault from every angle except
the one that named the throwing frame. The fixture now retries the swap and never throws; 120 stress
cycles across both Mono builds are clean.

Two real fixes came out of chasing it, and both stand on their own. A breakpoint hit during a func-eval
no longer suspends the VM - an atomic action's own owned breakpoint could be re-entered mid-evaluation,
`suspendCount` climbed 1, 2, 3, 4 and the evaluation died on its deadline. And every control-channel
round trip is now bounded, because `Task.Run(..., token)` cancels only a call's scheduling and
`PipeStream` cannot time out a read. `tests\run-mono-hooklab-stress.ps1` is the instrument that found
it, kept because the next thing in this area will need it too.

Domain reloads, generics, inlining, finalizers, reconnect/adoption, and unsupported Mono variants still
need explicit supported or refused results. No mod loader - UCH, BepInEx, or another - may become a
dependency, and none of this implies IL2CPP or AOT support.

## Dream - stop carrying a compiler into the target

Not a road: nothing here is required, and the current arrangement works on every runtime measured,
including a stripped Unity player. It is written down because the *reason* the payload is 16 MB and
needs a fallback axis at all is one decision - **Roslyn runs inside the target** - and there are two
credible ways to unmake it.

Roslyn's external surface is exactly two problems. Seven implementation assemblies
(`System.Collections.Immutable`, `System.Reflection.Metadata`, `System.Memory`, `System.Buffers`,
`System.Runtime.CompilerServices.Unsafe`, `System.Threading.Tasks.Extensions`,
`System.Text.Encoding.CodePages`) - real code, mergeable. And `netstandard, Version=2.0.0.0` - a pure
type-forward facade, which merging cannot remove because merging keeps the *reference*. There is no
escape by picking an older Roslyn: 4.9.2 and 5.6.0 ship `netstandard2.0` for everything that is not
.NET Core, and the last `net45` build was Roslyn 1.0, which is C# 6 from 2015.

**Dream A - one self-contained compiler assembly.** Merge Roslyn and its seven dependencies (ILRepack,
or a pass over dnlib, which dgSpy already depends on), then eliminate the `netstandard` reference by
rewriting each `[netstandard]Type` typeref to its real net4x home - the forward table is in the NuGet
reference assembly, so the transform's inputs are pinned and its output is ours to hash into the matrix
like any other payload. Result: one `HookLab.Compiler.dll` referencing only `mscorlib`, `System` and
`System.Core`, which every Mono target and every stripped player has. Twelve bootstrap payloads become
four, roughly 16 MB becomes roughly 5, and the entire fallback axis for the compiler group stops being
necessary.

Worth knowing before starting: a generated `netstandard` facade is *not* a substitute for the rewrite.
The reference is strong-named to Microsoft's key, so no assembly we build can satisfy it, and taking
Mono's or the framework's copy off a machine breaks the rule that every payload byte comes from a
pinned package or our own build. The rewrite avoids the question entirely by never asking for
`netstandard`.

**Dream B - compile on the host.** dgSpy compiles the hook in its own process, which already has
everything, and injects only the finished assembly. Removes the compiler group outright rather than
shrinking it, collapses the CodeDom-versus-Roslyn selector, and makes the payload byte-identical on
CLR v4, CoreCLR and Mono. The work is references: the host must compile against the *target's* own
assemblies, which for a player are the DLLs in `Managed\` - exact, on disk, and already enumerated by
`list_modules`. The existing guards (module MVID, IL SHA-256, signature) keep a stale compile from being
installed. The real cost is evidence: the compatibility probe's compile stage and every "compiled hook"
proof on all three runtimes would have to be re-earned.

A footnote, not a third dream: `Mono.CSharp` - the mcs compiler as a single net4x library - would
sidestep both, at the price of a C# 6/7-era language version and a compiler nobody maintains. For
Prefix/Postfix snippets that may well be enough; it is a product-quality decision rather than a
technical one.

**Neither is urgent.** In-target Roslyn was measured arriving in a real stripped Unity player in 2.9
seconds of resident work, carrying all twelve payloads because the player supplies none of them. The
only piece that cannot ship is `netstandard`, and Dream A exists to delete that requirement.

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
