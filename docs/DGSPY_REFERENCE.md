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
  `attach_endpoint`, `launch`, `list_sessions`, `get_session_state`, `pause`, `continue`, `detach`,
  `terminate`, `restart`.

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
- Code and metadata: `list_modules`, `list_documents`, `list_types`, `list_members`, `search_symbols`,
  `get_il`, `get_csharp`, `search_text`, `find_references`, `find_implementations`, `get_metadata`,
  `get_raw_module`, `analyze_symbol`.
- Explicit side effects and low-level access: `invoke_method`, `create_object`, `read_memory`,
  `write_memory`, `get_disassembly`, `get_registers`, `set_instruction_pointer`.

## State and lifetime rules

- `get_host_info` identifies the host: dnSpy/dgSpy versions, machine, architecture, supported engines,
  and the live `session_id` if there is one. `get_capabilities` reports per-operation time bounds,
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
  CorDebug). `runtime_guid` is what separates .NET Framework from Unity/Mono; they share a kind GUID.
- `attach` waits until the engine has enumerated threads before returning, avoiding a threadless stop.
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
- Mutations may include `expected_state_version`; a mismatch returns `stale_state`.
- `list_threads` requires a paused session and returns stable `thread_id` values as
  `process_id:os_thread_id`, including managed ID, name and state. It deliberately does not fetch every
  stack: Unity threads can exit during frame retrieval, and some Mono runtimes never answer that raced
  request. Pass an ID to `get_callstack` for bounded, deterministic selection; omit it to retain the
  best-effort managed-frame probe. `get_frame(thread_id, frame_index)` inspects exactly one frame.
- Frame identity is `thread_id` + `frame_index` for paused selection, and `module` + `method_token` +
  `il_offset` for code identity and `set_il_breakpoint`. `name` is display-only and must not be parsed.
- **On Mono/Unity, `il_offset` must be a sequence point** or the engine refuses the breakpoint. Many
  offsets qualify, but a frame's own `il_offset` often does not — so round-tripping it from
  `get_callstack` into `set_il_breakpoint`, which works on CorDebug, is refused on Mono. Check `bound`:
  `false` with `severity: "error"` will never be hit, `false` with no error is pending a module load.
  By default a refused offset is retried at method entry, which sets `snapped` and a `warning`; pass
  `snap_to_sequence_point=false` to get the failure instead. `bound` claims the engine installed the
  breakpoint, not that it will be reached.
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
  returns.
- **`search_symbols` is how you get from a name to a breakpoint.** It returns module plus metadata
  token, which is exactly what `set_il_breakpoint` takes, so an agent never has to parse display text
  into an identity. `set_breakpoint` does the same resolution server-side and then follows the identical
  path, so binding state and Mono snapping cannot diverge between the two tools.
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
  `scanned_methods` / `scan_truncated`; `find_implementations.search_module` provides the equivalent
  Unity-safe scope. These are work bounds, not merely output caps.
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
  object graph. Paged with `offset`/`count`, capped at 200, reporting `total` and `truncated`.
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
- Per-tool gateway deadlines are derived from the bounds the extension advertises in
  `get_capabilities` (`dgSpy.Protocol.CapabilityCatalog`), plus a margin, rather than guessed. A
  gateway deadline shorter than the inner bound abandons work that was about to succeed; a unit test
  keeps the two sides from drifting.

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

The unit tests cover the wire contract and capability catalog, the gateway's access control and its
Streamable HTTP version policy and deadline-versus-bound invariant, and the extension's pure
program-identity, session-state, stale-frame, and bounded
event-cursor invariants. The smoke script is the end-to-end test: it builds, deploys, starts a
disposable targets plus dnSpy plus the gateway, and currently asserts 365 checks across the delivered
CorDebug surface. It stops everything it starts and exits non-zero on any failure.

The smoke script covers `attach_endpoint`'s argument validation and failure path only; its success
path needs a Mono/Unity target; the completed manual acceptance record is in the
[historical Unity checklist](history/DGSPY_UNITY_CHECKLIST.md).
