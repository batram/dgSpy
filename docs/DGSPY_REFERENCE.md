# dgSpy local tool and behavior reference

This documents the delivered local tool surface through the completed first implementation section.

Three components:

- `dgSpy.Protocol`: versioned JSON DTOs used over loopback TCP.
- `Extensions/dgSpy.Extension`: the MEF extension and authoritative debugger-state owner.
- `dgSpy.Gateway`: a loopback-only Streamable HTTP MCP endpoint.

The extension is organized by responsibility: MEF lifetime at the root, RPC transport/dispatch in
`Rpc/`, debugger scheduling and state in `Debugger/`, bounded cursor handling in `Events/`, and stable
protocol identities in `Identity/`. See `Extensions/dgSpy.Extension/README.md` before adding tools.

Build and deploy with `.\build-dgspy.ps1` (see [DGSPY_BASELINE.md](DGSPY_BASELINE.md)), then start dnSpy
and the gateway with the same `DGSPY_RPC_PORT`:

```powershell
$env:DGSPY_RPC_PORT = '7351'
dotnet run --project .\dgSpy.Gateway\dgSpy.Gateway.csproj -c Release
```

The MCP endpoint is `http://127.0.0.1:7350/mcp`; `GET /health` is unauthenticated for process checks.
`DGSPY_URL` overrides the address.

`initialize` returns `Mcp-Session-Id`; clients send it on later requests. Legacy local callers without
the header share the compatibility identity `legacy-local`. `DGSPY_ACCESS_MODE` is `full-control`
(default) or `inspect-only`. `DGSPY_CONTROLLER_IDLE_SECONDS` defaults to 300 (minimum 30), and expiry
releases only ownership. `DGSPY_AUDIT_FILE` overrides the rotating 5 MiB redacted JSONL audit at
`%LOCALAPPDATA%\dgSpy\gateway-audit.jsonl`.

For packaged-host status and the planned extension-initiated Gateway registration flow, see
[remote hosts](REMOTE_HOSTS.md).

## Authentication

Every `/mcp` request must carry `X-dgSpy-Token`. Set `DGSPY_TOKEN` to choose the value, otherwise the
gateway generates one at startup and writes it to `%LOCALAPPDATA%\dgSpy\gateway.token`.

The gateway-to-extension RPC hop uses a separate credential. The extension reads `DGSPY_RPC_TOKEN` or
generates `%LOCALAPPDATA%\dgSpy\rpc.token`; the gateway reads the same source. The extension also reads
`DGSPY_HOST_ID` or persists a generated identity in `%LOCALAPPDATA%\dgSpy\host.id`. Every non-handshake
RPC request must carry both values, and the gateway refuses a handshake that differs from a configured
`DGSPY_HOST_ID`. These values authenticate the local RPC hop; they do not make the loopback listener a
remotely supported transport.

Requests are also rejected when they arrive from a non-loopback address, or carry an `Origin` that is
not loopback. This is not optional hardening: a web page the user visits can POST to `127.0.0.1`
without a preflight, so loopback binding alone would leave the debugger open to any site.

## Tools

- Host, discovery, and lifecycle: `list_hosts`, `get_host_info`, `get_capabilities`, `list_programs`, `attach`,
  `attach_endpoint`, `launch`, `list_sessions`, `get_session_state`, `get_session_controller`,
  `claim_session`, `release_session`, `pause`, `continue`, `detach`, `terminate`, `restart`.

Every tool in these extension-backed families accepts `host_id`. It may be omitted only when the Gateway
registry contains exactly one host. `list_hosts` is Gateway-local and needs no host selection.
- Events and output: `get_events`, `wait_for_event`, `wait_for_stop`, `get_stop_reason`, `get_output`,
  `wait_for_output`.
- Threads and values: `list_threads`, `get_callstack`, `get_frame`, `evaluate`, `get_members`, `set_value`,
  `get_exception`, `add_watch`, `list_watches`, `remove_watch`, `get_autos`, `create_object_id`,
  `list_object_ids`, `evaluate_object_id`, `release_object_id`, `get_value_export`, `write_value_export`.
- Breakpoints and control: `set_il_breakpoint`, `set_breakpoint`, `list_breakpoints`, `update_breakpoint`,
  `remove_breakpoint`, `clear_breakpoints`, `set_exception_breakpoint`, `list_exception_breakpoints`,
  `step_into`, `step_over`, `step_out`, `set_module_breakpoint`, `list_module_breakpoints`,
  `update_module_breakpoint`, `remove_module_breakpoint`, `export_breakpoints`, `import_breakpoints`,
  `list_exception_categories`, `list_exception_policies`, `set_exception_policy`,
  `remove_exception_policy`, `restore_exception_defaults`.
- Code and metadata: `search`, `list_modules`, `list_documents`, `list_types`, `list_members`, `search_symbols`,
  `get_il`, `get_csharp`, `search_text`, `find_references`, `find_implementations`, `get_metadata`,
  `get_raw_module`, `analyze_symbol`.
- Explicit side effects and low-level access: `invoke_method`, `create_object`, `read_memory`,
  `write_memory`, `get_disassembly`, `get_registers`, `set_instruction_pointer`.

## State and lifetime rules

- `attach` and `launch` assign their initialized MCP session as controller. Other controllers may inspect
  but cannot mutate that debugger session. Mutations use the narrow revision they depend on:
  `expected_lifecycle_version` for detach/terminate/restart, `expected_execution_version` for target and
  frame mutations, and `expected_breakpoints_version` for breakpoint/policy changes. Frame-bound
  mutations also require the opaque `expected_stop_id`. Missing or stale relevant guards fail before
  acting; unrelated thread/module events do not invalidate them. The deprecated `expected_state_version`
  alias has been removed: only the scoped guards exist, and an `expected_state_version` argument is
  ignored. `release_session` changes only
  ownership. An idle owner or Gateway restart leaves the target untouched and requires explicit
  `claim_session` before further mutations.
- MCP disconnect, controller expiry, Gateway disconnect/restart, and remote-host disconnect never resume,
  detach, terminate, or restart a target. Recovery is selection plus ownership, not implicit target control.

- `get_host_info` identifies the host: dnSpy/dgSpy versions, machine, architecture, supported engines,
  and the live `session_id` if there is one. `dgspy_version` and `extension_sha256` are derived from the
  extension assembly that the process actually loaded, and `extension_path` names the tree it came from.
  Trust those over any other version string: a stale deployment reports a stale hash, whereas a
  hand-maintained version number keeps looking current no matter how old the running code is.
  `get_local_deployment` and `doctor` compare the installed payload against the deployed one and report
  `stale` with recovery when they differ. `get_capabilities` reports per-operation time bounds,
  per-engine behavior, and limits. Engine differences are advertised, not assumed — most importantly
  that Mono/Unity binds breakpoints only at sequence points and that a deadline cannot abort work that
  has already started (`limits.cancels_in_flight_work: false`).
- `list_programs` unfiltered probes every process and takes seconds. Pass `process_ids` or
  `process_names` (wildcards allowed) when the target is known — that is ~50 ms instead of ~2500 ms.
  `provider_names` selects dnSpy attach providers (`DotNetFramework`, `DotNet`, `UnityEditor`,
  `UnityPlayer`) and skips the rest; each entry reports the providers that can produce it in
  `attach_providers`, ready to pass back. `UnityPlayer` is the multicast scan and never runs unless
  named. Each call replaces the set of valid `program_id` values — including a filtered call that
  returns nothing.
- `program_id` is composed from PID, runtime GUID, and the engine's discriminator (CLR version for
  CorDebug). `runtime_name` reports that discriminator; there is no duplicate string `runtime_id`.
  `runtime_guid` is what separates .NET Framework from Unity/Mono; they share a kind GUID.
- `command_line` is the command line dnSpy reads while enumerating the process. Use it with `pid`,
  `executable`, and `title` to distinguish otherwise-identical host and worker processes.
- When a user supplies only a PID, resolve it with `list_programs(process_ids: [PID])`, then pass the
  exact returned `program_id` to `attach`. A PID can contain multiple managed runtimes, so `attach`
  deliberately does not guess one. `attach` waits until the engine has enumerated threads before returning, avoiding a threadless stop.
  An arbitrary Unity pause can still expose only native/unavailable stacks; inspect a caller-selected
  thread or stop at a known managed breakpoint. It refuses a second session while one is live.
- `attach_endpoint` connects to a Mono/Unity soft-debugger endpoint by address and port. Use it for a
  target launched with `--debugger-agent=transport=dt_socket,server=y,address=HOST:PORT`: that target
  broadcasts no discovery beacon, so `list_programs` can never see it and `attach` has no `program_id`
  to take. Pass `process_is_suspended: true` when the agent argument said `suspend=y`. The session is
  an attach either way, so `detach` leaves the target running. See
  [historical Unity checklist](history/DGSPY_UNITY_CHECKLIST.md).
- A session that comes up and then fails reports `state: "faulted"` with `fault_message` — dnSpy's own
  connect-failure text when it produced one. Options dnSpy rejects outright are a caller error and
  return `attach_failed` instead, without creating a session. A faulted session still holds the
  `session_id`; clear it with `detach`.
- `pause` and `continue` return the state they produced, not the state before the transition.
- **`detach` is the only safe way to end a session.** Closing dnSpy with a session attached terminates
  the target. `detach` refuses with `detach_would_terminate` when dnSpy cannot detach cleanly, unless
  `allow_terminate=true`. If the engine does not actually remove the target within ten seconds, it
  returns `detach_timed_out`, preserves the active session, and emits no false detached event.
- **`launch` uses dnSpy start options, not `Process.Start` plus attach.** `restart` is therefore available
  only for a dgSpy-launched target. `terminate` is always explicit and separate from safe `detach`.
- A target exit leaves a terminal session that can still be inspected with `get_session_state` and
  `get_events`. The event carries PID, exit code, terminal reason, and a terminal flag. Call `detach` to
  clear the terminal session, or start the next session once the debugger has stopped.
- `list_sessions` recovers a lost `session_id`.
- `list_breakpoints` reports `engine_hit_count`: times the engine reached the breakpoint this
  session, counted before conditions, hit counts, and filters run. A conditional breakpoint whose
  condition keeps evaluating false still ticks it — that is how "working condition, not yet true" is
  distinguished from "never reached" during an otherwise silent wait. Absent when no session is
  active; reset when a new session starts.
- `wait_for_stop`, `wait_for_event`, and `get_events` reject an `after_event_id` beyond the newest
  event with `cursor_ahead_of_stream` instead of waiting forever or returning a clean empty result —
  such a cursor would skip the very ids the next events take. The known way to produce one is feeding
  a version counter where a cursor belongs.
- `event_id` is only an event cursor. `state_version` remains as a legacy all-event counter. The scoped
  revisions report relevant change domains and return `stale_lifecycle`, `stale_execution`,
  `stale_breakpoints`, or `stale_stop` on mismatch. `stop_id` changes only when the target reaches a new
  stop and is cleared on resume.
- Every operation that takes a version guard echoes the full current vector back in a `versions`
  object: `{lifecycle_version, execution_version, breakpoints_version, stop_id, last_event_id}`,
  read after the operation applied. Feed the next mutation's `expected_*` guards from there instead of
  an interposed `get_session_state`. **`versions.last_event_id` is not a wait cursor for an event the
  same call causes.** It is the newest event at response time, which is right for reading history with
  `get_events` and wrong for `wait_for_stop` after a resume: a breakpoint on a hot path is hit before
  `continue` returns, so the stamped cursor already includes that `stopped` event and waiting from it
  waits for the next one. Measured, not theorised — a `Probe` breakpoint reported
  `engine_hit_count: 1` while `wait_for_stop` reported `timed_out: true`. Capture the cursor before the
  resume; `set_breakpoint` and `set_il_breakpoint` return one as `cursor_event_id`. `stop_id` is `null`
  while the target runs. The one exception is `restore_exception_defaults`, whose result is a bare
  boolean. For a step that returns `completed: false`, the vector describes the state at response
  time — the in-flight step's stop, when it lands, arrives on the event stream with its own versions.
- `attach`, `attach_endpoint`, `launch`, `wait_for_stop` and `wait_for_event` carry the vector too,
  though none of them takes a guard. They are where a caller has no counters at all: a session opens
  with a mutation whose guard could otherwise only come from an interposed `get_session_state`, and a
  wait is where the caller learns the stop that moved `execution_version` and `stop_id` happened.
- **The vector for an execution change is stamped after that change is recorded, not when the call
  returns.** `execution_version` moves when the engine's `Continued`/`Stopped` event reaches the event
  buffer, which happens after the RPC has composed its answer. Stamping without waiting made `continue`
  hand back the value from *before* the resume, so the next guarded call died with
  `stale_execution: expected 6, current 7` — quoting a number that same response had supplied.
  `pause`, `continue` and the three steps therefore wait, bounded at 750 ms, for their own execution
  change to land. The wait is best effort: a step that never lands must not fail the call, so a timeout
  stamps what is known and leaves the caller no worse off than before the echo existed.
- Gateway compositions (`step_and_inspect`, `run_to_method`, `run_to_location`, `trace_calls`) lift a
  `versions` object to the top of their result. A composition is where the counters are hardest to
  guess, because several guarded calls moved them and only the Gateway saw the intermediate responses.
  The source is the call that observed the final state: the wait for the stepping tools, and the
  temporary-breakpoint removal for `run_to_*` — removing it moves `breakpoints_version` again, so
  stamping from the wait would return a guard that is stale on arrival.
- `list_threads` requires a paused session and returns stable `thread_id` values as
  `process_id:os_thread_id`, including managed ID, name and state. It deliberately does not fetch every
  stack: Unity threads can exit during frame retrieval, and some Mono runtimes never answer that raced
  request. Pass an ID to `get_callstack` for a caller-owned stack walk of that exact thread; this does
  not depend on dnSpy's UI callstack asynchronously following `CurrentThread`. Omit it to retain the
  best-effort managed-frame probe. `get_frame(thread_id, frame_index)` inspects exactly one frame.
- **`states` does not predict whether a func-eval can run; `include_evaluability` does.** CorDebug
  publishes `UnsafePoint` in `states`, Mono publishes no states at all, so a rule learned on one engine
  is silently wrong on the other. `list_threads(include_evaluability=true)` probes each thread and adds
  `can_evaluate` plus `evaluate_blocked_reason` — `no_frames`, `native_frame`, or `unsafe_point`, in the
  order the engine itself checks them. It costs one bounded stack walk per thread, which is why it is
  opt-in: on Unity that is the call a disappearing thread can stall. Measured against both engines, the
  prediction matched what `invoke_method` actually did on every thread, in both directions.
- **A func-eval is refused per thread, so pick the thread rather than stepping.** The stock dnSpy text
  says "Step once or run until a breakpoint hits", and on an idle process the first half is useless:
  every thread is blocked in a native wait and stepping cannot advance any of them. dgSpy adds a
  `recovery` field to that error saying so. The reliable route is a breakpoint on a method the target
  actually reaches. Note the evaluable frame is often not frame 0 — a thread parked in `Thread.Sleep`
  evaluates in its caller's frame, so pass `frame_index`.
- **`compiler_error` separates your mistake from the engine's refusal.** True means the expression never
  compiled and nothing ran in the target, so fix the expression; false with an error means it compiled
  and the engine declined to execute it here, so the same call can succeed at another thread or stop.
  `set_value` has always reported it; `invoke_method` and `create_object` now do too, classified from
  the Roslyn diagnostic prefix because dnSpy's value-node contract returns only a string.
- **"Executes in the target" is not "needs a func-eval", and `set_value` straddles the line.** Storing a
  value that already exists over there is a direct write and succeeds even at an unsafe point; producing
  one the engine must first create — a string literal, a boxed value, a new object — is a func-eval and
  is refused like any other. The cost of the value decides it, not whether the target is a primitive.
  Measured on CorDebug at an unsafe point: `answer = 1717` assigned, `label = null` assigned,
  `label = "..."` refused with `compiler_error: false`.
- **`get_autos` implements statement-scoped C# Autos independently of dnSpy's NYI provider.** It uses
  dnSpy language debug info to find the current source statement's IL span, maps referenced IL locals,
  parameters, `this`, and fields back to source expressions, and evaluates them through the same bounded
  value pipeline as `evaluate`. Function evaluation remains opt-in.
- **`get_registers` reads a stopped x64 Windows thread context.** It returns RAX-R15, RSP, RBP, RSI, RDI,
  RIP and RFLAGS with unsigned values, fixed-width hex, and bit widths. CorDebug supplies its OS thread id
  directly. Mono soft-debugger protocol 2.3+ supplies `THREAD.GET_TID`; dgSpy records that provenance when
  dnSpy creates the thread and verifies the opened thread belongs to the selected debug process. Older Mono
  agents return `capability_unsupported` rather than treating their managed `ThreadId` fallback as an OS id.
- `get_session_controller` reports `controller_expires_utc` and `controller_expires_in_seconds` when a
  session is owned, plus `controller_is_caller`. A client that lost its transport — an MCP client
  restarting its stdio server gives the session a new identity and strands the old lease on a dead one —
  cannot renew or claim until the lease lapses, so the deadline turns a blind poll into a known wait.
- Frame identity is `thread_id` + `frame_index` for paused selection, and `module` + `method_token` +
  `il_offset` for code identity and `set_il_breakpoint`. `name` is display-only and must not be parsed.
- **On Mono/Unity, `il_offset` must be a sequence point** or the engine refuses the breakpoint. Many
  offsets qualify, but a frame's own `il_offset` often does not — so round-tripping it from
  `get_callstack` into `set_il_breakpoint`, which works on CorDebug, is refused on Mono. Check `bound`:
  `false` with `severity: "error"` will never be hit, `false` with no error is pending a module load.
  By default a refused offset is retried at method entry, which sets `snapped` and a `warning`; pass
  `snap_to_sequence_point=false` to get the failure instead. `bound` claims the engine installed the
  breakpoint, not that it will be reached.
- **`run_to_method` and `run_to_location` are Gateway compositions, not host calls.** The Gateway sets a
  temporary breakpoint, continues, waits, and removes it, building each inner call's arguments itself.
  It used to drop `module` and `expected_breakpoints_version` on the way to `set_breakpoint`, and to
  guard the resume with the deprecated `expected_state_version` alias carrying an `execution_version`
  the host compares against a different counter. Both tools therefore always failed, and both failures
  named a caller mistake — "module is required" to a caller who passed `module`, then a `stale_state`
  quoting two numbers the caller never supplied.
- **Take the event cursor before setting a breakpoint, not after.** Use the `cursor_event_id` that
  `set_il_breakpoint` returns as `wait_for_stop`'s `after_event_id`. On a hot method the breakpoint
  fires before a subsequent `get_session_state` returns, and a cursor read afterwards has already
  missed the stop — the wait then times out on a breakpoint that works.
- Breakpoints are dnSpy-global, not session-scoped: they survive `detach` and rebind on the next
  attach, so a new session can stop on a breakpoint set by the previous one. `remove_breakpoint`
  removes exactly one listed ID; `clear_breakpoints` removes them all, including UI breakpoints.
- **A Mono/Unity endpoint accepts one connection per game launch.** A clean `detach` lets the agent
  listen again; any other connection to the port — including a "is it up?" TCP probe — consumes it for
  good and the target must be relaunched. Let `attach_endpoint` be the only thing that touches it.
- `get_events`, `wait_for_event`, and `wait_for_stop` read one bounded per-session sequence without
  consuming it, so concurrent callers see the same event. Optional `kinds` filter general waits and
  reads. A stale cursor sets `truncated` and returns `oldest_available_cursor`; waits cap at 10 s.
  `get_stop_reason` returns either an exact retained stop or the latest one. Normalized stops preserve
  reason, process, thread, breakpoint or exception identity, and stable IL location when dnSpy supplies it.
- **An unrecognized `kinds` value is rejected with `invalid_argument`, not filtered on.** A near miss
  like `breakpoint` for `breakpoint_hit` used to return a clean empty result, which is indistinguishable
  from "the event never happened" — the same false-negative shape as reading the event cursor too late.
  The error names the offending value and the whole valid set, which `get_capabilities` also serves as
  `event_kinds` and `stop_reasons`.

### Event kinds

`wait_for_stop` filters on `stopped` — the synthesized whole-process stop, which is the one to wait on.
The rest are informational.

| Group | Kinds |
|---|---|
| Stop | `stopped` |
| Session lifecycle | `session_started`, `session_ended`, `attached`, `attach_failed`, `detached`, `restarted`, `continued`, `terminated`, `restart_process_exited`, `session_exited` |
| Process and runtime | `process_created`, `runtime_created`, `runtime_exited` |
| Module and thread | `module_loaded`, `module_unloaded`, `thread_created`, `thread_exited` |
| Raw debugger messages | `exception_thrown`, `breakpoint_hit`, `step_completed`, `entry_point`, `program_break`, `break` |

A `stopped` event carries `stop_reason`: `breakpoint`, `exception`, `step`, `entry_point`,
`program_break`, `pause`, or `unknown`. `unknown` is not an error — a Unity pause can arrive with no
break message dgSpy recognizes — but the caller cannot infer why it stopped.

Note the pairing: `breakpoint_hit` is the raw debugger message, `stopped` with
`stop_reason: "breakpoint"` is the process actually being stopped by it. Waiting on the former can see
an event for a process that is still being suspended. The vocabulary lives in
`dgSpy.Protocol.EventKinds`, and every emitting call site names a constant from it, so a kind cannot
ship without being filterable and advertised in the same edit.

### Two streams, not three

`get_events`/`wait_for_event`/`wait_for_stop` carry the **structured** stream: normalized lifecycle and
stop events. `get_output`/`wait_for_output` carry the **text** stream. Nothing the target printed
appears in the event stream, and no debugger event appears in the text stream. `get_stop_reason` is not
a third stream; it reads one retained `stopped` event out of the structured one.

The text stream interleaves two sources, told apart by each message's `category`:

| Category | Source |
|---|---|
| `StandardOutput`, `StandardError` | The debugged program's own console streams, reassembled into whole lines |
| `Output`, `ErrorUser`, `StepFilter` | Host commentary from dnSpy and dgSpy, including the `dgSpy audit <id>:` line every side-effecting call writes |

Program output only exists for a target dgSpy **launched**, and only while `redirect_output` is on
(the default). The engine then creates the process with its stdout/stderr on pipes it owns. An attached
process's console handles were never dgSpy's, so nothing can be captured from one after the fact, and
for those sessions `get_output` carries host commentary alone. That is a property of process creation
on Windows, not a gap in the tool: retro-fitting handles onto a running process is not possible.

### An interrupted `launch` is not a failed one

`launch` creates the process and then waits for the engine to bring it up, so a client cancellation
lands *after* the side effect: the process exists, is attached, and may be parked at its entry point,
while the caller sees only "interrupted". Two things make that recoverable:

- The session records its `program_id` (`launch:<engine>:<path>`) as soon as the process exists, not
  once the call returns, so `list_sessions` shows an interrupted launch.
- A repeat `launch` of the same image adopts that live session and returns it rather than starting a
  second debuggee. Pass `adopt_existing=false` to run a second copy on purpose. A faulted or exited
  session is never adopted, because the caller asked for a running program.

`launch` does not wait for the `break_at` stop before replying — it returns once the engine has the
process and its threads. Wait on the event stream for the stop.

Lines, not chunks: the engines deliver these streams as raw pipe reads, so one read can carry three
lines or half of one. dgSpy reassembles them, and flushes a still-incomplete line after a short quiet
period and again when the process exits, so a program that writes a prompt without a newline is
delayed rather than withheld.
- Primitive locals are limited to values with a raw scalar; object expansion is outside milestone 1.
- **`update_breakpoint` distinguishes "clear" from "leave alone".** An omitted field keeps its current
  value; an empty string for `condition` or `trace_message` removes it. Without that distinction the
  only way to drop a condition would be to delete and recreate the breakpoint, which changes its id.
- **A tracepoint with `trace_continue` never stops**, so it produces no `stopped` event and
  `wait_for_stop` on it waits until its timeout. `update_breakpoint` returns a `warning` saying so when
  it sets one. Pass `trace_continue=false` to print *and* stop.
- Conditions and hit counts are evaluated by the engine inside the target, so an expression that cannot
  be evaluated fails at hit time — dnSpy then stops anyway — rather than being rejected when it is set.
- **`list_exception_breakpoints` reports first-chance entries by default.** dnSpy stops on *second*
  chance for essentially every .NET exception it ships, so the unfiltered list is ~2500 stock entries
  that are identical on every machine and say nothing about what this session configured. Pass
  `include_second_chance` to see them; the listing is bounded and reports `total` and `truncated`.
- **A step completes on the event stream, not in its own reply.** `step_into`/`step_over`/`step_out`
  return `cursor_event_id`; wait from it with `wait_for_stop` and the stop arrives with
  `stop_reason: "step"`. `completed: false` means still running, not failed. The cursor matters for the
  same reason it does for breakpoints: a step over a fast call lands before a follow-up state read
  returns. The result's `status` makes the outcome explicit — `completed`, `step_error` (see `error`),
  or `in_flight` with a `hint`: the target is running again, and a step across interop, optimized, or
  interpreted code may never land, in which case pause or set a breakpoint instead of waiting.
- **A breakpoint outranks a step.** Stepping out of a method that still has an active breakpoint in
  it — a loop body, say — hits that breakpoint first: the step reports `completed: false` and the
  next stop is the breakpoint, in the same method you were trying to leave, not the caller. This is
  correct debugger behaviour, not a failed step. Remove or disable the breakpoint first when the
  point of the step is to reach the frame above.
- `remove_exception_policy` returns `removed: true` with the entry's former flags under
  `former_policy`; the flags are what the policy *was*, not a still-active setting.
- **Exception breakpoints and exception policies are one thing.** `set_exception_breakpoint` and
  `set_exception_policy` write the same dnSpy entry (the policy form additionally takes module
  conditions); `list_exception_breakpoints` is the filtered deliberately-configured view of the same
  entries `list_exception_policies` reports raw, and `remove_exception_policy` removes entries created
  by either setter. Both list tools take the same `category` and `name` filters, matched exactly, and
  report `total` and `truncated`. That is what makes a removal confirmable: without a way to name one
  entry, the only view was dnSpy's whole stock definition set, so a caller doing careful cleanup could
  not prove it had cleaned up. The empty string selects the category default entry, the one
  `set_exception_breakpoint` writes when `name` is omitted.
- **Zero processes and several are opposite problems and no longer share one message.** Selecting a
  process without `process_id` answers `no_active_process` when the session has none (the target exited
  or was detached) and `ambiguous_target`, naming the live PIDs, when it has more than one. The single
  old "More than one process is active; pass process_id" sent a caller hunting for a second process
  when the real answer was that there was not even a first one.
- **`search` is the discovery entry point.** It is dnSpy's Search window as a tool: it tests the same
  candidate strings the GUI does, so a qualified path resolves --- `GameState.ChatSystem` finds the
  `ChatSystem` field on type `GameState`, which `search_symbols` cannot, because that tool compares the
  simple name only and so cannot find the very `full_name` it prints. `kinds` covers every entry of the
  GUI's *Search For* dropdown, including `property`, `event`, `parameter`, `local` and `literal`;
  `search_symbols` covers three of the twenty-four. Space-separated terms are AND-ed, `/slashes/` make a
  regular expression, and `kinds: ["literal"]` searches constant values and `ldc`/`ldstr` operands
  directly out of IL rather than decompiling, which is what makes it far cheaper than `search_text`.
  The port and the reasons for not driving dnSpy's own `IDocumentSearcher` are in
  [SEARCH_PROPOSAL.md](SEARCH_PROPOSAL.md).
- **`search` results round-trip; nothing needs parsing.** `full_name` is one of the strings the matcher
  itself tests, so it is accepted back as `pattern`. `declaring_type` is accepted by `list_members` and
  `get_csharp`. `module` plus `token` is accepted by `get_il`, `find_references`, `analyze_symbol` and
  `set_il_breakpoint`. This is the rule commit `3fcbacd73` established for `get_members`, applied to a
  second surface: a tool must never emit an identifier it would then reject.
- **`search` is the only read-only tool whose scope can leave the debug session.** `scope: "session"`
  (the default) searches the session's loaded modules, the same set as every neighbouring tool.
  `scope: "documents"` searches dnSpy's Assembly Explorer and needs no attached process, which answers
  the case where an assembly is open in dnSpy but not loaded in the target and every other tool reports
  `module_not_found`. `scope: "all"` searches both. Every hit carries `in_session`, so a caller knows
  before it tries whether the session-scoped tools will accept that module.
- **Every bounded scan is resumable.** `search.max_scan`, `search_text.max_methods` and
  `analyze_symbol.max_scan` bound inspected symbols rather than the clock, traversal order is
  deterministic, and `next_scan_offset` fed back as `scan_offset` continues exactly where the previous
  call stopped. **`scan_truncated: false` is the only thing that means a sweep is complete.** A bound
  without a cursor does not return a partial answer, it returns an unreachable region, and both failures
  were measured: an agent swept a plugin for hotkey definitions with `search_text`, hit `max_methods`,
  reported the sweep complete, and the user's own UI then showed three keybinds it had never reached;
  `analyze_symbol` could not find the callers of a method in a 7227-method module at *any* setting,
  because its bound capped at 5000. The bounds themselves are unchanged and deliberately still small ---
  `search_text` decompiles every method it looks at. A cursor is what a caller needed, not a bigger cap.
  A cursor is only valid for a repeat call carrying the same filter arguments, since those are what fix
  the traversal.
- **One module-name rule, for the whole family.** Every `module` and `search_module` argument accepts a
  module name, a filename, or a full path, case-insensitively and with the extension optional, so
  `Assembly-CSharp` reaches `Assembly-CSharp.dll`. A substring matches too, but an exact name always beats
  one, so a stem is never reported ambiguous against a longer neighbour like `Assembly-CSharp-firstpass.dll`.
  This used to be two rules: `get_csharp`, `list_types` and `list_members` compared for equality while
  `search`, `search_symbols` and `search_text` took a substring, and nothing in either schema said which,
  so `get_csharp(module: "Assembly-CSharp")` answered `module_not_found` for a module `search` was happily
  searching. Equality missed because both `name` and `filename` carry the `.dll`. Case was never the
  defect --- every arm was already case-insensitive --- and the rule lives in one place,
  `Extensions/dgSpy.Extension/Decompiler/ModuleNameMatch.cs`, rather than inline in two.
- **`module_not_found` names the near misses.** It used to say "use `list_modules`", which against a Unity
  player meant 170 modules and 60,131 characters: an error telling a caller to go and blow its own
  context. It now lists the closest loaded module names, including ones that differ only in separators
  (`AssemblyCSharp` suggests `Assembly-CSharp.dll`). Ambiguity is still reported with its candidates
  rather than resolved by guessing.
- **`list_modules` and `list_documents` filter and page.** Both take `name_pattern`, `offset` and `count`
  (default 100, max 500) and report `total` and `truncated`, like every sibling in the family. `name_pattern`
  follows the same module-name rule as `module`. Both answer with an object --- `{ modules | documents,
  total, offset, truncated }` --- not a bare array. On `list_documents` the filter saves real work, not
  just output: every row it returns loads that module's metadata.
- **`search_symbols` remains, narrower.** It returns module plus metadata token, which is exactly what
  `set_il_breakpoint` takes, so an agent never has to parse display text into an identity.
  `set_breakpoint` does the same resolution server-side and then follows the identical
  path, so binding state and Mono snapping cannot diverge between the two tools.
- **A member path is not a type name, and the error says which.** `list_members(type: "GameState.ChatSystem")`
  reports that `ChatSystem` is a *field* on type `GameState` of type `ChatDisplay`, and names the type to
  ask for instead. The previous flat "No type ... Use list_types" once led an agent to report that a
  member which plainly exists did not.
- **`get_il` marks the offsets Mono will accept.** `is_sequence_point` per instruction answers the
  question that previously took trial and error. `has_sequence_points: false` means no PDB was
  available — *not* that there are no legal offsets.
- **Ambiguity is reported, never resolved by guessing.** An ambiguous type name or an overloaded method
  returns the candidates. A breakpoint silently placed in the wrong overload is undetectable from the
  caller's side.
- **Metadata-backed in-memory and dynamic modules support breakpoints.**
  `get_csharp`, `get_il`, `list_types`, `list_members`, `get_metadata` and `get_raw_module` resolve them
  through dnSpy's metadata service. `get_raw_module` returns paged base64 with a whole-image SHA-256;
  for a file-less module the image is reconstructed from runtime metadata.
  Both breakpoint tools use the active engine's full `ModuleId`, including the discriminator required
  for file-less modules. `list_modules.can_set_breakpoint` is false only when no engine provider
  publishes a stable identity (for example Mono `eval-*` scratch modules with no metadata). A file-less
  module may still report a bare assembly name as its filename; that display value is not its identity.
- **Analysis results remain debugger-addressable.** `search_text` returns the containing method's module
  and token, `find_references` returns methods whose IL names the target member, and
  `find_implementations` returns loaded direct subclasses or interface implementers. All are bounded;
  module filters avoid scanning every Unity framework assembly when the caller already knows the scope.
  `search_text` also caps the number of methods it decompiles (`max_methods`, default 200) and reports
  `scanned_methods` / `scan_truncated` / `next_scan_offset`; `find_implementations.search_module` provides
  the equivalent Unity-safe scope. These are work bounds, not merely output caps. Reach for `search_text` last: it
  decompiles, so it costs orders of magnitude more than `search`, and for string or number constants
  `search` with `kinds: ["literal"]` answers the same question out of IL.
- **`value` and `display` are separate on purpose.** `value` is the raw scalar, `display` is dnSpy's
  formatted text. An agent comparing numbers wants the first; one showing something wants the second.
  Collapsing them would force every caller to parse display text back into a value.
- **`has_raw_value` separates `null` from "unavailable".** A null reference has `value` absent and
  `has_raw_value: true`; an optimized-away or out-of-scope local has `has_raw_value: false` and an
  `error` saying which. Conflating them reports a bug that is not there.
- **Func-eval is off by default on every evaluating tool.** `allow_func_eval` runs target code —
  property getters, `ToString` — which can deadlock a target holding a lock and can mutate the state
  being inspected. `set_value` is always side-effecting; its `compiler_error` flag tells you whether
  anything actually ran.
- **`get_members` expands one level and never recurses.** A member carries the `expression` that reaches
  it, so the caller spends and cancels its own depth. Server-side recursion is unbounded on a cyclic
  object graph. Paged with `offset`/`count`, capped at 200 and clamped to the remaining dnSpy child
  count, reporting `total` and `truncated`. Children whose property getter is blocked by the default
  no-func-eval policy still report their member name alongside the evaluation error.
- **Watches are stored expressions, not value handles.** A handle goes stale on the next resume; an
  expression is re-evaluated against whatever frame you name. A watch whose expression fails reports its
  own error instead of failing the whole call.
- **`get_frame`'s `include` covers arguments under `locals`** — dnSpy's provider does not separate
  arguments from locals, so there is no separate `arguments` value pretending it does.
- **Stepping never guesses a thread.** With no `thread_id` it steps the thread that carried the stop,
  and refuses outright if none is current — stepping the wrong thread resumes the target and stops
  somewhere unrelated, which is worse than an error.
- The gateway opens a new loopback TCP connection per request, so restarting dnSpy needs no gateway
  restart; calls fail while dnSpy is down and succeed again once the extension is listening.
- The debugger dispatcher contains and records exceptions from individual asynchronous callbacks so
  one bad cleanup/event callback cannot terminate the debugger thread and strand a paused target behind
  a modal dialog. `get_host_info` reports dispatcher fault count/detail plus evaluation queue state,
  active time, and pending work. A dispatcher fault or evaluation active beyond 130 seconds marks the
  host `degraded`; `doctor` then reports `healthy: false` and directs recovery to those fields.
- Per-tool gateway deadlines are derived from the bounds the extension advertises in
  `get_capabilities` (`dgSpy.Protocol.CapabilityCatalog`), plus a margin, rather than guessed. A
  gateway deadline shorter than the inner bound abandons work that was about to succeed; a unit test
  keeps the two sides from drifting. An expired or canceled routed call reports `deadline_exceeded`,
  not `internal_error` or `host_unavailable`; narrow symbol/module/result filters before retrying.

## Tests

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj
```

```powershell
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj
```

```powershell
dotnet test .\tests\dgSpy.Extension.Tests\dgSpy.Extension.Tests.csproj
```

```powershell
.\tests\run-milestone1-smoke.ps1
```

```powershell
.\tests\run-launch-output-smoke.ps1
```

`run-launch-output-smoke.ps1` is the launch-side smoke: it drives a console target through `launch`,
asserts that every line the program printed reaches `get_output` under a `StandardOutput` category with
the target's process id, and compares that against the log the target keeps itself. It then repeats the
`launch` call and asserts that the session is adopted rather than a second debuggee created. It needs a
target that mirrors its own stdout to a log; `-TargetExe` points it at one.

The unit tests cover the wire contract and capability catalog, the gateway's access control and its
Streamable HTTP version policy and deadline-versus-bound invariant, and the extension's pure
program-identity, session-state, stale-frame, and bounded
event-cursor invariants. The smoke script is the end-to-end test: it builds, deploys, starts a
disposable targets plus dnSpy plus the gateway, and currently asserts 365 checks across the delivered
CorDebug surface. It stops everything it starts and exits non-zero on any failure.

The smoke script covers `attach_endpoint`'s argument validation and failure path only; its success
path needs a Mono/Unity target; the completed manual acceptance record is in the
[historical Unity checklist](history/DGSPY_UNITY_CHECKLIST.md).
