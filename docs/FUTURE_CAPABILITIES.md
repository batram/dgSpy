# Future high-risk capabilities

These capabilities are deliberately unscheduled. Implement one only for a concrete workflow after the
remote authorization and audit model can grant it independently. None is a sandbox, and no permission
implies another.

HookLab now delivers guarded observation and compiled Prefix, Postfix, Finalizer, and Transpiler C#
through its bounded x64 desktop-CLR-v4 resident. That custom source is unsandboxed target-code execution,
but it is already governed by HookLab's exact target guards, lifecycle, and permissions. The sections
below govern *general* execution outside HookLab, assembly editing/live replacement, and host scripting;
they do not describe missing HookLab work. See the delivered boundary in the
[HookLab product documentation](HOOKLAB.md).

## Target C# execution

Execute caller-supplied C# in an explicitly selected debug target.

### Tier 1: debugger expression evaluation

Build on `evaluate`, `invoke_method`, and `create_object`. Publish the expression forms supported by the
active engine, require exact target/frame selection when ambiguous, and distinguish compiler, runtime,
timeout, and recovery failures. This tier does not accept compilation units, declarations, or statement
blocks.

### Tier 2: injected payload

Compile bounded C# outside the target, validate an allowlisted reference set, transfer a digest-verified
payload, load it into the exact runtime, and invoke only its declared entry point. Require an expected
state version and a dedicated `execute_target_code` permission. Report when a failure may already have
modified the target; loaded .NET Framework assemblies are assumed to remain until process exit.

Start with a disposable x64 CorDebug fixture. Mono/Unity remains unsupported until separately proven.
Gate compilation, transfer, invocation, hard timeout, debugger recovery, restart cleanup, ambiguity,
stale state, limits, audit redaction, and remote-default-off policy.

## Assembly editing, project export, and live patching

Expose dnSpy/dnlib services without automating WPF.

HookLab's validated source-project-plus-exact-DLL export does not authorize general assembly editing,
overwriting target artifacts, or debugger/JIT method-body replacement. Those remain separate
capabilities with separate permissions and acceptance gates.

- Use copy-on-write edit transactions keyed by exact document identity and original SHA-256.
- Preview deterministic manifests and diagnostics before writing.
- Write a new validated artifact by default; overwriting requires a separate host-write permission.
- Export C# projects through a temporary destination and publish only a complete validated result.
- Constrain canonical output paths to configured roots and report hashes, signing consequences, partial
  results, and changed symbols.
- Treat live method-body replacement as a separate capability. Require a paused exact target, unchanged
  signature/shape, expected state version, original body hash, and engine proof. Structural changes are
  artifact-only; rollback cannot be guaranteed after new code executes.

Gate byte-identical inputs, reloadable outputs, resources and metadata, traversal and overwrite refusal,
invalid C#/IL, signing policy, deterministic manifests, and compatible CorDebug replacement/rollback.
Mono/Unity live replacement remains unsupported until tested against a disposable fixture.

## dnSpy-host C# scripting

Run Roslyn scripting inside dnSpy, not inside the debug target. This grants code execution as the dnSpy
user and therefore has the broadest host trust boundary.

- Wrap a non-UI scripting service; never open or automate C# Interactive.
- Default to a fresh stateless context. Persistent contexts are opt-in, scoped to authenticated caller
  plus host, serialized, generation-checked, resettable, and cleared on shutdown or policy revocation.
- Capture per-request output without permanently replacing process-wide streams.
- Bound source, references, output, diagnostics, return serialization, duration, queues, and contexts.
- Require `execute_dnspy_scripts`; keep remote use disabled unless explicitly allowed.
- Audit identities, mode, generation, source digest, duration, outcome, and cancellation state without
  recording source, output, returned secrets, or inspected values by default.

Do not advertise safe cancellation if in-process Roslyn execution cannot be stopped and the host restored
to a known state. Gate stateless isolation, persistent caller isolation, reset behavior, limits,
authorization, audit redaction, and explicit documentation of host-level execution.

## Shared prerequisites

- Stable host identity and session ownership.
- Authenticated extension RPC and encrypted remote transport.
- Separate policy bits for inspection, control, mutation, termination, export, target execution, artifact
  editing, host writes, live patching, and host scripting.
- Bounded request/response sizes, deadlines, queues, and concurrency per host and runtime.
- Redacted audit records with caller, target, digest, outcome, and possible-side-effect state.
- Capability advertisement and end-to-end refusal tests for unsupported engines and unauthorized callers.
