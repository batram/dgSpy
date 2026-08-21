# HookLab

HookLab adds exactly guarded Harmony hooks to an attached x64 CLR v4 or CoreCLR process, through two
separate explicit backends. It supports direct authoring through dgSpy and automatic application
through the separately installed HookLab watcher. x86 residents are outside the current HookLab
boundary - see [Supported runtimes](#supported-runtimes).

## Resident model

dgSpy initializes one resident HookLab runtime in the selected process. Initialization is idempotent,
selects its own safe arrival path, verifies the resident command channel, and restores the process's
previous running or paused state. Creating the first hook can initialize the resident lazily.

The resident managed assemblies remain loaded until the target process or AppDomain exits. Removing all
hooks removes their behavioral effect but is not an assembly unload operation.

Every target method is guarded by exact module MVID, metadata token, signature, and original IL SHA-256.
The public `module_id` used to select the live module is opaque and session-scoped; rediscover it after
every attach and fail on zero or ambiguous resolution.

## Resident payload matrix

HookLab ships one payload file, and every assembly it injects travels inside it as hash-verified bytes.
The build generates a payload matrix describing that set, and it is the authoritative account of what
enters a target. Each entry records:

- the slot id and its role - contracts, resident, compiler, compiler-support, or patch engine;
- the carrier that holds the bytes: the bootstrap, or the resident probe nested inside it;
- the embedded resource name, target framework, and architecture;
- the runtime families the payload is valid on - `clrv4`, `coreclr`, `mono`, or any combination;
- the families on which the payload is a **fallback** rather than an insistence - see below;
- the exact assembly name, version, and public key token;
- provenance - the project or the pinned NuGet package and version it came from;
- the other slots it needs at run time, and its SHA-256.

The runtime axis is real, not decorative: the patch engine is `net48` Harmony on CLR v4 and Mono and
`net6.0` Harmony on CoreCLR, compilation is CodeDom on CLR v4 and Roslyn on CoreCLR and Mono, and
several Roslyn support slots are valid on CLR v4 and Mono but not on CoreCLR, which supplies them
itself. The matrix is where those differences are stated once, instead of being recoverable only by
reading the loaders that branch on them - and the resident honours them, serving only the slots
declared valid on the runtime it is living in.

### Carried, or a fallback for what the runtime lacks

A payload set is a fallback for what a runtime does not supply. **What a runtime lacks is a property of
a build, not of a family**, so the family flags alone could not express it - and stating it statically
would be wrong whichever way it was stated:

| | `System.Memory` | `System.Buffers` |
| --- | --- | --- |
| mono-project Mono 6.12 x64 | 4.0.1.1 - older than the pinned Roslyn's reference | absent |
| the Mono a Unity 2021.3 player embeds | 4.0.99.0, in `Facades` | 4.0.99.0, in `Facades` |

Not carrying them broke mono-project's Mono. Carrying them broke Unity's, and not by being missing:
two `System.Memory` assemblies in one AppDomain make `ReadOnlySpan<T>` two distinct types, so Roslyn's
own `ImmutableArray.Create<T>(ReadOnlySpan<T>)` overload became unfindable and every compiled hook
failed. Nothing was wrong with either assembly. They were simply not the same type any more.

So the matrix carries a second, narrower runtime axis. A slot may declare a **fallback set**: a subset
of the families it is valid on, on which it defers to a runtime-supplied assembly *if one can satisfy
the identity payload code actually references*. On such a family the resident asks the CLR binder for
the payload's exact declared identity **before** byte-loading anything, and the answer decides:

- **Nothing can satisfy it.** Ordinary probing fails, the CLR raises `AssemblyResolve` - which it only
  ever does as a last resort - and the resident serves its own digest-verified bytes, through the same
  code as a slot that was never a fallback. This is not a deferral, and the report says `carried`.
- **The runtime supplies one.** The embedded copy is never loaded, *and its manifest entry is removed*,
  so no later bind can be answered with a duplicate. A manifest entry is a standing promise to answer,
  and a promise to answer with a second copy of an assembly already in the domain is the defect, not a
  spare tyre.

The question is deliberately the exact identity and not the simple name. "Does this runtime have
something called `System.Memory`" is the wrong question - mono-project's Mono has one, at a version
that cannot satisfy the reference - and asking it by simple name would *cause* that unusable assembly
to load, creating the very duplicate this exists to prevent.

An answer *older* than the declared identity is reported, not refused, and the distinction matters. For
a carried slot an older assembly is refused, because there our bytes are the ones in play. On a fallback
the binder has already chosen the runtime's copy, so refusing protects nothing and only breaks a target
that works - measured on mono-project's Mono, which answers a request for `System.Numerics.Vectors`
4.1.6.0 with its own 4.0.0.0 and has satisfied the pinned Roslyn that way for as long as that leg has
existed. The same runtime refuses its own too-old `System.Memory` and lets the embedded copy serve, so
any rule treating the two alike would be wrong about one of them. The deferral line says which version
answered and whether it was older than the reference, which is what makes a later `MissingMethodException`
readable instead of mysterious.

**Deferral is never silent.** Every start reports `payload_deferrals`, empty when nothing deferred and
otherwise naming each slot and the full identity that answered instead. "It worked" is not evidence of
which copy was used, and on a runtime whose facades differ between builds that distinction is the whole
question. The compatibility probe surfaces the same line per leg.

#### Why this does not weaken provenance

Deferral is confined by the parser to the `compiler-support` role, and a matrix that marks any other
role a fallback is refused at parse time, in the resident and in the packaging tool alike. dgSpy's
contracts, its resident, its pinned compiler and its patch engine - the assemblies whose provenance the
whole bootstrap exists to guarantee - can never be satisfied by something found in the target. A
compiler-support slot is a different kind of thing: a versioned BCL facade whose only correct number of
copies in a process is one.

Within that role the deferral is still not opportunistic discovery. The resident never enumerates the
target's filesystem, never widens a search path, and never accepts an assembly by name: it asks the
binder one question, using the identity the matrix declares - name, version and public key token, the
same triple the packaging tool proves against the shipped bytes in both directions - and refuses
anything answering below the declared version rather than running compiled hooks against it. What it
defers to is, by construction, exactly what payload code's own references would have bound to a moment
later anyway. The change is that exactly one copy is loaded and the report names it.

`clrv4` is deliberately not a fallback family for these slots even though the same reasoning would fit:
.NET Framework 4.8 has neither facade, no target has ever been measured supplying one, and the CLR v4
leg must not acquire a conditional on an unmeasured hypothesis. CoreCLR does not declare these rows at
all. Both take a code path with no new branch in it.

Three things verify it, at different times and against different evidence:

- The build fails if a declared slot has no file, so a payload cannot go missing silently.
- Composition and every later `verify` read the matrix out of the packaged payload and prove it against
  that payload's own bytes - digest and assembly identity per slot, and, in the other direction, that no
  embedded payload resource is undeclared. An unmanifested resident DLL fails the package. The layout's
  `hooklab/hooklab-payload-matrix.json` is a projection of that verified matrix, and verification
  re-derives it rather than trusting it.
- The resident parses the matrix at load time and serves only the identities it declares, from embedded
  bytes, after verifying each digest and version.

A modern .NET target framework may not claim `clrv4`; the reverse is deliberately not asserted, because
the pinned `net462` `System.Collections.Immutable` really does load in a CoreCLR target.

Note what the matrix does not claim. It records that a payload is *valid* on a runtime family, not that
every payload is *used* on it - and on a fallback family, not even that it is *loaded*; only a live run's
`payload_deferrals` can say that. It is not a substitute for the packaged live hook lifecycle gates
either: it fails a wrong payload set earlier and by name, not instead. Three kinds of evidence stay
distinct here, and the fallback axis is exactly where they diverge. Source coverage says the rule is
implemented and its boundary asserted; package verification says the shipped bytes are the declared
ones; only running on a particular runtime build says which copy that build actually supplied.

## Compilation boundary

Compilation happens in the target, with the compiler the runtime has: CodeDom on CLR v4, Roslyn on
CoreCLR and on Unity - whose Mono has a CodeDom, but one that shells out to an `mcs` no player ships.
CodeDom is a .NET Framework facility a CoreCLR process cannot find, and Roslyn travels in the payload
precisely because CoreCLR has no CodeDom.

They are separated by construction rather than by discipline. One runtime-neutral boundary carries an
assembly name, source, reference paths and an optional patch-engine reference path - all strings - and
returns a loaded assembly or bounded string diagnostics. Each compiler lives alone in its own type, and
a metadata test refuses any runtime-specific compiler type appearing in a base type, interface, field,
property, method or constructor signature on the shared side, including inside array element types and
generic arguments. Selection reads the corlib name and whether `Mono.Runtime` exists - the corlib name alone cannot tell
Unity from CLR v4, because Mono calls its corlib `mscorlib` too - and builds one backend without
preparing the other.

Host-side compilation is deliberately not offered. It would need its own decision about exact target
reference identities, compiler recipes, hashes, and trust.

## A failed initialization does not orphan a resident

Initialization records the resident's endpoint identity - pipe, nonce, credential, probe instance - in
the protected discovery store the moment the resident publishes it, before anything is attempted
against it. Everything after that point is therefore recoverable: a refused authentication, a lost
pipe, or a host that goes away leaves a resident that the next `initialize_hooklab` discovers and
adopts, rather than one that is up, authenticated, listening, and reachable by nobody.

A timed-out initialization keeps looking for a short bounded window before giving up, for the same
reason. A resident that published late is adopted and reported as ready, because it is - and because
abandoning a live authenticated resident is worse than answering slowly. That window is only ever
reached by a failing initialization.

Staged files from a failed initialization are preserved as evidence and removed after fourteen days.
Preserving them is deliberate: they are the only record of what a target was offered. Removing them
eventually is equally deliberate, because nothing else did.

## Resident stages and refusal reports

A resident refusal report names the stage it failed in, before it names anything else. The stages are
stable strings: `parameters`, `payload_verify`, `dependency_resolution`, `patch_engine_load`,
`residency_commit`, `behavior_commit`, `retirement`, and `precondition` for a refusal made before any
work began.

The stage comes first because it is what makes the rest readable - "could not load file or assembly"
means one thing while the payload closure is being resolved and another once the probe is committing
residency. Alongside it the report carries the exception type and message, up to three inner exception
links, whether payloads are resident, endpoint teardown and command quiescence state, and whether a
retained cleanup can still be retried. The chain is bounded because the report travels through a file
and a pipe, where an unbounded one is a denial of service rather than a diagnostic.

`patch_engine_load` is a real stage rather than a label because the bootstrap loads the pinned patch
engine itself, before any probe type is prepared. The target guard runs immediately before it, under
`residency_commit`: the engine is the first thing a resident puts into a process, and it is not put
into one that has not been proved to be the target.

Finer stages the resident cannot honestly distinguish - compiler creation, compile, patch install -
happen past the boundary where the bootstrap can still tell them apart, and are not claimed.

## Supported runtimes

CLR v4 has one version. CoreCLR does not, so the versions HookLab is supported on are stated rather
than left implied by the word "CoreCLR":

| Runtime family | Supported | Evidence |
| --- | --- | --- |
| CLR v4 (`v4.0.30319`), x64 | yes | packaged CLR v4 HookLab live gate, cross-identity gate, compatibility probe |
| CoreCLR 10.0 up to but excluding 11.0, x64 | yes | packaged CoreCLR HookLab live gate, cross-identity gate, compatibility probe |
| Any other CoreCLR major version | no | none - a version outside every supported range is refused by name |
| Mono 6.12 up to but excluding 6.14, x64 | yes | Mono HookLab live gate against both builds, compatibility probe's mono leg; see below |

Mono is a supported HookLab runtime, standalone and embedded alike. The resident arrives the same way
it does on CoreCLR - one debugger evaluation - and the soft debugger places that evaluation on an owned
internal breakpoint of its own, exported by the Mono engine beside the CorDebug one.

Four earlier reasons a Mono resident was thought impossible are gone, and a fifth - arrival - turned out
to be one import shape and three event-suspend deadlocks rather than a missing capability:

- Unity's Mono implements neither `WindowsIdentity.GetCurrent().User` nor
  `PipeSecurity.AddAccessRule`, so the control endpoint's access control cannot be built the managed
  way. It is now built through the Win32 API instead, producing a descriptor byte-identical to the one
  CLR v4 produces - verified by reading it back off the kernel object, not by trusting the request.
  CLR v4 and CoreCLR keep their managed construction untouched.
- Mono crashes at process exit if a listener thread is still unwinding out of a disposed pipe, which
  HookLab's deliberately non-blocking endpoint teardown used to leave behind. Retirement now waits for
  the listener on the runtimes that need it and reports `listener_teardown`; CLR v4 and CoreCLR take
  the `not_required` path and are unchanged.
- Compilation on Mono took the CodeDom path, because the selector keys on the corlib name and Mono's is
  also `mscorlib` - and Mono's CodeDom shells out to an `mcs` no player ships. Mono now selects the
  payload's own Roslyn, which needed more declared slots: `System.Reflection.Metadata` and
  `System.Runtime.CompilerServices.Unsafe`, which CoreCLR has in its shared framework and CLR v4 never
  asked for because it never loads Roslyn.

`mono` is a real family in the payload matrix - one family covering standalone Mono and the Mono a
Unity player embeds - and the resident serves only the slots declared valid on the runtime it is living
in, so a slot added for one runtime can no longer break the bind on another. Where two *builds* of Mono
differ in what they supply, the fallback axis above decides per target and reports which copy answered.
Both builds run the full lifecycle: mono-project 6.12 carrying both facades, Unity 2021.3's 6.13
deferring both to its own 4.0.99.0.

### Arrival on Mono

Nothing about the soft debugger ever blocked arrival. It places engine breakpoints for every stepper,
through the same callback shape the CorDebug bridge uses. What blocked it was that dgSpy could not ask:
the owned-breakpoint contract lived in the CorDebug contracts assembly and was imported as a singleton,
so there was exactly one implementation by construction. It is now an engine-neutral contract imported
`ImportMany`, and each engine exports its own provider.

Three deadlocks stood behind that, all one bug wearing three hats: **an event whose handler suspends the
VM and waits for a Run cannot be raised by a func-eval**, because the only thread that could issue that
Run is the one parked inside the invoke. The engine already knew this for exceptions. It did not know it
for the three events a resident arrival necessarily raises:

| Event | Raised by | Symptom before |
| --- | --- | --- |
| `AssemblyLoad` | loading the payload | `Assembly.Load(byte[])` timed out at the evaluation deadline; the same call takes 6 ms when the target runs it itself |
| `ThreadStart` | the resident's listener and worker | arrival got past the payload and stopped at residency commit |
| `UserLog` | an ordinary `Debug.WriteLine` under residency commit | the VM ran for about a second, then froze for the whole deadline |
| `Breakpoint` | the target re-entering a method an atomic action still has an owned breakpoint on | `suspendCount` climbed 1, 2, 3, 4 across four hits and the evaluation died on its deadline |

Each now declines to suspend while an evaluation is in flight, and raises its message without waiting
for a Run. `TypeLoad`, which arrives in a flood behind every assembly load, never suspended in the first
place. The breakpoint case is also what every other debugger does: breakpoints do not fire while an
expression is being evaluated. It is reached in ordinary use, because an atomic action places an owned
breakpoint, waits for the hit, and then evaluates with the other threads running - so the target's own
loop can re-enter that same method mid-evaluation.

Two smaller differences were real and are stated where every other runtime difference lives, in the
backend table:

- **The application domain identity.** The resident asserts `AppDomain.CurrentDomain.Id` about itself
  and the host names the domain it intends; the guard compares them. dnSpy's Mono engine numbers
  application domains from a counter of its own - it says so - and Mono's root domain is 0, not the CLR
  default domain's 1. Arrival refused with `Guard 'appdomain_id' mismatch. Expected '1', actual '0'`,
  which was the guard working correctly on a value the host had no business asserting. The backend now
  states the runtime's own numbering, and a caller naming a specific domain on an engine that invents
  its ids is refused rather than guessed at.
- **The target must still be able to exit.** A resident outlives the debugger by design: it stays
  loaded and its endpoint stays up until retirement. CLR v4 and CoreCLR abandon its background listener
  when the process goes down; Mono does not, and a target that had hosted a resident never exited at
  all. The resident now releases its endpoint on `ProcessExit`, on the runtimes that need it. For a game
  this is the difference between a window that closes and one that does not.

#### A stopped target cannot answer its resident

This is inherent, not a defect, and it is the one behaviour worth understanding before hooking a real
game. The resident answers dgSpy over a control channel **from inside the target**, on the target's own
threads. While the target is stopped - at a breakpoint, on a paused session, or on an unhandled
exception - it cannot answer at all. `install_hook`, `update_hook`, `remove_hook` and `get_hook_events`
all wait, and after 30 seconds dgSpy reports `hooklab_resident_unresponsive`, naming the operation and
pointing at the session state. The target is left alive and the resident recovers as soon as it runs
again.

The case that surprises people is the third one. **An unhandled exception in the target stops the
target**, because that is what a debugger is for - and a target that has been stopped that way looks,
from the outside, exactly like a resident that has failed. `state=paused` is the tell. It is worth
checking before suspecting HookLab: the long investigation recorded in
`docs/local/evidence/2026-08-21-road1-mono-arrival.md` ended at an unhandled `IOException` thrown by
dgSpy's *own test fixture*, which had been stopping itself the whole time.

**Do not try to resume the target from the host to get an answer out of it.** Both obvious repairs -
the engine's own run reconciliation, and an ordinary manager-level continue - killed the target outright
within 80 ms, in separate 40-cycle runs. A VM suspended with a control command in flight has to be left
alone.

The 30-second bound is what keeps this legible. Before it existed the host waited past every deadline it
had, because `Task.Run(..., token)` cancels only a call's scheduling and `PipeStream` cannot time out a
read: one such wait took a 900-second harness timeout to end and said nothing about which call caused
it. `tests\run-mono-hooklab-stress.ps1` is the instrument for anything in this area - it cycles a hook
through install, update and remove dozens of times and, on a stall, reports whether the target is alive,
whether its own unrelated threads are still advancing, and what the session state is. Those three
answers separate a wedged resident from a suspended VM from a dead target, and getting them confused
sends an investigation a long way in the wrong direction.

A range is a claim that a packaged live hook lifecycle has actually run there. Adding one needs its own
evidence, not an expectation that it should work.

## Resident compatibility probe

The compatibility probe drives one complete resident lifecycle against a real CLR v4 target and a real
CoreCLR target, in a couple of seconds each, with no dnSpy and no GUI:

```powershell
dotnet build tests\TestTargets\HookLabProbeTarget\HookLabProbeTarget.csproj -c Release
dotnet run --project tests\HookLab.CompatibilityProbe -c Release -- --negative
```

Each leg runs named stages - `payload_verify`, `backend_selection`, `target_launch`, `runtime_range`,
`residency_commit`, `listener_ready`, `authenticated`, `compile`, `behavior`, `event`, `remove`,
`retire` - and a failure reports the stage plus every payload identity that stage had selected. That is
the point of it: a wrong patch engine or an unresolvable compiler dependency used to appear only as a
timeout inside a packaged live gate, long after everything else had run. `--negative` additionally
requires a payload with one byte changed to be refused, so the verification stage cannot pass vacuously.

Two things it deliberately does not prove, because the packaged live gates own them and remain
authoritative:

- **Arrival.** The target loads the payload into itself. That is the CoreCLR delivery path minus the
  debugger, and it is not the CLR v4 path at all, which arrives through the native bootstrap.
- **Anything about dnSpy** - the extension, the GUI, the MCP surface, or session handling.

What it does prove is everything downstream of arrival, which is where the runtimes actually differ:
payload selection from the matrix, the dependency closure, compiler creation and a real compilation,
the pinned patch engine loading, live behavior change, event capture, removal, and clean retirement.

### The Mono leg

`--runtime mono` runs the same fixture on a real Mono and drives the same complete lifecycle the other
two legs do - compile, install, observe, remove, retire, target still alive. Like them, it proves
everything downstream of arrival and nothing about arrival itself, which is why a green Mono leg does
not by itself make Mono a supported HookLab runtime.

It is opt-in and is not part of `--runtime all`, because it needs a Mono runtime this repository does
not ship, and it is never discovered by scanning the machine - which Mono happens to be installed must
not decide what was measured. With mono-project's x64 Mono installed it needs nothing else:

```powershell
$env:DGSPY_MONO_EXE = 'C:\Program Files\Mono\bin\mono.exe'
dotnet run --project tests\HookLab.CompatibilityProbe -c Release -- --runtime mono
```

x64 matters: the resident's architecture guard refuses an x86 target, so a 32-bit Mono is rejected
rather than silently measured.

`--mono-runtime` and `--mono-assemblies` exist for the Mono a Unity player embeds, which cannot be run
the simple way. Unity ships `mono.exe` as x86 only, so an x64 Unity-Mono target needs a small host that
loads the player's x64 runtime and calls `mono_main` - `MonoHost64`, in `tests\TestTargets\MonoHost64` -
and Unity's own `mono.exe` class libraries are not a player's either: they are a CoreFX-derived set
whose named-pipe servers P/Invoke a `System.Native` shim absent on Windows. The editor's
`unityjit-win32` profile is byte-identical to a shipped player's `Managed` directory, which is what
makes that variant accurate without a game.

Both variants pass, and the leg reports what distinguishes them. `payload_deferrals` is `none` on
mono-project's Mono, which supplies neither facade usably and gets both from the payload
(`payload_load_count=9`); on Unity's it names `System.Memory` and `System.Buffers` at 4.0.99.0 and the
load count drops to 7. That line is the leg's only evidence of *which* copy was used, and until the
fallback axis existed the Unity variant did not pass at all: two `System.Memory` assemblies in one
domain split `ReadOnlySpan<T>` identity and Roslyn could not find its own `ImmutableArray.Create`
overload. See [Carried, or a fallback for what the runtime lacks](#carried-or-a-fallback-for-what-the-runtime-lacks).

## Mono HookLab live gate

The probe leg above loads the payload into its own fixture, which is the delivery path minus the
debugger. `tests\run-mono-hooklab-smoke.ps1` is the part it structurally cannot cover: the resident
arrives the way the product delivers it - one debugger evaluation placed on an owned internal
breakpoint - and the gate then drives the whole lifecycle over the resident's own control channel.

It runs against a fixture this repository builds (`tests\TestTargets\MonoHookLabTarget`) under a Mono
the caller supplies, and it takes the same `-MonoRuntime`/`-MonoAssemblies` parameters the probe does,
so one command covers both Mono builds:

```powershell
$env:DGSPY_MONO_EXE = 'C:\Program Files\Mono\bin\mono.exe'
.\tests\run-mono-hooklab-smoke.ps1
```

That ordering is deliberate. Mono is the runtime; a Unity player merely embeds it, and arrival is
identical in both. Making the general case the gate - and Unity a variant of it - is what stops "does
HookLab work on Mono" from being answerable only on a machine with a Unity editor and a licence.
`tests\run-unity-hooklab-smoke.ps1` remains, and adds the one thing a fixture cannot: a real player,
with a game loop, a mod loader and a graphics thread.

The gate asserts the target is on Mono from the runtime's own answer rather than from having launched
`mono.exe`, so a launch that silently fell through to the CLR fails rather than passing quietly. It
ends by stopping the target and requiring a clean exit, because a process that has hosted a resident and
cannot exit is a defect this runtime actually had.

## Identity and the control channel

The debugger and its target are not assumed to be the same Windows account. Two principals are read
once per initialization - the controller, which is the process driving HookLab, and the target, read
from its own process token - and every authority decision derives from that pair rather than from
whichever process happens to be asking.

Two things follow from it:

- **The exchange area.** Staged payloads, `initialize.params` and the resident's completion report share
  one directory. When the principals match it is the controller's temp directory. When they differ it is
  a machine-wide directory created for that operation, with a protected DACL naming exactly those two
  principals, and removed with the operation. A failed initialization keeps it and names its path in the
  error rather than deleting the only record of what the target was offered.
- **The resident's control endpoint.** The pipe DACL is protected and fully enumerated: the target's own
  SID, plus the controller as a second named principal when the two differ. Nothing is relaxed and no
  principal is replaced; when the accounts match, the DACL is what it always was.

That DACL is defence in depth rather than the authentication boundary. The channel is already
authenticated by a 32-byte secret injected with the payload, and whoever writes `initialize.params` has
already chosen the bytes the target will execute - so they are strictly more privileged than anything
the DACL could grant. It exists so that a target running as another account does not build an endpoint
its own controller cannot open.

Authoring a grant is not the same as holding one. SID equality does not establish effective access, and
neither does reading back a DACL just written: restricted SIDs, deny-only groups, deny ACEs, integrity
level and impersonation state all sit between an intended grant and a usable one. dgSpy reports such a
precondition as not provable before the attempt rather than as satisfied. Connectivity is established by
an authenticated round trip, and the target's access to the exchange area by the target reading it.

## Supported hooks

HookLab supports:

- bounded observation Prefix, Postfix, and Finalizer hooks;
- compiled custom C# Prefix and Postfix hooks, including a paired Prefix/Postfix source document;
- compiled exception-preserving, replacing, or suppressing Finalizers;
- compiled Harmony `IEnumerable<CodeInstruction>` Transpilers.

Harmony supplies its normal binding conventions, including named original arguments, `ref` mutation,
`__instance`, `__args`, `__result`, paired `__state`, `___fieldName`, `__exception`, and
`CodeInstruction`. HookLab does not add a parallel argument-binding or IL-rewriting layer.

Compilation occurs in the target context. A candidate is compiled and validated before it replaces the
active patch. A failed create changes nothing; a failed update preserves the last good source, revision,
enabled state, diagnostics, and active behavior. Revisions increase monotonically. Enable and disable
preserve compiled state and do not recompile on enable.

Observation and compiled hooks may coexist on the same method and phase without removing each other's
Harmony ownership.

## Product surfaces

The MCP surface includes:

- `initialize_hooklab` and `get_hooklab_status`;
- `get_hooklab_readiness`, which evaluates every precondition `initialize_hooklab` requires without
  touching the target. Each precondition answers `satisfied`, `failed`, or `not_provable_preflight`,
  and a failure names the exact precondition initialization would refuse with. It never reports that
  the payload will load: at its strongest it reports **no known incompatibility**, because the
  target-side loader is what decides. It is the same computation initialization is gated on, so it
  cannot drift from the path it describes;
- `get_hook_template`;
- `install_hook`, `create_hook`, and `update_hook`;
- `list_hooks`, `enable_hook`, and `disable_hook`;
- `get_hook_events`;
- `remove_hook` and `remove_all_hooks`;
- `export_hook_package`.

The GUI uses the same service directly rather than calling MCP. Method context commands create
observation or custom C# hooks. The HookLab window exposes real initialization, editing, enable/disable,
show, event, and removal operations. Observation rows are deliberately non-editable. Method glyphs show
single or multiple installed hooks without guessing which row a click should mutate.

## Standalone watcher

`HookLab.Injector` is the package-neutral standalone injection and reconciliation boundary.
`HookLab.Watcher` validates immutable packages and editable profiles, discovers matching processes,
applies desired hooks, adopts authenticated residents, records bounded status/audit output, and supports
pause and profile enable/disable. The separately packaged, unelevated `HookLab.Watcher.Companion`
presents exact lifecycle state, pause/resume, profile enable/disable, installed-task start/restart, and
the bounded audit tail in the notification area. Exiting the companion does not stop the elevated,
headless watcher. Both run without dnSpy, the Gateway, or MCP.

The supported package contains a closed watcher layout and installer. See
[HookLab watcher installation and operation](../guides/HOOKLAB_WATCHER.md) for installation, task supervision,
commands, upgrade behavior, and uninstall guarantees.

`export_hook_package` freezes one retained compiled dgSpy hook into a canonical watcher deployment below
`DGSPY_EXPORT_ROOT`. It revalidates the exact live process and guarded method identity, writes a closed
package, and creates a disabled sibling profile. Export never enrolls or enables automatic deployment.
The watcher can atomically enroll that canonical export into its separately protected enrollment root.
Enrollment verifies the deployment again, rejects identity conflicts, and durably disables the profile
before publication; automatic application begins only after an explicit `enable-profile` command.

The debugger-integrated and standalone adapters currently retain separate resident ownership paths.
Neither may inject a competing generation merely because it cannot adopt a resident it discovered.
Authenticated cross-adoption and preservation of foreign-owned hooks remain roadmap work.

## Current limitations

- Generated templates do not support generic methods or methods on generic declaring types.
- Resident compilation references expand only for a concrete target the current target-AppDomain set
  cannot compile.
- Source size remains bounded by the bootstrap parameter contract.
- The editor provides C# classification and normal editing, but not a HookLab-owned Roslyn project,
  semantic completion, or advisory Roslyn diagnostics.
- Generated parameters use conservative verbatim identifiers rather than polished conditional escaping.
- Cross-adoption between watcher-created and dgSpy-created residents is not yet a supported workflow.

These limitations are recorded and ordered in [Dream of roads](../roadmap/dream_of_roads.md). Detailed tasks,
investigations, handoffs, and live evidence belong in the separate `docs/local` work repository.

## Safety invariants

- Initialization and hook mutations must preserve the target's prior execution state unless the caller
  explicitly requests a debugger state change.
- Disconnect, timeout, watcher exit, upgrade, and uninstall do not implicitly resume, detach, terminate,
  restart, remove hooks, or modify target binaries.
- Unknown and foreign-owned resident hooks are preserved.
- Exact target guards fail closed; display names and first matches are never identity.
- Elevated watcher inputs come only from closed, verified, immutable installation or enrollment
  inventories. Mutable control, status, and audit state remains separate.
- Loaded resident payloads are retired only after their exact target identity exits.
