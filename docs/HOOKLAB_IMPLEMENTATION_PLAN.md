# HookLab implementation plan

HookLab is a standalone dnSpy GUI extension and target-side runtime-hooking system. It lives in this
monorepo, but its UI, protocol, compiler, and runtime probes remain separate assemblies. The first
supported runtime is x64 CLR v4/.NET Framework 4.8; Unity/Mono follows only after the CLR acceptance
gate is green. The dgSpy integration and atomic bootstrap are specified in
[DGSPY_EXTENSION_PROVIDER_PLAN.md](DGSPY_EXTENSION_PROVIDER_PLAN.md) and
[DGSPY_ATOMIC_ACTIONS_PLAN.md](DGSPY_ATOMIC_ACTIONS_PLAN.md). Existing dgSpy tools and contracts remain
unchanged until these milestones are implemented and accepted.

## Product and assembly boundaries

| Assembly | Responsibility |
| --- | --- |
| `HookLab.Extension.x.dll` | dnSpy WPF UI, authoring, orchestration, event view, and package export. |
| `HookLab.Contracts.dll` | Versioned hook documents, package manifests, probe messages, and stable DTOs. |
| `HookLab.Probe.CorDebug.dll` | net48 target probe using a pinned standard Harmony release. |
| `HookLab.Probe.Mono.dll` | Later Unity/Mono probe using a separately validated Harmony or HarmonyX backend. |
| `HookLab.Compiler.exe` | Isolated Roslyn helper compiling against exact references on the selected host. |

Harmony and probe dependencies must not enter dnSpy's load context. The GUI and MCP provider use one
`HookManagerService`, so interactive and remote operations have identical validation and ownership.
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
2. Stage hash-verified payload files beneath a configured host cache using canonical-path and
   reparse-point protections.
3. Run naturally to a declared managed location and select an evaluatable managed frame, using only
   a declared bounded nearby-slot search when necessary.
4. Execute a fixed bootstrap equivalent to `Assembly.Load(File.ReadAllBytes(path))`, call the probe's
   versioned initializer, and pass only bounded initialization data.
5. Verify assembly digest, protocol version, `probe_instance_id`, target identity, and pipe endpoint.
6. Remove the temporary breakpoint, settle debugger state, delete staged files where possible, and
   apply the declared deterministic resume policy.

The operation returns truthful terminal and ambiguity information defined by the atomic-actions plan.
Loaded assemblies normally cannot be unloaded from a .NET Framework AppDomain; unpatching removes
methods but not assembly generations. HookLab tracks generations and warns before repeated expert
recompilation.

## Probe transport and reconnection

HookLab and each probe communicate through an authenticated, bounded named pipe. The pipe has a
target-appropriate ACL and a deterministic discovery record keyed by full process identity and a
nonce. Connection establishment uses a challenge tied to a per-probe secret; secrets never enter
logs, manifests, or event payloads.

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

WinForms/WPF scheduling uses weakly held, validated targets. Disposed controls, shutting-down
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

Before installation the probe inventories loaded Harmony, HarmonyX, and MonoMod assemblies. It refuses
an incompatible backend rather than loading a second patching implementation. Compatible coexistence
requires an explicit tested matrix and reports every owner/patch conflict. The Mono backend decision
is separate from the CLR decision and must be validated in BepInEx/UCH environments.

Packages record the pinned Harmony license and notices, HarmonyX/MonoMod licenses if used, and the
origin and license of any UnityExplorer-derived Hook Manager code. License review is an acceptance
item, not a post-release cleanup.

## Delivery stages and acceptance

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

The CLR fixture must prove install, observe, mutate, retry, exception handling, expert compilation,
unpatch, detach-with-hooks, post-detach control, reconnection, export, and clean process exit.
VMConnect acceptance requires a non-stopping postfix that observes `SyncDisplaySettings()` failure and
schedules a bounded UI-thread retry without breaking fullscreen or waiting for an agent response while
paused. It is a validation target, not a bundled machine-specific patch.

All existing Protocol, Gateway, Extension, composition, CorDebug, and bounded Unity gates run
sequentially. GUI smokes run on the hidden desktop. Mono work begins only after every applicable CLR
gate is green.
