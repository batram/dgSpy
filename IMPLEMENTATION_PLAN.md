# dgSpy MCP Implementation Plan

**Current state:** see [docs/DGSPY_STATUS.md](docs/DGSPY_STATUS.md) for what is implemented and
verified, what is still open, and the handoff notes. This document is the target, not the status.

## Objective

Expose dnSpy's debugger, decompiler, metadata, search, and scripting capabilities to AI agents through MCP. Agents must be able to discover programs, attach or launch, control execution, set breakpoints, wait for stops, inspect complete debugger state, evaluate expressions, and navigate decompiled C# and IL.

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

Anything below marked "full target" stays in the plan for direction; the exit criteria for Phases 0 and 1 apply only to the scope above.

## Design principles

- Use dnSpy's public APIs and MEF extension model; do not automate the WPF UI.
- Run the debugger extension on the same host as dnSpy and the processes being debugged.
- Bind the extension transport exclusively to loopback TCP by default.
- Keep Milestone 1 local-only and unauthenticated while the transport and debugger lifecycle are stabilized. Add authentication before any non-loopback or multi-user deployment.
- Reach remote hosts through SSH, WireGuard, or a mutually authenticated TLS gateway.
- Model debugger actions as typed operations instead of exposing C# scripting as the primary API.
- Separate target expression evaluation from scripts executed inside dnSpy.
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
    Rpc/
        RpcException.cs
        RpcHost.cs

dgSpy.Protocol/
    dgSpy.Protocol.csproj
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

`dgSpy.Protocol` must contain DTOs only and must not reference WPF or dnSpy implementation assemblies. This keeps the gateway independently testable and allows transport replacement without changing debugger behavior.

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

## Phase 1: Local RPC and discovery

### Work

1. Implement a versioned local RPC protocol between the gateway and extension.
2. Use TCP bound exclusively to `127.0.0.1` (and optionally `::1` once dual-stack behavior is tested). Never bind the extension RPC listener to wildcard, LAN, or VM-facing interfaces.
3. Add handshake, version negotiation, request IDs, cancellation, deadlines, and structured errors. Cancellation must reach the debugger operation itself, not only the socket: an expired deadline has to abandon or abort the queued dispatcher work and report `deadline_exceeded` once.
4. Implement process discovery using `AttachableProcessesService` rather than raw `Process.GetProcesses()` as the authoritative list. Allow the caller to select attach providers, because Unity discovery performs a multi-second network scan on every unfiltered enumeration.
5. Report duplicate entries when a process exposes multiple supported runtimes.
6. Build `program_id` from typed identity fields, never from `RuntimeId.ToString()`. `RuntimeId` has no string form; the durable identity is PID plus provider plus the engine's own discriminator (CLR version for CorDebug, address and port for Mono/Unity). Surface `RuntimeGuid` as well as `RuntimeKindGuid`, since both supported engines share the same kind GUID and are only distinguishable by runtime GUID.
7. Add health, version, and capability operations.
8. Reject cross-origin browser traffic at the gateway before any tool runs: require a loopback or absent `Origin`, and require a locally generated shared secret. Without this, any web page the user visits can drive the debugger through the loopback MCP endpoint, which no amount of loopback-only binding prevents.

### Initial operations

- `GetHostInfo`
- `GetCapabilities`
- `ListPrograms`
- `Ping`

Milestone 1 exposes a single implicit host and a single session. `host_id` routing, multiple concurrent sessions, and session ownership arrive with Phase 9; until then the protocol carries `session_id` only, and a second `attach` while a session is live is an error rather than a silent replacement.

### Exit criteria

- Gateway reconnects after dnSpy restarts.
- Process discovery returns PID, executable, title, architecture, runtime identity, runtime GUID, and attach provider.
- Two runtimes reachable at the same PID produce two distinct, stable `program_id` values.
- The extension RPC port is unreachable through non-loopback interfaces.
- A cross-origin `fetch` from a web page cannot invoke any tool.
- An expired deadline cancels the in-flight debugger operation and returns a structured error.
- Protocol compatibility failures are explicit and actionable.

## Phase 2: Attach, launch, and lifecycle control

### Work

1. Attach using the exact options returned by the chosen `AttachableProcess`.
2. Add launch support through dnSpy debugger start options, not `Process.Start` followed by a race-prone attach.
3. **`attach_endpoint` (implemented)** — attach directly to a Mono soft-debugger endpoint by address and port. Required for the UCH workflow: a target launched with `--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:55555,suspend=n` emits no multicast beacon, so no attach provider will ever enumerate it and `list_programs` cannot reach it. This is the only route to the Mono/Unity engine. `UnityAttachToProgramOptions` is internal to dnSpy's Mono engine; the public equivalent is `UnityConnectStartDebuggingOptions` (`Address`, `Port`, `ProcessIsSuspended`, `ConnectionTimeout`) passed to `DbgManager.Start`, which `DbgEngineProviderImpl` maps onto the same engine and `DbgEngineImpl.StartCore` treats as an attach.
4. Track debugger processes and runtimes from `DbgManager` events.
   - `attach` must not report ready until the engine has enumerated **threads**, not merely a process. A pause issued inside that window produces a stop with no current thread and an empty call stack, and the session does not recover until it runs again. Waiting for the first thread costs a few hundred milliseconds and makes pause-then-inspect deterministic.
   - The call stack follows `DbgManager.CurrentThread`, which only dnSpy's UI or a thread-carrying stop sets. Headless callers must select a thread themselves when none is current.
5. Implement pause, continue, detach, stop, and restart where supported.
6. Define selection rules for sessions containing multiple processes or runtimes.
7. Preserve process-exit and attach-failure details in the event log. Attach failures have two shapes and must stay distinguishable: options `DbgManager.Start` rejects outright are a caller error and never become a session, whereas an engine that starts and then fails to connect reports through `DbgManager.MessageUserMessage`, which is what makes a `faulted` session carry a real reason instead of a timeout guess.

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

### Exit criteria

- An agent can list and attach to a .NET test process by PID and runtime.
- State transitions are observable without reading dnSpy UI state.
- Detach leaves the target alive; terminate has separately tested semantics.
- **Observed 2026-08-03**: with a CorDebug session attached to a live process, closing dnSpy and confirming its shutdown prompt *terminated the attached target*. `detach` is now implemented and verified as the safe exit; the dnSpy shutdown path still needs its own test.
- Unexpected target exit produces a terminal event and releases all handles.

## Phase 3: Event stream and breakpoint waiting

### Work

1. Subscribe to debugger pause, breakpoint, step, exception, process, runtime, module, and thread events.
2. Normalize events into a per-session bounded sequence with monotonically increasing IDs.
3. Implement cancellable long polling with `after_event_id` and a bounded timeout.
4. Return immediately if an unseen event already exists.
5. Preserve the stop reason, process, thread, breakpoint, exception, and location.
6. Ensure concurrent waiters do not consume events destructively.

### MCP tools

- `get_events`
- `wait_for_event`
- `wait_for_stop`
- `get_stop_reason`

### Exit criteria

- An agent can set up a wait before or after a breakpoint hit without losing the event.
- Cancellation and timeout do not leak tasks or event subscriptions.
- Resume followed by another stop produces a new state version and event ID.
- Event-buffer truncation is reported with the new oldest available cursor.

## Phase 4: Breakpoints and stepping

### Work

1. Resolve source-style locations by module/type/method and IL offset.
2. Support metadata-token and exact IL-offset breakpoints as the dependable base representation. "Exact IL offset" is not portable: Mono accepts only sequence points and rejects everything else with `NO_SEQ_POINT_AT_IL_OFFSET`, while CorDebug accepts any offset. Advertise this as a capability and, where sequence points are known, offer to snap a requested offset to the nearest one rather than returning a breakpoint that silently never binds.
3. Add decompiled C# line mapping where sequence-point or decompiler mappings permit it.
4. Implement enabled state, conditions, hit counts, trace messages, and exception settings.
5. Report binding state and per-runtime bound-breakpoint errors. This is not cosmetic: without it `set_il_breakpoint` returns an id for a breakpoint the engine refused, and the caller's only symptom is a `wait_for_stop` that never fires. Observed against UCH on 2026-08-03.
6. Scope breakpoints to the session, or expose `clear_breakpoints`. dnSpy's `DbgCodeBreakpointsService` is global, so breakpoints survive `detach` and rebind on the next attach — a fresh session can stop on a breakpoint its caller never set.
7. Implement step into, over, and out using the selected thread's `DbgStepper`.
8. Close steppers and cloned code locations on every terminal path.

### MCP tools

- `set_breakpoint`
- `set_il_breakpoint`
- `list_breakpoints`
- `update_breakpoint`
- `remove_breakpoint` (implemented)
- `clear_breakpoints`
- `set_exception_breakpoint`
- `step_into`
- `step_over`
- `step_out`

### Exit criteria

- A breakpoint can be set before its module loads and later reports as bound.
- Conditional breakpoints and hit counts are covered by integration tests.
- Step completion is returned through the same event mechanism as breakpoint stops.
- Invalid or ambiguous method locations return candidates instead of silently choosing one.

## Phase 5: Threads, call stacks, locals, and watches

### Work

1. Enumerate processes, runtimes, app domains, modules, threads, and stack frames.
2. Capture stack frames only while paused and close dnSpy frame objects correctly.
3. Expose arguments, locals, `this`, exceptions, return values, and object members.
4. Add paging, maximum depth, cycle detection, string limits, collection limits, and evaluation timeouts.
5. Preserve raw type information separately from formatted display text.
6. Support writing locals, parameters, and fields where the runtime permits it.
7. Implement watch expressions as stored expressions re-evaluated against a selected frame, not as permanent value handles.

### MCP tools

- `list_processes`
- `list_runtimes`
- `list_modules`
- `list_threads` (implemented)
- `get_callstack` (implemented with caller-selected `thread_id`)
- `get_frame` (implemented with caller-selected `thread_id` + `frame_index`)
- `select_frame`
- `get_arguments`
- `get_locals`
- `get_this`
- `get_exception`
- `get_members`
- `evaluate`
- `set_value`
- `add_watch`
- `list_watches`
- `remove_watch`

### Exit criteria

- A breakpoint hit can be followed by call-stack and local-variable inspection through MCP only.
- Optimized-away and unavailable values are distinguished from `null`.
- Object expansion cannot recurse indefinitely or return unbounded data.
- All paused-state handles become predictably stale after resume.

## Phase 6: Decompiled C#, IL, metadata, and search

### Work

1. Resolve loaded modules to dnSpy documents and in-memory module images.
2. Expose assembly, module, namespace, type, member, and metadata-token navigation.
3. Decompile assemblies, types, and methods using an explicitly selected language.
4. Provide IL instructions with offsets, operands, exception regions, locals, and sequence mappings.
5. Search loaded documents and optionally user-opened documents by type/member/text pattern.
6. Add reference analysis through dnSpy analyzer services where reusable; otherwise implement a headless service over the same metadata model.
7. Return paged results and stable symbol identities.

### MCP tools

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

- The agent can navigate from a paused frame to its method's C# and IL.
- Dynamic and self-modifying modules can use their in-memory image when available.
- Search results include enough identity to set breakpoints without parsing display text.
- Large assemblies and result sets remain bounded and cancellable.

## Phase 7: Advanced evaluation and low-level debugging

### Work

1. Support target method invocation and object construction behind explicit side-effect controls.
2. Add memory reads and writes where the active engine supports them.
3. Add native/managed disassembly and registers when exposed by the runtime.
4. Add set-instruction-pointer after validating the selected frame and target location.
5. Surface runtime feature flags on every relevant operation.
6. Implement hard evaluation timeouts and recovery for hung function evaluation where the engine permits aborting it.

### MCP tools

- `invoke_method`
- `create_object`
- `read_memory`
- `write_memory`
- `get_disassembly`
- `get_registers`
- `set_instruction_pointer`

### Exit criteria

- Read-only inspection remains available when mutating capabilities are disabled.
- Every side-effecting call is labeled and audited.
- Unsupported features return structured capability failures.
- Evaluation failures do not leave the session permanently unusable without an explicit reported fault.

## Phase 8: dnSpy scripting

dnSpy C# Interactive code runs inside dnSpy, not inside the paused target. It can access dnSpy services and therefore has control equivalent to code running as the dnSpy user.

### Work

1. Extract or wrap the Roslyn scripting engine behind a non-UI service.
2. Capture standard output, return value, diagnostics, duration, and cancellation state.
3. Use a separate authorization capability from debugger expression evaluation.
4. Disable scripting by default for remote clients.
5. Add configurable assembly/reference allowlists if practical, while documenting that in-process scripting is not a security sandbox.

### MCP tools

- `execute_dnspy_script`
- `reset_dnspy_script_context`

### Exit criteria

- Scripting works without opening or driving the C# Interactive tool window.
- Requests are serialized or isolated so concurrent agents cannot corrupt shared script state.
- The audit log contains caller identity and a digest of executed code.
- Documentation states clearly that enabling this tool grants host-level code execution as the dnSpy user.

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
   - inspect
   - control execution
   - mutate target state
   - terminate processes
   - execute dnSpy scripts
6. Add structured audit records with secrets and inspected values redacted by policy.
7. Add connection-loss behavior that does not automatically resume, detach, or terminate a paused target unless configured.

### Exit criteria

- An MCP client on another host can securely discover and debug a target inside a VM.
- The debugger is not reachable from the VM network without the selected tunnel or authenticated listener.
- Reconnection preserves session state and event cursors where the dnSpy process survived.
- Authorization tests prove that inspection-only clients cannot resume, mutate, terminate, or script.

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
- Separate read, control, mutation, termination, and scripting permissions.
- Request, response, expression, script, and decompilation size limits.
- No secrets in ordinary logs.
- Audit side-effecting operations.
- Explicit opt-in for memory writes, function calls, target termination, and dnSpy scripting.
- Never claim Roslyn scripting or debugger function evaluation is sandboxed.

## Testing strategy

### Unit tests

- Protocol serialization and compatibility
- Handle generation and invalidation
- Event cursor and truncation behavior
- Program identity and session-state precedence
- Capability and authorization policy
- MCP argument validation and structured errors
- Paging and output bounds

### Integration test targets

Create deterministic programs containing:

- nested calls with known locals and arguments
- async and iterator state machines
- multiple threads
- thrown and caught exceptions
- overloaded and generic methods
- properties with side effects
- dynamically loaded assemblies
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
10. Detach while leaving the target alive.
11. Repeat through a VM tunnel.

## Documentation deliverables

- Architecture and trust-boundary document
- Local installation and dnSpy extension deployment
- MCP client configuration
- VM and remote-tunnel recipes
- Tool reference with state and handle semantics
- Runtime capability matrix
- Security and authorization guide
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
10. Optionally execute dnSpy-host C# scripts under a distinct high-risk permission.
11. Recover cleanly from disconnects, exits, stale handles, timeouts, and unsupported runtime features.
12. Perform all remote communication through an authenticated, encrypted, auditable transport.

## First implementation milestone

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
