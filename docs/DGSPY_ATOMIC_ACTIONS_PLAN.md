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

1. Acquire the extension's process-scoped action lease and record the current event cursor/state.
2. Add an internal logical owner to a multiplexed physical breakpoint at the target location.
3. Continue and wait for natural execution to the exact target within the bound.
4. Validate process, runtime, thread, frame, method/offset, and CorDebug evaluability.
5. If explicitly allowed, search only declared nearby sequence points/IL offsets and record the slot
   actually used; otherwise fail.
6. Execute the predeclared action, collect a structured result, and perform explicit verification.
7. Release only the internal breakpoint owner, wait for breakpoint-set settling, release temporary handles,
   and apply the requested resume policy.

Results have three orthogonal status fields whose values are fixed before protocol DTOs ship:

- `action_outcome`: `trigger_not_reached`, `reached_not_evaluable`, `nearby_slot_not_found`,
  `action_failed`, `verification_failed`, or `completed`;
- `interruption_reason`: `none`, `timeout`, `cancelled`, `client_disconnected`, `target_exited`,
  `appdomain_unloaded`, `ui_shutdown`, `dispatcher_degraded`, or `external_debugger_action`; and
- `cleanup_outcome`: `not_required`, `completed`, `failed`, or `ambiguous`.

Results also report whether the action may have executed, verification evidence, final debugger state,
audit ID, and the status operation needed after ambiguity. An interruption never gets collapsed into
an action failure, and cleanup failure never overwrites what is known about the action. No outcome
claims that arbitrary execution is guaranteed.

Timeout, cancellation, disconnect, and target exit follow the same cleanup state machine. A timeout
must not silently leave an internal breakpoint owner or unexpected pause behind.

## Specialized managed-payload action

The payload action accepts a bounded bootstrap digest/identity, dependency manifest, initializer type
and method, protocol version, and bounded initialization data. Files are staged in a configured
hash-verified host cache. At an evaluatable managed frame the host loads one bootstrap assembly with
`Assembly.Load(byte[])`. Its initializer has no static dependency on payload types, installs a resolver
restricted to the manifest's exact assembly identities and embedded hash-verified bytes, then loads the
probe and backend. It invokes the fixed initializer contract, verifies returned probe identity and
transport health, and deletes staged files where possible. No dependency is resolved from the target's
working directory, the host cache, or an unverified probing path after bootstrap.

The loaded assembly may remain resident until AppDomain exit even after its hooks are removed. This
action is always audited, requires `runtime_hooks`, and additionally requires `custom_hook_code` when
the payload contains caller-supplied compiled code. It cannot be repurposed into an arbitrary bootstrap
expression. Public `invoke_method` calls are not composed to implement it.

## Auto-continued debugger tracepoint actions

Host-side tracepoints capture declared bounded arguments, locals, fields, results where the engine can
observe them, and exceptions without surfacing an interactive debugger stop to MCP. They are not
zero-stop instrumentation: CorDebug still pauses the target on every hit while the host captures and
continues it. They are therefore unsuitable for hot or timing-sensitive paths where an in-process
HookLab hook is required. Capture never calls getters or `ToString()` by default. Depth, element count,
string length, bytes/event, events/sec, total buffer bytes, sampling, and lifetime are mandatory bounds.

Events use existing cursor/wait conventions and include truncation, dropped counts, target identity,
thread, method/offset, and capture status. A hot or repeatedly failing tracepoint is sampled or
auto-disabled according to policy. Optimized-away or unavailable values are reported truthfully.
Mutation remains an explicit atomic action or HookLab hook, not a hidden tracepoint side effect.

## Auto-continued debugger exception capture

Exception tracing records, where available:

- managed exception type, HRESULT, and bounded inner-exception type chain;
- managed throw method and IL/native offset;
- bounded managed stack and the native-transition boundary marker;
- first-chance/handled/unhandled disposition and continuation state; and
- process/runtime/AppDomain/thread/module identities.

The host continues automatically under declared policy, but the target still incurs a debugger stop
for each captured exception. It does not claim native stack recovery, COM internal state, unmanaged
pointer provenance, or causality beyond the managed debugger boundary.

## Concurrency, ownership, and cleanup

The coordinator and lease live in `dgSpy.Extension`, next to `OnDebuggerAsync` and the breakpoint
services, rather than in the Gateway. Only one atomic action may control a process at a time. Every
conflicting host mutation—including continue, pause, step, detach, terminate, restart, instruction
pointer changes, and breakpoint mutation—consults that lease before reaching the engine; RPC and CLI
requests return `action_in_progress`. Existing version and stop guards remain optimistic stale-state
detection and are not treated as exclusion.

Relevant dnSpy UI commands are disabled while an action owns the selected process. Their disabled-state
text and the dgSpy activity/status surface identify the owning action, its bounded deadline, and the
cancel/status operation instead of presenting unexplained grey controls. Any engine state transition
that bypasses the coordinator is treated as `external_debugger_action`: the action stops issuing work,
performs ownership-scoped cleanup, and never overwrites or automatically resumes the externally
selected state.

Internal breakpoints carry an unforgeable host owner token. An internal action can share an exact
location with a user breakpoint without changing its condition, trace, hit count, labels, or enabled
state. Each owner receives its hits independently. If the user breakpoint requests a visible stop, that
stop wins over the action's resume policy. Cleanup releases only that action's own resources and leaves
every other owner untouched. Engines that cannot provide these semantics advertise the capability as
absent rather than falling back to the public `breakpoints.Add`, which rejects a duplicate location.

The mechanism is one engine breakpoint per logical owner, not several owners refcounted onto a single
shared one. The CLR raises a separate callback per breakpoint object at the same IL offset, so
independent delivery comes free and no shared physical breakpoint has to be tracked or torn down when
its last owner leaves. Do not design against a refcount that does not exist. The stage-0 verdict
evaluated the shared-breakpoint alternative and rejected it; see
`docs/local/evidence/hooklab-breakpoint-multiplex-verdict.md`.

Stage 0 measured this facility as viable on CorDebug and located it: `DnDebugger`'s IL breakpoint list,
one layer below `DbgCodeBreakpointsService`, imposes no uniqueness constraint on location, each
`DnILCodeBreakpoint` owns its own `ICorDebugFunctionBreakpoint`, the CLR raises a separate breakpoint
callback per object at one IL offset, and dnSpy's own `CreateBreakpointForStepper` already uses exactly
this shape. Two measured consequences bind the implementation. A disabled user breakpoint has no
physical breakpoint at all, so an owner is built from the location itself rather than from a bound
breakpoint, and re-enabling one mid-action inserts a physical breakpoint underneath a running action. A
stop caused by an internal-only owner otherwise reaches MCP as an unexplained `pause`: every such stop
carries its owning action instead, because an unattributed pause is exactly the kind of untruthful
result the outcome fields exist to prevent. See
`docs/local/evidence/hooklab-breakpoint-multiplex-verdict.md`.

Capability absence is the shipping answer only for an engine dgSpy does not control, such as Mono. For
CorDebug it is a stage-0 stop condition: dnSpy's public breakpoint service exposes no owner list and
binds through an engine-created bound breakpoint, so the facility must be prototyped against that layer
before stage 1. If it cannot be built there, the atomic breakpoint contract is redesigned rather than
shipped with the capability reported absent wherever a user breakpoint already occupies the location.
See the stage-0 spike in [HOOKLAB_IMPLEMENTATION_PLAN.md](HOOKLAB_IMPLEMENTATION_PLAN.md).

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

The protocol and capability catalog version every new operation and result schema. Older Gateways omit
unknown tools, newer Gateways refuse an incompatible host contract before mutation, and provider or
engine capability absence remains distinct from permission denial.

## Verification and acceptance

- Fixture tests cover every `action_outcome`, every `interruption_reason` and `cleanup_outcome`, exact
  and nearby slots, hot-breakpoint races, unsafe points, action/verification exceptions, and ambiguity
  recovery without implying that every Cartesian combination is valid.
- Breakpoint tests prove user-created breakpoints survive, coincident user/internal owners both receive
  a hit, a user-visible stop wins, and internal breakpoints settle before a terminal result.
- Concurrency tests race same-client and other-client RPC/CLI mutations, UI commands, and direct engine
  transitions against an action and prove host-side exclusion or truthful interruption.
- Trace tests prove no MCP-visible stop, explicitly measure target pause cost, enforce bounded capture
  and overflow accounting, and reject hot-path configurations that require an in-process hook.
- Exception tests cover handled/unhandled and first-chance events, HRESULT and managed stack capture,
  native-transition markers, and automatic continuation.
- HookLab tests prove atomic probe install, identity/pipe verification, failed-bootstrap cleanup, and
  deterministic resume.
- VMConnect acceptance proves the required hook can install and react without an agent pause
  round-trip or fullscreen timing perturbation.
- Mono implementation starts only after CLR fixture and VMConnect acceptance are green, then records
  every CorDebug/Mono capability difference explicitly.
