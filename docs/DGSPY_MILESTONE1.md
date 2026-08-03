# dgSpy milestone 1

Three components:

- `dgSpy.Protocol`: versioned JSON DTOs used over loopback TCP.
- `Extensions/dgSpy.Extension`: the MEF extension and authoritative debugger-state owner.
- `dgSpy.Gateway`: a loopback-only Streamable HTTP MCP endpoint.

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

`list_programs`, `attach`, `detach`, `list_sessions`, `get_session_state`, `pause`, `continue`,
`set_il_breakpoint`, `wait_for_stop`, `get_callstack`.

## State and lifetime rules

- `list_programs` unfiltered probes every process and takes seconds. Pass `process_ids` or
  `process_names` (wildcards allowed) when the target is known — that is ~50 ms instead of ~2500 ms.
  Each call replaces the set of valid `program_id` values.
- `program_id` is composed from PID, runtime GUID, and the engine's discriminator (CLR version for
  CorDebug). `runtime_guid` is what separates .NET Framework from Unity/Mono; they share a kind GUID.
- `attach` waits until the engine has enumerated threads before returning, so a following `pause` has
  a call stack. It refuses to start a second session while one is live.
- `pause` and `continue` return the state they produced, not the state before the transition.
- **`detach` is the only safe way to end a session.** Closing dnSpy with a session attached terminates
  the target. `detach` refuses with `detach_would_terminate` when dnSpy cannot detach cleanly, unless
  `allow_terminate=true`.
- `list_sessions` recovers a lost `session_id`.
- Mutations may include `expected_state_version`; a mismatch returns `stale_state`.
- Frame identity is `module` + `method_token` + `il_offset`, which is exactly what `set_il_breakpoint`
  takes. `name` is formatted for display and must not be parsed.
- `wait_for_stop` is cursor-based and non-destructive. The extension retains 256 events and reports
  the oldest available cursor. The extension caps the wait at 10 s.
- Primitive locals are limited to values with a raw scalar; object expansion is outside milestone 1.
- The gateway opens a new loopback TCP connection per request, so restarting dnSpy needs no gateway
  restart.

## Tests

```powershell
dotnet test .\tests\dgSpy.Protocol.Tests\dgSpy.Protocol.Tests.csproj
```

```powershell
dotnet test .\tests\dgSpy.Gateway.Tests\dgSpy.Gateway.Tests.csproj
```

```powershell
.\tests\run-milestone1-smoke.ps1
```

The unit tests cover the wire contract and the gateway's access control. The smoke script is the
end-to-end test: it builds, deploys, starts a disposable target plus dnSpy plus the gateway, and
asserts 37 checks across access control, discovery, attach, inspection, breakpoints, and detach. It
stops everything it starts and exits non-zero on any failure.
