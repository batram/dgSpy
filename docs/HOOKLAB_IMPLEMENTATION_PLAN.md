# HookLab implementation plan

HookLab is a standalone dnSpy GUI extension and target-side runtime-hooking system. It lives in this
monorepo, but its UI, protocol, compiler, and runtime probes remain separate assemblies. The GUI
extension targets both the default net10.0-windows dnSpy host and the retained net48 host; its target
probe and target runtime are separate concerns. The first supported target runtime is x64 CLR v4/.NET
Framework 4.8; Unity/Mono follows only after the CLR acceptance gate is green. The dgSpy integration
and atomic bootstrap are specified in
[DGSPY_EXTENSION_PROVIDER_PLAN.md](DGSPY_EXTENSION_PROVIDER_PLAN.md) and
[DGSPY_ATOMIC_ACTIONS_PLAN.md](DGSPY_ATOMIC_ACTIONS_PLAN.md). Existing dgSpy tools and contracts remain
unchanged until these milestones are implemented and accepted.

## Product and assembly boundaries

| Assembly | Responsibility |
| --- | --- |
| `HookLab.Extension.x.dll` | net48/net10 dnSpy WPF UI, authoring, orchestration, event view, and package export. |
| `HookLab.Contracts.dll` | netstandard2.0 hook/package/probe DTOs shared by the HookLab host components and target probes. |
| `HookLab.Probe.CorDebug.dll` | net48 target probe using a pinned standard Harmony release. |
| `HookLab.Probe.Mono.dll` | Later Unity/Mono probe reusing a validated resident HarmonyX/MonoMod backend. |
| `HookLab.Compiler.exe` | Isolated Roslyn helper compiling against exact references on the selected host. |

Harmony and probe dependencies must not enter dnSpy's load context. The GUI and MCP provider use one
`HookManagerService`, so interactive and remote operations have identical validation and ownership.
The provider also references the separate `dgSpy.ExtensionContracts` host plug-in ABI; target probes
never reference or load that assembly, and `dgSpy.ExtensionContracts` contains no HookLab wire DTOs.
The first milestone supports prefixes, postfixes, and finalizers. Transpilers, reverse patches,
CoreCLR, x86, native debugging, profiler/ReJIT, and automatic process watching/native bootstrap are
explicit later work.

## Identities, guards, and state

A target identity contains `host_id`, canonical image path, PID, process creation time, architecture,
runtime identity, and selected AppDomain. PID alone is never sufficient. Multiple-AppDomain targets
initially require an explicit runtime/AppDomain selection.

Every hook target is guarded by all of:

- process and runtime identity;
- module MVID;
- metadata token;
- full declaring-type and method signature; and
- SHA-256 fingerprint of the expected IL body.

Any mismatch refuses installation or update. HookLab never searches for a "nearest" method. The
probe reports a stable `probe_instance_id`, stable `patch_id` values, and a monotonically increasing
`hooks_version`. Hooks remain active after debugger detach; removal is always explicit. Process exit,
AppDomain unload, or identity mismatch invalidates the resource and PID reuse cannot revive it.

Harmony cannot intercept calls already inlined into callers. HookLab reports known or likely inlining
and offers validation hooks at callers; it never claims complete interception.

## Atomic probe bootstrap

Probe installation is one specialized debugger-host operation, not a client composition of public
evaluation calls:

1. Resolve and validate the exact target, runtime, AppDomain, module, and method guard.
2. Stage a hash-verified bootstrap with an embedded dependency bundle beneath a configured host cache
   using canonical-path and reparse-point protections.
3. Run naturally to a declared managed location and select an evaluatable managed frame, using only
   a declared bounded nearby-slot search when necessary.
4. Execute a fixed `Assembly.Load(byte[])` bootstrap whose dependency-free initializer installs a
   manifest-restricted resolver for embedded, hash-verified payload assemblies; then call the probe's
   versioned initializer and pass only bounded initialization data.
5. Verify assembly digest, protocol version, `probe_instance_id`, target identity, and pipe endpoint.
6. Release the internal breakpoint owner, settle debugger state, delete staged files where possible,
   and apply the declared deterministic resume policy.

The operation returns truthful terminal and ambiguity information defined by the atomic-actions plan.
Loaded assemblies normally cannot be unloaded from a .NET Framework AppDomain; unpatching removes
methods but not assembly generations. HookLab tracks generations and warns before repeated expert
recompilation.

## Probe transport and reconnection

HookLab and each probe communicate through an authenticated, bounded named pipe. The pipe has a
target-appropriate ACL and a deterministic discovery record keyed by full process identity and a
nonce. Connection establishment uses a challenge tied to a per-probe secret; secrets never enter
logs, manifests, or event payloads.

The initializer returns the secret once to the installing host. For restart continuity the host stores
it only in a discovery record protected with Windows DPAPI for the host account and a restrictive ACL;
the record also contains host ID, canonical image identity, PID plus creation time, runtime/AppDomain,
probe instance, endpoint nonce, protocol version, and expiry. Discovery validates every field against
the live target before attempting a pipe connection. Successful removal and target exit delete the
record; startup quarantines stale, malformed, replayed, or identity-mismatched records. Recovery rotates
the secret after authentication. This protects continuity credentials from other accounts and
accidental disclosure; it is not a sandbox against arbitrary code already running as the same user or
inside the target.

Messages are length-prefixed, versioned, size-limited, and schema-validated. Hook callbacks never
wait for the pipe. They append compact events to a bounded ring buffer and a worker performs delivery.
Overflow increments an explicit dropped-event count. Commands are serialized through the probe,
carry an expected `hooks_version`, and return the new version.

Pipe or dnSpy/Gateway disconnection preserves hooks. A restart discovers the probe, authenticates,
checks its identity and version, and reconciles state. An incompatible client may read a bounded
status response but cannot mutate hooks. State is never preserved across target exit or PID reuse.

## Hybrid authoring

### Declarative hooks

Declarative prefix, postfix, and finalizer documents support:

- bounded capture of arguments, fields, results, exceptions, counters, and timing;
- predicates, sampling, rate limits, and per-hook event filters;
- argument or result replacement and an explicit skip-original action;
- exception observation, replacement, suppression, or propagation;
- bounded retry policies with attempt limits, backoff, reentrancy guards, and predicates; and
- scheduling on the current thread, a WinForms control, or a WPF dispatcher.

Argument/result replacement, skip-original, and exception replacement are synchronous current-thread
actions. Work posted to a WinForms/WPF dispatcher is a later invocation and cannot retroactively alter
the original call's return value or exception. WinForms/WPF scheduling uses weakly held, validated
targets. Disposed controls, shutting-down
dispatchers, disconnected sessions, and process shutdown are cancellation, not retryable failure.
Probe-dispatch exceptions are caught and recorded. Hooks fail open where possible, preserve the
original method/exception unless an explicit successful action says otherwise, and auto-disable after
a bounded consecutive-failure threshold.

### Expert C#

The UI generates readable prefix/postfix/finalizer templates. `HookLab.Compiler.exe` runs as a
separate process on the selected local or remote host, uses exact target reference metadata, and has
bounds for source size, reference count and roots, diagnostics, output size, duration, and compiler
concurrency. Compilation isolation protects dnSpy stability; custom C# remains explicitly
unsandboxed code execution inside the target.

The helper produces deterministic patch DLL/PDB artifacts and digests. Generated patches avoid
unnecessary static target-type references and use generated adapters/reflection where loader context
requires it. Injection is hash-verified and uses the same atomic payload path as the standard probe.

## Bounded observation and hot-hook behavior

Each hook declares maximum event rate, total bytes, serialization depth, collection count, string
length, and retry/reentrancy limits. Value capture does not invoke property getters or `ToString()` by
default. Hot hooks require sampling or aggregation when configured thresholds are exceeded. Events
include `probe_instance_id`, `patch_id`, `hooks_version`, a monotonic sequence, timestamp, thread,
truncation markers, and dropped counts. Slow consumers cannot block target execution.

## Prototype export

A validated prototype exports both:

1. a readable generated source project containing manifest, generated source, exact reference
   metadata, tests, licenses, and build instructions; and
2. the exact compiled DLL/PDB and SHA-256 hashes used during live validation.

The canonical manifest records target guards, backend and versions, compiler identity/options,
artifact hashes, entry points, permissions required, and validation results. Export uses a configured
safe artifact root, canonical containment and reparse protection, fresh temporary paths, atomic
publication, and no overwrite by default. Remote artifacts use chunked hash-verified retrieval.
Manifests and entry points must already be compatible with the deferred automatic loader.

## Backend coexistence and licensing

The dependency-free bootstrap inventories loaded Harmony, HarmonyX, and MonoMod assemblies before any
backend-specific type is resolved. On CLR it reuses only an exact tested compatible standard-Harmony
identity; otherwise it loads the pinned embedded backend, and it refuses an incompatible resident
backend rather than loading a competing implementation.

The UCH/Mono implementation must bind to and reuse the compatible HarmonyX/MonoMod implementation
already loaded by BepInEx. It does not load a second patching implementation into that target. A clean
Mono fixture may load the separately pinned HarmonyX bundle, but UCH acceptance is absent until the
resident versions, API compatibility, owner isolation, patch/unpatch behavior, and conflict reporting
are proven as a matrix. An incompatible resident backend fails closed with its exact identity and no
target mutation.

Packages record the pinned Harmony license and notices, HarmonyX/MonoMod licenses if used, and the
origin and license of any UnityExplorer-derived Hook Manager code. License review is an acceptance
item, not a post-release cleanup.

## Delivery stages and acceptance

0. **Disposable feasibility spike:** at a manually prepared user breakpoint, use existing explicit
   evaluation/invocation primitives to load a throwaway probe into a net48 fixture and prove Harmony
   patch/unpatch, CorDebug coexistence, bounded pipe transport, dependency resolution, and clean target
   exit. The load test must prove func-eval completes across the nested module/assembly-load callbacks
   raised by `Assembly.Load`; successful loading outside func-eval is not sufficient.

   In the same stage, prototype the lowest engine layer needed to multiplex an internal logical owner
   with an existing conditional, trace, enabled, and disabled user breakpoint at the exact same
   location. Prove independent hit delivery, user-stop precedence, and ownership-scoped cleanup without
   mutating the user's settings. If dnSpy's engine cannot support that model, stop and redesign the
   atomic breakpoint contract before stage 1; do not defer the discovery behind a capability-absent
   fallback.

   This spike is not shipped, is not exposed as a workflow, and does not weaken the production rule
   that bootstrap is one specialized host action. Stop before the atomic/provider implementation if
   either the bootstrap or breakpoint experiment cannot establish these facts.
1. **CLR fixture and transport:** disposable net48 fixture, guarded atomic injection, authenticated
   pipe health, status, unpatch, and clean exit.
2. **Observation:** non-stopping prefix/postfix/finalizer events, bounds, counters, and GUI event view.
3. **Declarative mutation:** argument/result actions, skip, exception actions, bounded retries,
   failure auto-disable, and current-thread/WinForms/WPF schedulers.
4. **Expert compilation and export:** isolated selected-host compilation, injection, generation
   tracking, source-project-plus-exact-DLL export, and rebuild verification.
5. **dgSpy/MCP and remote operation:** typed and generic provider tools, permissions, independent
   leases, detach-with-hooks, reconnection, remote compilation, artifact transfer, and redacted audit.
6. **Unity/Mono:** equivalent applicable contract against UCH, safe detach, existing-Harmony conflict
   detection, and bounded Unity gates.

The CLR fixture must prove install from both net48 and net10 dnSpy hosts, observe, mutate, retry,
exception handling, expert compilation, unpatch, detach-with-hooks, post-detach control, reconnection,
export, and clean process exit. It also verifies debugger behavior after patching: breakpoints, stepping,
call stacks, `get_il`, and `get_csharp` either remain accurate or explicitly report that metadata and
execution have diverged.
VMConnect acceptance requires a non-stopping postfix that observes `SyncDisplaySettings()` failure and
schedules a bounded UI-thread retry without breaking fullscreen or waiting for an agent response while
paused. It is a validation target, not a bundled machine-specific patch.

All existing Protocol, Gateway, Extension, composition, CorDebug, and bounded Unity gates run
sequentially. GUI smokes run on the hidden desktop. Mono work begins only after every applicable CLR
gate is green.

Pipe acceptance covers same-integrity, elevated-host to medium-integrity target, the supported reverse
direction, unauthorized clients, discovery-record replay, and pipe-name squatting. Each delivery stage
is independently gated and may ship without later stages; expert compilation/export and Mono do not
expand the minimum CLR probe milestone merely because they appear in the same plan.
