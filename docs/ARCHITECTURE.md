# dgSpy architecture

## Purpose

dgSpy exposes dnSpy's debugger, decompiler, metadata, and search capabilities to AI agents through MCP,
without automating the WPF UI. The extension owns debugger state; the gateway translates MCP requests,
enforces the local HTTP boundary, and is the future home of remote-host routing and authorization.

```text
AI agent / MCP client
        |
        | Streamable HTTP MCP
        v
dgSpy MCP Gateway
        |
        | versioned loopback RPC
        v
dgSpy dnSpy Extension
        |
        | dnSpy public debugger/decompiler APIs
        v
.NET Framework / Mono / Unity targets
```

## Current supported boundary

- x64 dnSpy and x64 targets only.
- .NET Framework CorDebug (`CLR v4.0.30319`, covering .NET Framework 4.0–4.8).
- Mono/Unity endpoint attach as exercised against Ultimate Chicken Horse.
- One local gateway, one dnSpy instance, and one logical dgSpy session. dnSpy may own multiple selected
  processes/runtimes within that session.
- C# is the agent-facing source language. CoreCLR, x86, and Visual Basic parity are not current gates.

## Components and ownership

- `dgSpy.Protocol` contains DTOs and the shared capability catalog. It targets `netstandard2.0` and has no
  dnSpy or WPF dependency.
- `dgSpy.Gateway` exposes the MCP tools, validates `Origin` and `X-dgSpy-Token`, derives deadlines from the
  shared capability catalog, and talks to the extension over loopback TCP.
- `Extensions/dgSpy.Extension` is a net48 MEF extension deployed as `dgSpy.Extension.x.dll`. It owns dnSpy
  services, session state, handles, events, evaluation queues, and transport lifetime.
- The extension binds RPC to `127.0.0.1` and authenticates every request with a generated or explicitly
  configured shared credential. The gateway verifies the extension's stable `host_id` during handshake.
  Multi-host routing and remote encrypted transport remain roadmap work.

Keep tool families in focused `RpcHost.<Family>.cs` partials under `Debugger/`, `Decompiler/`,
`Evaluation/`, `Events/`, `Handles/`, or `Identity/`. Keep shared dnSpy objects and shutdown ownership in
`Rpc/RpcHost.cs`. Pure policy belongs outside WPF/dnSpy implementation dependencies so the net7 test
project can exercise it.

## State and identity model

A host identity represents one reachable extension endpoint. The delivered implementation persists one
stable local identity; the gateway does not yet register or route multiple hosts. A session represents one
logical attachment or launch and reports `attaching`,
`running`, `paused`, `mixed`, `detaching`, `exited`, or `faulted`, plus a monotonic `state_version` and
event cursor.

Programs, processes, runtimes, modules, threads, frames, values, symbols, breakpoints, and object IDs use
typed or opaque identities. Display strings and dnSpy `RuntimeId.ToString()` are never durable identity.
Frame/value snapshots are invalid after resume unless an engine-backed object ID explicitly provides
persistence. Mutations may carry `expected_state_version`; ambiguity must fail without acting.

## Concurrency and lifetime invariants

- Marshal dnSpy-owned state through `DbgManager.Dispatcher`; never perform socket I/O, response
  serialization, decompilation, or target evaluation on that dispatcher.
- Serialize metadata/decompiler work on the evaluation queue because dnlib and dnSpy module caches are
  lazy and not safe for concurrent first access.
- Events and debugger output are separate bounded, non-destructive cursor streams. Concurrent waiters see
  the same event; stale cursors report truncation and the oldest available cursor.
- A deadline can abandon the caller's wait, but dnSpy cannot cancel an arbitrary queued dispatcher action.
  Func-eval uses the engine's harder timeout where available. This limitation is advertised.
- Breakpoints are dnSpy-global and survive detach. Closing dnSpy while attached may terminate the target;
  `detach` is the safe exit and `terminate` is always explicit.

## Security and trust boundary

Debugger access is equivalent to local process control and can become arbitrary target-code execution.
The delivered local boundary therefore requires loopback listeners, gateway `Origin` validation, and a
local shared secret. It does not claim that loopback alone is authentication or that function evaluation
is sandboxed.

Before remote or multi-client use, add multi-host routing, encrypted transport, per-client/per-target
permissions, request and response limits, side-effect audit records,
and defined disconnect behavior. Keep inspect, execution control, mutation, termination, host export,
target-code execution, artifact editing, live patching, and dnSpy-host scripting as separate permissions.

## Verification rule

Claims are engine-specific. “Implemented” means the code and contract exist; “verified” means exercised
against a real dnSpy and target. Preserve separate CorDebug and Mono/Unity evidence with the relevant
test fixtures. The completed 2026-08-04 acceptance record is available in
[history](history/README.md).
