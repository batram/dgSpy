# dgSpy Target C# Execution Plan

**Status:** separate future capability track; not implemented or scheduled. The main debugger roadmap remains
in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md). dnSpy-host scripting is a different trust boundary,
documented in [DNSPY_SCRIPTING_PLAN.md](DNSPY_SCRIPTING_PLAN.md).

## Objective and boundary

Execute caller-supplied C# inside an explicitly selected debug target. This capability is target code
execution, not dnSpy scripting and not a security sandbox. It uses a distinct `execute_target_code`
permission, is disabled for remote clients by default, and is always side-effecting and audited.

C# is the only supported source language. Visual Basic parity is out of scope.

## Tier 1: Enhanced function evaluation

Build on the existing `evaluate`, `invoke_method`, and `create_object` paths without describing debugger
expressions as arbitrary scripting.

1. Require explicit `process_id` and `runtime_id` whenever more than one target is active, plus the
   caller-selected thread and frame where the expression needs frame context.
2. Publish the C# expression forms the active engine accepts and return compiler diagnostics separately
   from runtime failures, timeouts, and recovery failures.
3. Preserve the current safe defaults: function evaluation and side effects remain opt-in except at the
   explicit invocation and construction tool boundaries.
4. Return target identity, completion state, value, diagnostics, duration, timeout/recovery state, and an
   audit ID for every side-effecting evaluation.

Tier 1 remains limited to the debugger expression compiler. It does not accept general compilation units,
method or type declarations, or arbitrary statement blocks.

## Tier 2: Injected C# payload

Add `execute_target_csharp` as an explicit high-risk tool:

1. Accept bounded C# source, a declared static entry point, an allowlisted reference set, exact
   `session_id`/`process_id`/`runtime_id`, a hard timeout, and an expected `state_version`.
2. Compile source outside the target into a bounded payload assembly. Return compilation diagnostics and
   do not touch the target when compilation or policy validation fails.
3. Transfer the payload through bounded RPC chunks, verify its SHA-256 at both ends, load it into the
   selected runtime, and invoke only the declared entry point.
4. Return the serializable result, diagnostics, duration, payload digest, audit ID, target state, and a
   flag stating whether the target may already have been modified.
5. Serialize injection and invocation per runtime. Reject ambiguous, running, stale, unsupported, or
   concurrently mutating targets before loading the payload.

No API promises payload unload or rollback. On .NET Framework, assume a loaded assembly remains until the
process exits; restart is the dependable cleanup boundary. If timeout, cancellation, or invocation failure
occurs after load begins, report that the target may have been touched.

## Security and limits

- Require the dedicated `execute_target_code` permission; neither ordinary mutation nor dnSpy scripting
  permission implies it.
- Disable Tier 2 for remote clients unless a policy explicitly enables it for the caller and target.
- Bound source text, references, compiler diagnostics, payload bytes, result bytes, duration, and queued
  requests. Reject unknown references rather than resolving them from arbitrary host paths.
- Audit caller identity, host/session/process/runtime, source and payload digests, entry point, outcome,
  duration, and whether the target may have been modified. Do not log source or returned secrets by default.
- Document that reference allowlists reduce accidents but do not sandbox code executing inside the target.

## Engine rollout

CorDebug on the deterministic x64 .NET Framework fixture is the first supported engine. Before advertising
Tier 2, a feasibility spike must prove compilation, transfer, load, invocation, hard timeout behavior,
post-failure debugger recovery, and restart cleanup.

Mono/Unity reports `capability_unsupported` until the same behavior is exercised live against UCH. Do not
infer support from compilation or from ordinary Mono function evaluation.

## Exit criteria

- Tier 1 clearly reports the debugger expression boundary and distinguishes compile, runtime, timeout, and
  recovery failures.
- Tier 2 executes a statement-bearing C# payload in the exact selected CorDebug target and returns a
  bounded result with matching source/payload digests and an audit ID.
- Invalid source and disallowed references provably leave the target untouched.
- Ambiguous target selection, stale state, missing permission, and unsupported engines fail before load.
- Timeout after load reports possible target mutation and the documented restart recovery path.
- Authorization, limits, serialization, auditing, and remote-default-off behavior are covered end to end.
