# dgSpy milestone 1

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

## Authentication

Every `/mcp` request must carry `X-dgSpy-Token`. Set `DGSPY_TOKEN` to choose the value, otherwise the
gateway generates one at startup and writes it to `%LOCALAPPDATA%\dgSpy\gateway.token`.

Requests are also rejected when they arrive from a non-loopback address, or carry an `Origin` that is
not loopback. This is not optional hardening: a web page the user visits can POST to `127.0.0.1`
without a preflight, so loopback binding alone would leave the debugger open to any site.

## Tools

`get_host_info`, `get_capabilities`, `list_programs`, `attach`, `attach_endpoint`, `detach`,
`list_sessions`, `get_session_state`, `pause`, `continue`, `set_il_breakpoint`, `list_breakpoints`,
`remove_breakpoint`, `clear_breakpoints`, `wait_for_stop`, `list_threads`, `get_callstack`,
`get_frame`.

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
  [DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md).
- A session that comes up and then fails reports `state: "faulted"` with `fault_message` — dnSpy's own
  connect-failure text when it produced one. Options dnSpy rejects outright are a caller error and
  return `attach_failed` instead, without creating a session. A faulted session still holds the
  `session_id`; clear it with `detach`.
- `pause` and `continue` return the state they produced, not the state before the transition.
- **`detach` is the only safe way to end a session.** Closing dnSpy with a session attached terminates
  the target. `detach` refuses with `detach_would_terminate` when dnSpy cannot detach cleanly, unless
  `allow_terminate=true`.
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
- `wait_for_stop` is cursor-based and non-destructive. The extension retains 256 events and reports
  the oldest available cursor. The extension caps the wait at 10 s.
- Primitive locals are limited to values with a raw scalar; object expansion is outside milestone 1.
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
deadline-versus-bound invariant, and the extension's pure program-identity, session-state, and bounded
event-cursor invariants. The smoke script is the end-to-end test: it builds, deploys, starts a
disposable target plus dnSpy plus the gateway, and asserts 89 checks across access control, loopback-only
reachability, dnSpy-restart recovery, host info and capabilities, x64 discovery and provider filtering,
attach, thread/frame inspection, breakpoints, and detach. It stops everything it starts and exits
non-zero on any failure.

The smoke script covers `attach_endpoint`'s argument validation and failure path only; its success
path needs a Mono/Unity target and is a manual checklist, [DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md).
