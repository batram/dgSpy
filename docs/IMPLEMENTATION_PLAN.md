# dgSpy MCP Implementation Plan

**Current state:** see [DGSPY_STATUS.md](DGSPY_STATUS.md) for what is implemented and
verified, what is still open, and the handoff notes. This document is the target, not the status.

## Objective

Expose dnSpy's debugger, decompiler, metadata, and search capabilities to AI agents through MCP. Agents must be able to discover programs, attach or launch, control execution, set breakpoints, wait for stops, inspect complete debugger state, evaluate expressions, and navigate decompiled C# and IL.

The system must also support targets running on another Windows host or inside a VM without directly exposing an unauthenticated debugger endpoint.

## Target architecture

```text
AI agent / MCP client
        |
        | Streamable HTTP MCP
        v
dgSpy MCP Gateway
        |
        | authenticated local RPC
        v
dgSpy dnSpy Extension
        |
        | dnSpy public debugger/decompiler APIs
        v
.NET Framework / .NET / Mono / Unity targets
```

The dnSpy extension is the authoritative owner of debugger state. The MCP gateway translates MCP requests, manages host and session identities, applies authorization and output limits, and supports remote transport.

Unlike UltimateGlorpExplorer, dgSpy must route by stable `host_id` and `session_id`, not by an upstream port supplied by the agent. One dnSpy extension endpoint can own multiple debugged processes and runtimes.

## Current scope

The plan describes the full target. Active development is deliberately narrower:

- **Architecture**: x64 only. x86 dnSpy and x86 targets are out of scope for now.
- **Engines**: .NET Framework CorDebug (`CLR v4.0.30319`, covering 4.0 through 4.8) and the Mono/Unity attach path used for UCH.
- **CoreCLR**: not a current target. Keep the capability model engine-agnostic so it can be added, but do not gate milestones on it.
- **Deployment**: one local host, one gateway, one dnSpy instance, one debug session at a time.
- **Agent-facing language**: C# only. Visual Basic parity is out of scope even where dnSpy exposes it.

Anything below marked "full target" stays in the plan for direction; the exit criteria for Phases 0 and 1 apply only to the scope above.

## Design principles

- Use dnSpy's public APIs and MEF extension model; do not automate the WPF UI.
- Run the debugger extension on the same host as dnSpy and the processes being debugged.
- Bind the extension transport exclusively to loopback TCP by default.
- Keep Milestone 1 local-only and unauthenticated while the transport and debugger lifecycle are stabilized. Add authentication before any non-loopback or multi-user deployment.
- Reach remote hosts through SSH, WireGuard, or a mutually authenticated TLS gateway.
- Model debugger actions as typed operations. Optional target-code execution and dnSpy-host scripting
  are separate high-risk tracks, documented in [TARGET_CODE_EXECUTION_PLAN.md](TARGET_CODE_EXECUTION_PLAN.md)
  and [DNSPY_SCRIPTING_PLAN.md](DNSPY_SCRIPTING_PLAN.md).
- Treat all object, frame, value, and location handles as session-scoped and invalid after resume unless explicitly documented otherwise.
- Preserve debugger events in a bounded sequence so agents cannot miss a stop between MCP calls.
- Return structured errors such as `not_paused`, `stale_handle`, `optimized_away`, `unsupported`, and `evaluation_timed_out`.
- Advertise runtime-specific capabilities rather than pretending Mono, CorDebug, .NET Framework, and CoreCLR behave identically.

## Repository layout

Add the following projects without mixing transport code into existing dnSpy contracts:

```text
Extensions/dgSpy.Extension/
    dgSpy.Extension.csproj
    ExtensionEntryPoint.cs
    README.md
    Debugger/
        RpcHost.Scheduling.cs
        SessionStateCalculator.cs
    Decompiler/
    Evaluation/
    Events/
        DebugEventBuffer.cs
        RpcHost.Events.cs
    Handles/
    Identity/
        ProgramIdentity.cs
        RpcHost.Host.cs
    Rpc/
        RpcException.cs
        RpcHost.cs

dgSpy.Protocol/
    dgSpy.Protocol.csproj
    Capabilities.cs
    Requests/
    Responses/
    Events/
    Errors/

dgSpy.Gateway/
    dgSpy.Gateway.csproj
    Mcp/
    Rpc/
    Hosts/
    Sessions/
    Security/

tests/
    dgSpy.Protocol.Tests/
    dgSpy.Extension.Tests/
    dgSpy.Gateway.Tests/
    dgSpy.IntegrationTests/
    TestTargets/
```

`dgSpy.Protocol` must contain DTOs and contract constants only, and must not reference WPF or dnSpy
implementation assemblies. `Capabilities.cs` is the one non-DTO file: the operation/bound/engine table
is shared contract, and putting it anywhere else lets the extension's real bounds and the gateway's
deadlines drift apart, which is exactly the bug it exists to prevent. This keeps the gateway independently testable and allows transport replacement without changing debugger behavior.

Milestone 1 keeps MEF composition/lifetime in `ExtensionEntryPoint.cs`; the loopback host and operation
dispatch in `Rpc/RpcHost.cs`; dispatcher scheduling in `Debugger/`; event cursor behavior in `Events/`;
and stable identities in `Identity/`. New tool families belong in focused `RpcHost.<Family>.cs` partials
under their owning directory. Pure rule files are linked into `dgSpy.Extension.Tests`, so those
invariants run on net7 without loading WPF or dnSpy.

## State model

### Hosts

A host represents one reachable dnSpy extension endpoint.

Required fields:

- `host_id`
- display name and machine name
- dnSpy and dgSpy versions
- operating system and architecture
- connection state
- supported debugger engines
- authentication identity

### Sessions

A session represents one logical debugger attachment or launch. It can contain multiple processes, runtimes, app domains, modules, and threads.

Required fields:

- `session_id`
- owning `host_id`
- state: `attaching`, `running`, `paused`, `mixed`, `detaching`, `exited`, or `faulted`. `attach` is asynchronous, so a session must report `attaching` until the engine is up and `faulted` when it fails; it must never report `exited` for a session that has not started yet.
- selected process, thread, and frame where applicable
- monotonically increasing `state_version`
- most recent event sequence
- runtime capability flags

### Handles

Use opaque handles for modules, threads, frames, values, locations, and breakpoints. Never serialize dnSpy objects or memory addresses as durable identities.

Each handle records:

- owning session
- object kind
- generation
- stable identity where dnSpy provides one
- disposal callback when required

Resuming execution increments the value/frame generation. Accessing an invalidated handle returns `stale_handle` with guidance to request a fresh snapshot.

## Phase 0: Build baseline and extension spike — complete

Completed and verified on 2026-08-03. CorDebug is acquired through `list_programs` + `attach`;
endpoint-launched UCH is acquired through `attach_endpoint` because that launch mode emits no discovery
beacon. The automated CorDebug smoke target is explicitly x64 and dnSpy's reported architecture is
asserted end to end.

### Work

1. Document the toolchain baseline in `docs/DGSPY_BASELINE.md`: .NET SDK, dnSpy target framework, architecture, and the two supported debug engines.
2. Build the existing dnSpy solution unchanged (`build.ps1 netframework`).
3. Provide a single repeatable build-and-deploy step for the three dgSpy projects that copies the extension into dnSpy's extension directory. The dgSpy projects stay out of `dnSpy.sln` so the fork's own build is unaffected.
4. Add a minimal MEF extension which loads at startup and logs its version.
5. Inject and read `DbgManager`, `AttachableProcessesService`, `DbgCodeBreakpointsService`, `DbgCallStackService`, `DbgLanguageService`, and `DbgDotNetCodeLocationFactory`. Document services deferred to later phases rather than importing them speculatively.
6. Prove safe calls from a background request thread through `DbgManager.Dispatcher`, and write down the thread-affinity rules next to that abstraction.
7. Add an x64 .NET Framework test target with a known method token and a long-running loop.
8. Verify the Unity/Mono attach path manually against UCH. Record whether the launch mode is discoverable
   through `list_programs`; when UCH is launched with an explicit soft-debugger endpoint and emits no
   discovery beacon, verify it through `attach_endpoint` instead.

### Exit criteria

- A clean checkout can build dnSpy, build dgSpy, and deploy the extension with two documented commands.
- The extension loads in x64 net48 dnSpy and writes its version to the Output window.
- Required services resolve through MEF without a debug session being active.
- Debugger state can be queried from an RPC thread without running request serialization, socket I/O, or expression evaluation on the debugger dispatcher thread.
- Thread-affinity rules are documented next to the RPC dispatcher abstraction.
- Both engines in scope are reachable through their supported acquisition path: the .NET Framework test
  target through `list_programs` + `attach`, and endpoint-launched UCH through `attach_endpoint`.

## Phase 1: Local RPC and discovery — **complete**

Closed out 2026-08-03; see [DGSPY_STATUS.md](DGSPY_STATUS.md) for the verification record.
One exit criterion was amended rather than met: dnSpy exposes no way to abort a queued dispatcher
callback or a started evaluation, so a deadline reports `deadline_exceeded` without unwinding the
debugger. That limitation is now advertised through `get_capabilities` instead of being implied away.

### Work

1. Implement a versioned local RPC protocol between the gateway and extension.
2. Use TCP bound exclusively to `127.0.0.1` (and optionally `::1` once dual-stack behavior is tested). Never bind the extension RPC listener to wildcard, LAN, or VM-facing interfaces.
3. Add handshake, version negotiation, request IDs, cancellation, deadlines, and structured errors. Cancellation must reach the debugger operation itself, not only the socket: an expired deadline has to abandon or abort the queued dispatcher work and report `deadline_exceeded` once. **Partially delivered.** The deadline abandons the wait and reports `deadline_exceeded` exactly once, but dnSpy offers no cancellation for a queued dispatcher callback or a started evaluation, so that work runs to completion. Advertised as `limits.cancels_in_flight_work: false` rather than hidden; revisit if func-eval makes evaluations long enough for it to matter.
4. Implement process discovery using `AttachableProcessesService` rather than raw `Process.GetProcesses()` as the authoritative list. Allow the caller to select attach providers, because Unity discovery performs a multi-second network scan on every unfiltered enumeration.
5. Report duplicate entries when a process exposes multiple supported runtimes.
6. Build `program_id` from typed identity fields, never from `RuntimeId.ToString()`. `RuntimeId` has no string form; the durable identity is PID plus provider plus the engine's own discriminator (CLR version for CorDebug, address and port for Mono/Unity). Surface `RuntimeGuid` as well as `RuntimeKindGuid`, since both supported engines share the same kind GUID and are only distinguishable by runtime GUID.
7. Add health, version, and capability operations. Capabilities are the contract for engine differences the caller cannot guess — arbitrary IL offsets versus sequence points, which engines are discoverable — and for the extension's own per-operation time bounds. The gateway derives its deadlines from those bounds rather than guessing them, so the two cannot drift.
8. Reject cross-origin browser traffic at the gateway before any tool runs: require a loopback or absent `Origin`, and require a locally generated shared secret. Without this, any web page the user visits can drive the debugger through the loopback MCP endpoint, which no amount of loopback-only binding prevents.

### Initial operations

- `get_host_info`
- `get_capabilities`
- `list_programs`
- `ping` (RPC handshake; not exposed as an MCP tool)

Milestone 1 exposes a single implicit host and a single session. `host_id` routing, multiple concurrent sessions, and session ownership arrive with Phase 9; until then the protocol carries `session_id` only, and a second `attach` while a session is live is an error rather than a silent replacement.

### Exit criteria

- ✅ Gateway reconnects after dnSpy restarts. Verified by killing and restarting dnSpy mid-run: calls fail while it is down and succeed again without restarting the gateway.
- ✅ Process discovery returns PID, executable, title, architecture, runtime identity, runtime GUID, and attach provider. `attach_providers` reports the dnSpy provider names that can produce the entry, which are exactly the values `provider_names` accepts.
- ✅ Two runtimes reachable at the same PID produce two distinct, stable `program_id` values. Unit-tested against `ProgramIdentity`; the two in-scope engines cannot co-exist in one process, so no live fixture produces it.
- ✅ The extension RPC port is unreachable through non-loopback interfaces. Verified by connecting to this machine's own LAN address on the RPC port.
- ✅ A cross-origin `fetch` from a web page cannot invoke any tool.
- ⚠️ An expired deadline returns a structured error once, but does not cancel the in-flight debugger operation — see work item 3. Advertised through `get_capabilities`.
- ✅ Protocol compatibility failures are explicit and actionable (`incompatible_protocol`, naming both versions).

## Phase 2: Attach, launch, and lifecycle control — **complete**

Closed out 2026-08-03. CorDebug launch, restart, explicit termination, safe detach, and unexpected
nonzero target exit are covered by the automated live smoke. `get_events` is exposed early as the
smallest way to make Phase 2 terminal events observable; Phase 3 still owns the complete event model.

### Work

1. Attach using the exact options returned by the chosen `AttachableProcess`.
2. Add launch support through dnSpy debugger start options, not `Process.Start` followed by a race-prone attach. **Implemented for CorDebug and Unity launch options; CorDebug is automated.**
3. **`attach_endpoint` (implemented)** — attach directly to a Mono soft-debugger endpoint by address and port. Required for the UCH workflow: a target launched with `--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n` emits no multicast beacon, so no attach provider will ever enumerate it and `list_programs` cannot reach it. This is the only route to the Mono/Unity engine. `UnityAttachToProgramOptions` is internal to dnSpy's Mono engine; the public equivalent is `UnityConnectStartDebuggingOptions` (`Address`, `Port`, `ProcessIsSuspended`, `ConnectionTimeout`) passed to `DbgManager.Start`, which `DbgEngineProviderImpl` maps onto the same engine and `DbgEngineImpl.StartCore` treats as an attach.
4. Track debugger processes and runtimes from `DbgManager` events.
   - `attach` must not report ready until the engine has enumerated **threads**, not merely a process. A pause issued inside that window produces a stop with no current thread and an empty call stack, and the session does not recover until it runs again. Waiting for the first thread costs a few hundred milliseconds and makes pause-then-inspect deterministic.
   - The call stack follows `DbgManager.CurrentThread`, which only dnSpy's UI or a thread-carrying stop sets. Headless callers must select a thread themselves when none is current.
5. Implement pause, continue, detach, stop, and restart where supported. **Implemented.** `terminate`
   always calls dnSpy's explicit `TerminateAll`; `restart` is accepted only for a dgSpy-launched session.
6. Define selection rules for sessions containing multiple processes or runtimes. **Defined for the current
   single-session scope:** lifecycle operations apply to every process owned by that dnSpy session; returned
   state always includes the complete current PID set.
7. Preserve process-exit and attach-failure details in the event log. **Implemented.** Terminal events carry
   PID, exit code, reason, and a terminal flag. Attach failures have two shapes and stay distinguishable:
   options `DbgManager.Start` rejects outright are a caller error and never become a session, whereas an
   engine that starts and then fails to connect reports through `DbgManager.MessageUserMessage`, which is
   what makes a `faulted` session carry a real reason instead of a timeout guess.

### MCP tools

- `list_hosts`
- `list_programs`
- `attach`
- `attach_endpoint`
- `launch`
- `list_sessions`
- `get_session_state`
- `pause`
- `continue`
- `detach`
- `terminate`
- `restart`
- `get_events` (Phase 2 terminal lifecycle events; expanded in Phase 3)

### Exit criteria

- ✅ An agent can list and attach to a .NET test process by PID and runtime.
- ✅ State transitions are observable without reading dnSpy UI state.
- ✅ Detach leaves the target alive; terminate has separately tested semantics.
- **Observed 2026-08-03**: with a CorDebug session attached to a live process, closing dnSpy and confirming its shutdown prompt *terminated the attached target*. `detach` is implemented and verified as the safe exit. Window shutdown is a separate host-lifecycle path, not an agent lifecycle operation or Phase 2 exit gate.
- ✅ Unexpected target exit produces a terminal event and releases all session-scoped handles. Verified
  with exit code 23. Current frame/value handles are snapshots rather than retained dnSpy objects; the
  retained session-scoped breakpoint-offset metadata is cleared on terminal exit and restart.

## Phase 3: Event stream and breakpoint waiting — **complete**

Completed 2026-08-03. The CorDebug smoke verifies normalized breakpoint details, waits issued both
before and after a hit, concurrent non-destructive waiters, bounded timeout, and a second stop with new
event and state versions. Buffer tests verify cancellation recovery and truncation cursor reporting.

### Work

1. ✅ Subscribe to debugger pause, breakpoint, step, exception, process, runtime, module, and thread events.
2. ✅ Normalize events into a per-session bounded sequence with monotonically increasing IDs.
3. ✅ Implement cancellable long polling with `after_event_id` and a bounded timeout.
4. ✅ Return immediately if an unseen event already exists.
5. ✅ Preserve the stop reason, process, thread, breakpoint, exception, and location.
6. ✅ Ensure concurrent waiters do not consume events destructively.

### MCP tools

- `get_events`
- `wait_for_event`
- `wait_for_stop`
- `get_stop_reason`

### Exit criteria

- ✅ An agent can set up a wait before or after a breakpoint hit without losing the event.
- ✅ Cancellation and timeout do not leak tasks or event subscriptions.
- ✅ Resume followed by another stop produces a new state version and event ID.
- ✅ Event-buffer truncation is reported with the new oldest available cursor.

## Milestone 1: complete

Every item in "First implementation milestone" at the end of this document is delivered and verified;
Phases 0 through 3 are closed. The phase numbering below understates that, because seven tools the
original Phase 4 and Phase 5 lists own already ship: `set_il_breakpoint`, `list_breakpoints`,
`remove_breakpoint`, `clear_breakpoints`, `list_threads`, `get_callstack`, and `get_frame`. They landed
early because Milestone 1's vertical slice needed them, and the remaining phases are scoped around that
rather than pretending it did not happen.

## Phase 4: Breakpoint conditions and stepping — **complete**

Closed out 2026-08-03. Conditions, hit counts, tracepoints, exception breakpoints and all three step
kinds are covered by the live CorDebug smoke.

Breakpoint *identity* is deliberately not in this phase. Setting a breakpoint by module/type/method
name, and mapping it to a decompiled C# line, are symbol-resolution problems, and Phase 6 owns the
metadata and decompiler layer that makes them possible. Leaving them here left Phase 4 half-blocked on
a layer that does not exist yet; they now live in Phase 6, item 8.

Everything below works on the token+offset identity that already ships.

### Work

1. ✅ Support metadata-token and exact IL-offset breakpoints as the dependable base representation. "Exact IL offset" is not portable: Mono accepts only sequence points and rejects everything else with `NO_SEQ_POINT_AT_IL_OFFSET`, while CorDebug accepts any offset. Advertised as a capability, and a requested offset is snapped to method entry rather than returning a breakpoint that silently never binds.
2. ✅ Report binding state and per-runtime bound-breakpoint errors. This is not cosmetic: without it `set_il_breakpoint` returns an id for a breakpoint the engine refused, and the caller's only symptom is a `wait_for_stop` that never fires. Observed against UCH on 2026-08-03.
3. ✅ Scope breakpoints to the session, or expose `clear_breakpoints`. dnSpy's `DbgCodeBreakpointsService` is global, so breakpoints survive `detach` and rebind on the next attach — a fresh session can stop on a breakpoint its caller never set. Delivered as `clear_breakpoints` plus `remove_breakpoint`; the breakpoints stay global and the documentation says so.
4. ✅ Implement enabled state, conditions, hit counts, and trace messages. All four are one dnSpy
   settings object written as a unit. An empty string clears a condition or trace message; an omitted
   field is left alone, which is the only way to remove one without recreating the breakpoint. A
   tracepoint with `trace_continue` produces **no stop at all**, so `update_breakpoint` returns a warning
   rather than leaving the caller to discover it from a `wait_for_stop` that never returns.
5. ✅ Implement exception breakpoint settings. Listing defaults to first-chance entries: dnSpy stops on
   *second* chance for essentially every .NET exception it ships, so "everything that stops" is ~2500
   stock entries identical on every machine. The listing is bounded and reports `total` and `truncated`.
6. ✅ Implement step into, over, and out using the selected thread's `DbgStepper`. Step completion
   normalizes into the event stream as `step_completed` and as a `stopped` event with
   `stop_reason: "step"`, so the caller waits for it exactly as for a breakpoint — including the same
   cursor discipline, since a short step lands before a follow-up state read returns.
7. ✅ Close steppers on every terminal path. The normal path is dnSpy's own `autoClose`; the tracked
   reference covers detach, terminate, restart, and the target exiting mid-step, where `StepComplete` is
   never raised. Code locations were already closed on the duplicate-breakpoint path in Phase 3.

### MCP tools

- `set_il_breakpoint` (implemented)
- `list_breakpoints` (implemented)
- `remove_breakpoint` (implemented)
- `clear_breakpoints` (implemented)
- `update_breakpoint` (implemented)
- `set_exception_breakpoint` (implemented)
- `list_exception_breakpoints` (implemented; not in the original plan — a caller that can set an
  exception breakpoint needs to see what is already set, and dnSpy's stock defaults make that
  non-obvious)
- `step_into` / `step_over` / `step_out` (implemented)

### Exit criteria

- ⏭️ A breakpoint set before its module loads reporting as bound is deferred to Phase 6, which owns
  module identity. `set_il_breakpoint` takes a module path, so a not-yet-loaded module can be named but
  the pending-then-bound transition is only meaningful once modules can be resolved by name.
- ✅ Conditional breakpoints and hit counts are covered by the live smoke, including that an empty
  string clears rather than sets, and that an unknown `condition_kind` is rejected with the valid set.
- ✅ Step completion is returned through the same event mechanism as breakpoint stops: same
  `wait_for_stop`, same cursor, `stop_reason: "step"`.
- ✅ A stepper is closed on every terminal path; a step is refused outright on a running target, on an
  unknown thread, and when no thread is current, rather than guessing which thread to resume.

## Phase 5: Expression evaluation and object inspection — **complete**

Closed out 2026-08-03 on CorDebug. Evaluation, one-level paged member expansion, assignment, exception
reads, watches and module listing are covered by the live smoke.

Enumeration and frame capture already ship. What is missing is the ability to look at anything that is
not a primitive local, which is a function-evaluation problem, so this phase is scoped to that.

Three tools from the original list are deliberately gone:

- **`select_frame` is dropped.** It contradicts a decision already shipped and verified: `get_callstack`
  and `get_frame` are stateless on caller-supplied `thread_id` + `frame_index`, precisely because
  `DbgManager.CurrentThread` is set only by dnSpy's UI or a thread-carrying stop. Reintroducing
  server-side frame selection recreates the problem headless callers hit in Phase 2. `evaluate` and
  `set_value` take the same two parameters instead.
- **`get_arguments`, `get_locals`, and `get_this` fold into `get_frame`**, which already returns
  primitive locals, as an `include` parameter. They are one round trip, not four.
- **`list_processes` and `list_runtimes` fold into `get_session_state`**, which already returns the
  complete `process_ids` set. Under one session they carry no information it does not.

### Work

1. ✅ Enumerate threads and stack frames; capture frames only while paused and close dnSpy frame objects correctly.
2. ✅ Expose arguments, `this`, exceptions, and object members beyond primitive scalars. Arguments are
   *not* a separate tool: dnSpy's locals provider does not separate them from locals, so pretending
   otherwise would be a lie in the tool surface. Return values are deferred — they need `ReturnValuesProvider`
   and a step-completion stop to be meaningful, so they belong with whatever exercises stepping on Mono.
3. ✅ Add paging and collection limits (`offset`/`count`, `total`, `truncated`, hard cap 200). Depth and
   cycle detection are handled by *not recursing*: expansion is one level, and a member carries the
   expression that reaches it, so depth is the caller's to spend and cancel. Evaluation timeouts come
   from the operation bounds the gateway already derives from `get_capabilities`.
4. ✅ Preserve raw type information separately from formatted display text. `value` is the scalar,
   `display` is dnSpy's formatting, and `has_raw_value` separates a value of `null` from a value the
   runtime cannot supply.
5. ✅ Support writing locals, parameters, and fields. `set_value` reports `compiler_error` so a caller
   can tell "the expression did not compile and nothing ran" from "the target may already be touched".
6. ✅ Implement watch expressions as stored expressions re-evaluated against a caller-selected frame.
   A failing watch reports its own error rather than failing the call.
7. ⏭️ Deferred with reason: func-eval is now *possible* (`allow_func_eval`) but off by default, and
   nothing in the CorDebug fixture makes an evaluation long enough to provoke `stale_handle` or to make
   `cancels_in_flight_work: false` bite. Both remain recorded in `docs/DGSPY_STATUS.md`.

### MCP tools

- `list_threads` (implemented)
- `get_callstack` (implemented with caller-selected `thread_id`)
- `get_frame` (implemented; `include: ["locals","this"]` returns objects too, in `values`)
- `list_modules` (implemented; `can_set_breakpoint` distinguishes path-less modules)
- `get_exception` (implemented)
- `get_members` (implemented, paged, one level)
- `evaluate` (implemented; func-eval opt-in)
- `set_value` (implemented)
- `add_watch` / `list_watches` / `remove_watch` (implemented)

### Exit criteria

- ✅ A breakpoint hit can be followed by call-stack and local-variable inspection through MCP only.
- ✅ Optimized-away and unavailable values are distinguished from `null`, by `has_raw_value` plus the
  runtime's own error text. Unit-tested on the wire and exercised live against a static method's `this`.
- ✅ Object expansion cannot recurse indefinitely or return unbounded data: one level, capped at 200,
  with `total` and `truncated`.
- ⏭️ `stale_handle` is still reasoned about rather than provoked — see work item 7. The check exists and
  runs on every evaluation; what is missing is an evaluation slow enough to lose the race deliberately.

## Phase 6: Decompiled C#, IL, metadata, and search — **complete**

Closed out 2026-08-04. The complete surface is covered by the live CorDebug smoke, including bounded
decompiled-text search, IL reference and type-implementation analysis, module/token metadata, and paged
raw module images with a whole-image SHA-256. `get_method_body` is subsumed by `get_il`, which returns
the body with offsets, operands and sequence points.

Reverified against live UCH on 2026-08-04. Analysis operations must bound work as well as output:
`search_text` caps scanned methods and accepts a type scope, while `find_implementations` accepts a
module scope. The live pass exercised all five tools, a real Mono breakpoint stop, lifecycle
responsiveness after every operation, clean detach, and game survival.

**Consider taking this before the rest of Phase 5.** Today a breakpoint requires the caller to already
know a metadata token, which for the UCH workflow is the single largest gap between "the debugger works"
and "an agent can use it unaided". That is a symbol-search problem, not an evaluation problem, and item 8
below is what closes it. Evaluation is more capability; this is more reach.

### Work

1. Resolve loaded modules to dnSpy documents and in-memory module images. This also covers the deferred
   limitation that a frame can name a module with no file (`data-000001BE153D3040` on a UCH stack), which
   `set_il_breakpoint` cannot address because it takes a module path.
2. Expose assembly, module, namespace, type, member, and metadata-token navigation.
3. Decompile assemblies, types, and methods using an explicitly selected language.
4. Provide IL instructions with offsets, operands, exception regions, locals, and sequence mappings.
5. Search loaded documents and optionally user-opened documents by type/member/text pattern.
6. Add reference analysis through dnSpy analyzer services where reusable; otherwise implement a headless service over the same metadata model.
7. Return paged results and stable symbol identities.
8. **Moved here from Phase 4.** Resolve source-style breakpoint locations by module/type/method name, and
   map them to decompiled C# lines where sequence-point or decompiler mappings permit it. Both need the
   symbol layer this phase builds; neither is possible against token identity alone. Invalid or ambiguous
   method names must return candidates instead of silently choosing one.

### MCP tools

- `set_breakpoint` (implemented — resolves the name, then takes the identical `set_il_breakpoint` path,
  so binding state, Mono snapping and `cursor_event_id` cannot diverge between the two)
- `list_documents` (implemented)
- `list_types` (implemented, paged and filtered)
- `list_members` (implemented, paged and filtered)
- `search_symbols` (implemented, bounded)
- `get_il` (implemented, with `is_sequence_point` per instruction)
- `get_csharp` (implemented, method or whole type)
- `search_text` (implemented; searches decompiled C# and returns containing method identities)
- `find_references` (implemented; scans loaded method IL for the target module+token identity)
- `find_implementations` (implemented; finds loaded direct subclasses and interface implementers)
- `get_metadata` (implemented; module/table counts plus optional token resolution)
- `get_raw_module` (implemented; bounded base64 chunks, total size and whole-image SHA-256; file-less
  runtime metadata is serialized to a module image)
- `get_method_body` — subsumed by `get_il`

Original list, retained for reference:

- `list_documents`
- `list_types`
- `list_members`
- `get_csharp`
- `get_il`
- `get_metadata`
- `get_method_body`
- `search_symbols`
- `search_text`
- `find_references`
- `find_implementations`
- `get_raw_module`

### Exit criteria

- ✅ The agent can navigate from a paused frame to its method's C# and IL: the frame reports module and
  token, and both `get_il` and `get_csharp` take exactly those.
- ✅ Dynamic and in-memory modules resolve *metadata* through `DbgMetadataService`, so `get_csharp` and
  `get_il` reach them. `set_breakpoint` still cannot: dnSpy's code-location factory addresses modules by
  path. That is an explicit `module_has_no_path` error plus a `can_set_breakpoint: false` flag on
  `list_modules`, rather than a breakpoint that never binds. Verified on CorDebug: the fixture carries
  an assembly loaded from bytes and a Reflection.Emit dynamic assembly, each reached only through a
  callback, so a breakpoint in the callee leaves the file-less module's frame on the stack. The live run
  found that an in-memory module reports a *bare assembly name* as its filename rather than nothing, and
  that the refusal keyed off emptiness while `can_set_breakpoint` did not — so the guard accepted a
  module the flag called unusable. Both now share one predicate. `set_il_breakpoint` still has no guard
  (it takes a path and gets a name); it does not claim to be bound, and that is asserted rather than
  fixed. **Verified on Mono too**, against live UCH, which carries 16 file-less modules — MonoMod,
  Harmony's `HarmonyDTFAssembly*`, an in-memory `UnityEngine.CoreModule`, and `eval-*` func-eval
  assemblies that publish no metadata and refuse with `metadata_unavailable`. Only one thing is still
  observed-once rather than reproducible: a *frame* whose module has no file. On Mono, Harmony patch
  frames report the original file-backed module, so that route does not produce one.
- ✅ Search results include enough identity to set breakpoints without parsing display text: module plus
  metadata token, verified by feeding a `list_members` token straight into the breakpoint tools.
- ✅ A breakpoint can be set from a type and method name alone, with no token supplied by the caller.
  Ambiguous types and overloaded methods return the candidates rather than picking one.
- ✅ Large assemblies and result sets remain bounded: every listing is paged or capped and reports
  `total` and `truncated`, and every operation carries a bound the gateway derives its deadline from.

## Phase 7: Advanced evaluation and low-level debugging — **complete**

Completed 2026-08-04 and verified end to end on CorDebug. The public dnSpy contract does not expose
registers, so `get_registers` is intentionally present but returns `capability_unsupported`; this is the
structured capability outcome the phase requires, not an empty successful response. Mono/Unity advertises
that native disassembly is unavailable. Its invocation, memory and set-IP paths compile and are capability-
described, but have not yet been exercised against UCH.

### Work

1. ✅ Support target method invocation and object construction behind explicit side-effect controls.
   `invoke_method` and `create_object` are `evaluate` with side effects permitted, and stay separate
   tools anyway: that makes the audit and permission boundary a property of the tool surface rather than
   a flag someone can flip.
2. ✅ Add bounded memory reads and writes where the active engine supports them.
3. ✅ Add managed IL and native-code blocks when exposed by the runtime; registers report a structured
   unsupported capability because dnSpy exposes no public register service.
4. ✅ Add set-instruction-pointer after validating the selected frame, current method, and engine location.
5. ✅ Surface runtime feature flags for invocation, construction, memory, native disassembly, registers,
   set-IP, and function-evaluation abort behavior.
6. ✅ Pass the caller's bounded timeout into dnSpy's evaluation context. CorDebug aborts a timed-out
   func-eval and reports when recovery failed and func-eval was disabled; the RPC deadline remains an
   outer bound and still cannot cancel arbitrary queued dispatcher work.

### MCP tools

- `invoke_method`
- `create_object`
- `read_memory`
- `write_memory`
- `get_disassembly`
- `get_registers`
- `set_instruction_pointer`

### Exit criteria

- ✅ Read-only inspection remains separate from mutation in the operation capability table.
- ✅ Every Phase 7 side-effecting tool is labeled in MCP discovery and writes an audit record; invocation,
  construction and set-IP return the audit id.
- ✅ Unsupported features return `capability_unsupported` or `capability_unavailable`.
- ✅ Engine-level func-eval timeouts abort where supported and surface dnSpy's recovery failure rather than
  silently leaving the session unusable.

## Phase 8: Debugger completeness

This phase collects debugger-native follow-ons to the completed lifecycle, event, breakpoint, evaluation,
analysis, and low-level phases. It does not reopen Phases 0–7 or weaken their verified contracts. Multiple
active targets means multiple processes and runtimes owned by dnSpy's default debugger manager, not
isolated debugger managers inside one dnSpy process.

### Work

1. Support explicit `process_id` and `runtime_id` selection on every operation whose target can be
   ambiguous. Preserve the current implicit target only when exactly one candidate is valid; otherwise
   return `ambiguous_target` without acting. Report aggregate session state as `mixed` when targets differ.
2. Expose dnSpy object IDs as runtime-scoped persistent references. Create, list, evaluate, and release
   them; advertise engine support and release them on request, runtime exit, detach, or session teardown.
3. Add `get_autos` through the active C# language's Autos provider. Add a separate bounded, cursor-based
   debugger/output stream carrying process and runtime identity, sequence IDs, timestamps, truncation,
   and message category; stop events remain in their existing event stream.
4. Add module-load and module-unload breakpoints through `DbgModuleBreakpointsService`, including dnSpy's
   module-name wildcard, dynamic, in-memory, load order, process-name, and app-domain filters.
5. Define one versioned canonical JSON format for code, trace, module, and exception breakpoints. Import
   validates and supports dry-run, deduplicates by stable breakpoint identity, defaults to `merge`, and
   requires explicit `replace` before removing existing breakpoints.
6. Expose exception categories, definitions, flags, and conditions through `DbgExceptionSettingsService`.
   Support list, add, modify, remove, and restore-default operations; thrown, user-unhandled, and unhandled
   modes are capability-gated rather than normalized across engines.
7. Export supported evaluated values as bounded byte chunks with total length and whole-value SHA-256.
   Optionally write on the debug host behind a separate permission, configured export roots, canonical
   path validation, and no-overwrite by default; return the final path and hash.
8. Expand analysis with typed edges for callers, callees, field reads and writes, construction, overrides,
   interface implementation, attributes, and event add/remove access. Preserve module/type scopes, paging,
   stable symbol identities, scan bounds, and truncation reporting from Phase 6.

### MCP tools

- `create_object_id` / `list_object_ids` / `evaluate_object_id` / `release_object_id`
- `get_autos`
- `get_output` / `wait_for_output`
- `set_module_breakpoint` / `list_module_breakpoints` / `update_module_breakpoint` / `remove_module_breakpoint`
- `export_breakpoints` / `import_breakpoints`
- `list_exception_categories` / `list_exception_policies` / `set_exception_policy` / `remove_exception_policy` / `restore_exception_defaults`
- `get_value_export` / `write_value_export`
- `analyze_symbol`

### Exit criteria

- Every ambiguous multi-target request fails without changing debugger or target state, and every result
  identifies the process and runtime that produced it.
- Object IDs survive resume when the active engine supports them and are deterministically disposed at all
  documented lifetime boundaries.
- Autos and output are accessible without UI automation; output cursors report gaps after truncation.
- Module breakpoints cover load and unload, and breakpoint JSON round-trips without semantic loss.
- Breakpoint import dry-run performs no mutation; `merge` never deletes, and only explicit `replace` does.
- Exception modes and object-ID support are advertised per engine and return structured unsupported errors.
- Value transfer and host export are bounded and hashed; host export rejects traversal, disallowed roots,
  and overwrite unless explicitly authorized.
- Analyzer results identify the relationship kind and both endpoint symbols without parsing display text.

## Phase 9: Remote hosts and secure transport

### Recommended deployment

Run one gateway and dnSpy extension on each debug host. Connect from the agent host through an SSH or WireGuard tunnel. Bind MCP and extension RPC to loopback unless a hardened remote listener is explicitly configured.

### Work

1. Add gateway host registration and stable host identities.
2. Support MCP Streamable HTTP on loopback.
3. Document SSH local-forward and reverse-forward configurations for VM networking constraints.
4. For direct network exposure, require TLS, mutual client authentication, request-size limits, rate limits, and explicit capability policies.
5. Add per-client permissions:
   - discover
   - inspect a selected target
   - control execution of a selected target
   - mutate target state
   - terminate processes
   - export values on the debug host
   - execute target code (see [TARGET_CODE_EXECUTION_PLAN.md](TARGET_CODE_EXECUTION_PLAN.md))
   - edit assembly artifacts and replace live method bodies (see [ASSEMBLY_EDITING_PLAN.md](ASSEMBLY_EDITING_PLAN.md))
   - execute dnSpy-host scripts (see [DNSPY_SCRIPTING_PLAN.md](DNSPY_SCRIPTING_PLAN.md))
6. Add structured audit records with secrets and inspected values redacted by policy.
7. Add connection-loss behavior that does not automatically resume, detach, or terminate a paused target unless configured.

### Exit criteria

- An MCP client on another host can securely discover and debug a target inside a VM.
- The debugger is not reachable from the VM network without the selected tunnel or authenticated listener.
- Reconnection preserves session state and event cursors where the dnSpy process survived.
- Authorization tests prove that inspection-only clients cannot resume, mutate, terminate, export to the
  host, execute target code, edit artifacts, replace live methods, or execute dnSpy-host scripts.

## MCP behavior requirements

### Tool responses

All tools should return structured content containing:

- `host_id` and `session_id` where applicable
- `state_version`
- operation-specific result
- relevant capability flags
- warnings
- stable continuation token for paged data

Avoid returning essential identities only in prose.

### Long-running operations

- Every operation accepts cancellation and a deadline.
- `wait_for_stop` should default to a bounded wait and accept `after_event_id`.
- Attach, launch, evaluation, and decompilation return progress or explicit timeout errors.
- Gateway and extension timeouts must be coordinated so the inner operation expires first.

### Concurrency

- Serialize state-mutating debugger operations per session.
- Permit concurrent read-only requests only where dnSpy object lifetime and dispatcher rules make this safe.
- Include the expected `state_version` on mutations to reject decisions based on stale paused state.
- Define ownership or leases for competing agents before enabling multi-client execution control.

## Security requirements

The service provides capabilities equivalent to a local debugger and, optionally, arbitrary code execution. Security is a core feature, not a deployment add-on. Milestone 1 temporarily permits an unauthenticated loopback-only extension RPC endpoint for local development; authentication is mandatory before remote, shared-host, or production use.

Loopback binding is not by itself a trust boundary for the HTTP gateway. A browser can reach `127.0.0.1` and can issue a simple cross-origin POST without a preflight, so `Origin` validation plus a local shared secret belong in Phase 1, not Phase 9. The raw RPC socket is different: browsers cannot speak it, so it stays unauthenticated only for Milestone 1 and only on loopback.

- Loopback-only TCP binding by default.
- `Origin` validation and a local shared secret on the HTTP gateway from Milestone 1 onward.
- No anonymous remote listener.
- Add authenticated extension RPC connections and protect their credentials as part of Phase 9 hardening.
- Mutual authentication for network transports.
- Separate read, per-target control, mutation, termination, host-export, target-code, artifact-edit,
  live-patch, and dnSpy-host scripting permissions.
- Request, response, expression, value-export, script, artifact, and decompilation size limits.
- No secrets in ordinary logs.
- Audit side-effecting operations.
- Explicit opt-in for memory writes, function calls, target termination, host exports, target-code
  execution, artifact editing, live method replacement, and dnSpy-host scripting.
- Never claim debugger function evaluation, target-code execution, or Roslyn scripting is sandboxed.

Optional execution and editing tracks are deliberately kept outside this debugger roadmap:
[TARGET_CODE_EXECUTION_PLAN.md](TARGET_CODE_EXECUTION_PLAN.md),
[ASSEMBLY_EDITING_PLAN.md](ASSEMBLY_EDITING_PLAN.md), and
[DNSPY_SCRIPTING_PLAN.md](DNSPY_SCRIPTING_PLAN.md).

## Testing strategy

### Unit tests

- Protocol serialization and compatibility
- Handle generation and invalidation
- Event cursor and truncation behavior
- Program identity and session-state precedence
- Capability and authorization policy
- MCP argument validation and structured errors
- Paging and output bounds
- Multi-target selection and `ambiguous_target` no-op behavior
- Breakpoint interchange validation, dry-run, deduplication, and merge/replace semantics
- Value-export hashing, path containment, and overwrite policy

### Integration test targets

Create deterministic programs containing:

- nested calls with known locals and arguments
- async and iterator state machines
- multiple threads
- multiple processes and runtimes with independently changing state
- thrown and caught exceptions
- overloaded and generic methods
- properties with side effects
- dynamically loaded assemblies
- categorized exceptions, debugger output, and values suitable for object IDs and byte export
- optimized and unoptimized builds
- a long-running method for pause and stepping tests

For the current scope, the automated target is x64 .NET Framework; the Mono/Unity engine is covered by a manual checklist against UCH until a scriptable Unity target exists. Keep engine-specific expected results separate so CoreCLR can be added later without rewriting them.

### End-to-end scenarios

1. List programs and attach.
2. Set a breakpoint before module load.
3. Wait for the breakpoint.
4. Inspect call stack, arguments, locals, and object members.
5. Navigate to decompiled C# and IL.
6. Evaluate a read-only expression.
7. Modify a value with permission enabled.
8. Step and observe the next stop event.
9. Resume and verify old handles are rejected.
10. Create an object ID, resume, pause again, and evaluate or explicitly release it according to capability.
11. Stop on a matching module load and read the corresponding cursor-based output.
12. Export and dry-run import a mixed breakpoint set, then merge it without duplicates.
13. Export a byte value through chunks and verify its SHA-256; reject an unauthorized host path.
14. Select between multiple active targets and prove an omitted ambiguous selector performs no action.
15. Detach while leaving the target alive.
16. Repeat through a VM tunnel.

## Repository and dependency maintenance

Not part of any phase, but both items block "a clean checkout can build this" and neither is a debugger
concern. Recorded 2026-08-03; nothing has been done about either.

### 1. `Mono.Debugger.Soft` has a local-only commit — decide where it lives

All seven submodules come from `https://github.com/dnSpy/`. Six sit on commits that exist on those
remotes and will clone forever. One does not:

| Submodule | State |
|---|---|
| ICSharpCode.Decompiler, NRefactory, netcorefiles, ICSharpCode.TreeView, Roslyn.ExpressionCompiler, dnSpy.Images | on public commits |
| **Mono.Debugger.Soft** | **`888ded0` "Bound Unity stack-frame fetch waits" is on no remote** |

That commit is the fix that stopped dnSpy wedging when a Unity thread exits during frame retrieval —
`ThreadMirror.GetFrames()` bounded to 3 s. The superproject records `888ded0` as its gitlink, so a fresh
clone plus `git submodule update --init` cannot resolve it. **Phase 0's exit criterion "a clean checkout
can build dnSpy, build dgSpy, and deploy the extension with two documented commands" is therefore
currently false.**

Options, in the order they were judged:

1. **Fork `dnSpy/Mono.Debugger.Soft`** and push `888ded0` to it; change that one `.gitmodules` URL. Keeps
   the repo's shape uniform and self-documents the change GPLv3 §5 asks to be stated. Needs a GitHub
   account action.
2. **Vendor it** — drop the submodule and commit its 87 `.cs` files. Self-contained, nothing external to
   remember, loses an upstream merge path that is worth little because dnSpy is archived.
3. A local `file://` mirror. Started and then abandoned: it bakes a machine-local absolute path into a
   tracked file and protects against nothing but an accidental `git gc`.

A local `dgspy` branch now pins `888ded0` inside the submodule so it cannot be pruned; before that it was
reachable only from a detached HEAD and the gitlink. A bare mirror exists at
`C:\Users\mjb\develop\UCH-dev\mirrors\Mono.Debugger.Soft.git` and is not referenced by anything tracked.

Related, and required before publishing: the superproject's own `origin` is `github.com/dnspy/dnspy`,
which cannot be pushed to, so **every dgSpy commit is local-only as well**. And
[DGSPY_BASELINE.md](DGSPY_BASELINE.md) claims dgSpy makes exactly one edit to dnSpy sources;
there are two — the `DbgMessageThreadExitedEventArgs` `ExitCode` fix and this frame-fetch bound. GPLv3 §5
wants both stated.

Licensing is not an obstacle to any option: dnSpy is GPLv3, `Mono.Debugger.Soft` is MIT X11 in origin
(`dnSpy/dnSpy/LicenseInfo/OtherLicenses.txt:164`) and GPLv3 as dnSpy ships it, so vendoring or forking
both work provided that notice is retained. Note that `dgSpy.Protocol` and `dgSpy.Gateway` reference no
dnSpy assembly and talk over a socket, so only `Extensions/dgSpy.Extension` is unavoidably GPLv3; if the
gateway and wire protocol should ever be reusable under permissive terms, split them before publishing.

### 2. Check whether the upstream sources have moved on

Every dependency here is pinned to a dnSpy fork made years ago, and dnSpy itself is archived. The
*original* projects behind those forks have not all stopped. Worth an investigation pass:

| dnSpy fork | Original project | Why it might matter |
|---|---|---|
| `Mono.Debugger.Soft` | Mono's soft-debugger client | The frame-fetch hang we patched by hand may be fixed upstream, better. Mono's debugger protocol also gained newer commands. |
| `ILSpy` / `ICSharpCode.Decompiler` | ILSpy | Years of decompiler correctness work; directly improves `get_csharp`. |
| `NRefactory` | NRefactory / Roslyn-era successors | Largely superseded; check whether it is still needed at all. |
| `Roslyn.ExpressionCompiler` | Roslyn | Expression evaluation and func-eval quality — Phase 5 and Phase 7 depend on it. |
| `dnlib` (not a submodule) | dnlib | Metadata reading; newer versions handle more edge cases in dynamic modules, which Phase 6 left open. |
| dnSpy itself | dnSpyEx (active community fork) | The likeliest single win: dnSpyEx carries years of fixes to exactly the engine paths dgSpy drives. |

Scope the investigation before doing any of it: for each, establish what changed, whether the fix is one
dgSpy actually hits, and what the migration costs. **Do not start a wholesale rebase onto dnSpyEx as a
side quest** — it would invalidate every live verification recorded in `docs/DGSPY_STATUS.md`, which is
the project's main asset. Evaluate, write down the findings, then decide.

## Documentation deliverables

- Architecture and trust-boundary document
- Local installation and dnSpy extension deployment
- MCP client configuration
- VM and remote-tunnel recipes
- Tool reference with state and handle semantics
- Runtime capability matrix
- Security and authorization guide
- Optional capability plans for target-code execution, assembly editing, and dnSpy-host scripting
- Troubleshooting guide for attach permissions, architecture mismatch, unavailable locals, and stuck evaluations

## Definition of done

dgSpy is complete when an authorized remote AI agent can, without UI automation:

1. Discover supported programs on a selected host.
2. Attach to or launch a target.
3. Set and manage breakpoints by stable code identity.
4. Wait reliably for breakpoint, exception, pause, and step events.
5. Pause, resume, step, detach, and terminate according to permissions.
6. Inspect processes, runtimes, modules, threads, call stacks, arguments, locals, fields, watches, and exceptions.
7. Evaluate expressions and deliberately mutate target state where supported and authorized.
8. Navigate and search assemblies as decompiled C#, IL, and metadata.
9. Use advanced memory, disassembly, register, and instruction-pointer operations when supported by the active engine.
10. Select and control multiple active processes and runtimes without ambiguous implicit targeting.
11. Use persistent object IDs, Autos, module breakpoints, breakpoint interchange, richer exception policies,
   value export, typed analyzer relationships, and bounded debugger output when supported.
12. Recover cleanly from disconnects, exits, stale handles, timeouts, and unsupported runtime features.
13. Perform all remote communication through an authenticated, encrypted, auditable transport.

## First implementation milestone — delivered

All ten items below are implemented and verified; see `docs/DGSPY_STATUS.md` for the record. Kept as
written because it is the list the boundaries were chosen against.

The first useful vertical slice should include only:

1. MEF extension loading.
2. Loopback TCP handshake.
3. `list_programs`.
4. `attach`.
5. `get_session_state`.
6. `pause` and `continue`.
7. One IL-offset breakpoint.
8. `wait_for_stop` with an event cursor.
9. `get_callstack` and primitive locals.
10. Local Streamable HTTP MCP gateway with `Origin` validation and a local shared secret.

This slice validates the difficult boundaries—dnSpy threading, debugger object lifetimes, event delivery, and MCP cancellation—before expanding the tool surface.
