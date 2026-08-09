# dgSpy atomic actions and non-stopping tracing plan

Timing-sensitive debugger operations must complete inside the selected dgSpy host without an MCP or
agent round trip while the target is paused. This plan adds bounded workflows rather than promising
arbitrary execution. HookLab probe installation is the first consumer; see
[HOOKLAB_IMPLEMENTATION_PLAN.md](HOOKLAB_IMPLEMENTATION_PLAN.md).

## Atomic run-to-action workflow

An atomic request declares the session/process/runtime/AppDomain and guarded method identity, exact
method or IL location, timeout, optional bounded nearby-slot policy, predeclared action, verification,
and deterministic resume policy. Initial actions are bounded capture, assignment from supplied
serialized values, audited method invocation, and managed-payload bootstrap. There is no arbitrary
host script or open-ended expression program.

The host performs one state machine:

1. Create an internally owned temporary breakpoint and record the current event cursor/state.
2. Continue and wait for natural execution to the exact target within the bound.
3. Validate process, runtime, thread, frame, method/offset, and CorDebug evaluability.
4. If explicitly allowed, search only declared nearby sequence points/IL offsets and record the slot
   actually used; otherwise fail.
5. Execute the predeclared action, collect a structured result, and perform explicit verification.
6. Remove only the internal breakpoint, wait for breakpoint-set settling, release temporary handles,
   and apply the requested resume policy.

Terminal outcomes are exactly distinguishable as `trigger_not_reached`, `reached_not_evaluable`,
`nearby_slot_not_found`, `action_failed`, `verification_failed`, or `completed`. Results also report
whether the action may have executed, verification evidence, cleanup outcome, final debugger state,
audit ID, and the status operation needed after ambiguity. No outcome claims that arbitrary execution
is guaranteed.

Timeout, cancellation, disconnect, and target exit follow the same cleanup state machine. A timeout
must not silently leave an internal breakpoint or unexpected pause behind.

## Specialized managed-payload action

The payload action accepts a bounded assembly digest/identity, initializer type and method, protocol
version, and bounded initialization data. Files are staged in a configured hash-verified host cache.
At an evaluatable managed frame the host uses a fixed `Assembly.Load(byte[])` bootstrap, invokes the
fixed initializer contract, verifies returned probe identity and transport health, and deletes staged
files where possible.

The loaded assembly may remain resident until AppDomain exit even after its hooks are removed. This
action is always audited, requires the target-code/runtime-hook permission, and cannot be repurposed
into an arbitrary bootstrap expression. Public `invoke_method` calls are not composed to implement it.

## Non-stopping tracepoint actions

Host-side tracepoints capture declared bounded arguments, locals, fields, results where the engine can
observe them, and exceptions without surfacing an interactive debugger stop to MCP. Capture never
calls getters or `ToString()` by default. Depth, element count, string length, bytes/event, events/sec,
total buffer bytes, sampling, and lifetime are mandatory bounds.

Events use existing cursor/wait conventions and include truncation, dropped counts, target identity,
thread, method/offset, and capture status. A hot or repeatedly failing tracepoint is sampled or
auto-disabled according to policy. Optimized-away or unavailable values are reported truthfully.
Mutation remains an explicit atomic action or HookLab hook, not a hidden tracepoint side effect.

## Non-stopping exception capture

Exception tracing records, where available:

- managed exception type, HRESULT, and bounded inner-exception type chain;
- managed throw method and IL/native offset;
- bounded managed stack and the native-transition boundary marker;
- first-chance/handled/unhandled disposition and continuation state; and
- process/runtime/AppDomain/thread/module identities.

The host continues automatically under declared policy. It does not claim native stack recovery, COM
internal state, unmanaged pointer provenance, or causality beyond the managed debugger boundary.

## Concurrency, ownership, and cleanup

Only one atomic action may control a process at a time; conflicting requests return
`action_in_progress`. Internal breakpoints carry an unforgeable host owner token. Cleanup enumerates
and removes only that action's resources, preserves all user-created and other-client breakpoints, and
waits for debugger breakpoint-set settling before reporting completion.

Disconnect does not abandon the state machine: the host completes or cancels according to the
request's policy, records the terminal result for later retrieval, and resumes deterministically.
Cancellation during invocation is reported as potentially side-effecting. UI shutdown, target exit,
AppDomain unload, or a degraded dispatcher has its own terminal cleanup result.

## MCP and engine capabilities

The host advertises atomic-action, nearby-slot, tracepoint-result, and exception-detail capabilities
per engine. CorDebug x64 CLR v4 is implemented first. Existing `run_to_method` semantics remain
natural arrival and are not broadened. Results explicitly distinguish natural execution, debugger
function evaluation, and injected payload execution. Mono exposes only operations proven by its own
capability and lifecycle tests.

## Verification and acceptance

- Fixture tests cover every terminal outcome, exact and nearby slots, hot-breakpoint races, unsafe
  points, action/verification exceptions, timeouts, disconnect, cancellation, target exit, cleanup,
  and ambiguity recovery.
- Breakpoint tests prove user-created breakpoints survive and internal breakpoints settle before a
  terminal result.
- Trace tests prove no MCP-visible stop, bounded capture, overflow accounting, hot-hook behavior, and
  unavailable-value reporting.
- Exception tests cover handled/unhandled and first-chance events, HRESULT and managed stack capture,
  native-transition markers, and automatic continuation.
- HookLab tests prove atomic probe install, identity/pipe verification, failed-bootstrap cleanup, and
  deterministic resume.
- VMConnect acceptance proves the required hook can install and react without an agent pause
  round-trip or fullscreen timing perturbation.
- Mono implementation starts only after CLR fixture and VMConnect acceptance are green, then records
  every CorDebug/Mono capability difference explicitly.
