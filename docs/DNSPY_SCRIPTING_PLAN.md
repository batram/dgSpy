# dgSpy dnSpy-Host C# Scripting Plan

**Status:** deliberately unscheduled. Implement this only for a concrete workflow that typed debugger operations
cannot serve. This capability is intentionally outside [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

## Objective and trust boundary

Expose Roslyn C# Interactive through a non-UI dnSpy service. Code runs inside dnSpy, not inside the paused
debug target, and can access dnSpy services and the host resources available to the dnSpy user. Enabling it
therefore grants host-level code execution and is not a security sandbox.

C# is the only supported scripting language. Visual Basic parity is out of scope.

## MCP tools

- `execute_dnspy_script`
- `reset_dnspy_script_context`

`execute_dnspy_script` accepts bounded source, execution mode (`stateless` by default or `persistent`),
allowlisted references, a hard timeout, and caller/session correlation metadata. It returns standard output,
return value, compilation diagnostics, duration, timeout/cancellation state, context generation, source
digest, and audit ID.

`reset_dnspy_script_context` destroys the caller's persistent context and advances its generation. A stale
generation is rejected rather than silently executing against different accumulated state.

## Execution model

1. Extract or wrap dnSpy's Roslyn scripting engine behind a service that never opens or drives the C#
   Interactive tool window.
2. Stateless execution uses a fresh context and disposes it after the response. Persistent execution is
   opt-in, scoped to authenticated caller plus host, and serialized so concurrent requests cannot corrupt
   shared state.
3. Capture output per request without permanently replacing process-wide console streams. Bound captured
   output and report truncation.
4. Apply cancellation and a hard deadline. If in-process Roslyn execution cannot be safely stopped, report
   that fact as a capability and do not claim the host was restored to a clean state.
5. Reset all contexts on extension shutdown, authentication identity change, or policy revocation.

## Authorization, limits, and audit

- Require the dedicated `execute_dnspy_scripts` permission. Debugger mutation and target-code permissions
  do not imply it.
- Disable scripting for remote clients by default. Enabling it requires an explicit caller policy.
- Bound source, references, standard output, diagnostics, return serialization, duration, queued requests,
  and persistent contexts per caller.
- Allow only configured references where practical, while documenting that reference allowlists do not
  sandbox in-process code or prevent access through already loaded assemblies.
- Audit caller identity, host, execution mode, context generation, source digest, duration, outcome, and
  cancellation state. Do not log source, output, return values, or inspected secrets by default.

## Exit criteria

- Scripts execute without opening or automating the C# Interactive window.
- Stateless requests cannot observe prior script state; persistent requests observe only their own caller's
  serialized context and can reset it deterministically.
- Output, return values, diagnostics, duration, cancellation state, digest, and audit ID are returned within
  advertised limits.
- Authorization, remote-default-off behavior, reference policy, concurrency, limits, and audit redaction are
  verified end to end.
- Documentation states plainly that enabling this tool grants host-level code execution as the dnSpy user.
