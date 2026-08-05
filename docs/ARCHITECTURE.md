# dgSpy architecture

## Purpose

dgSpy exposes dnSpy's debugger, decompiler, metadata, and search capabilities to AI agents through MCP,
without automating the WPF UI. The extension owns debugger state; the Gateway translates MCP requests,
enforces the local HTTP boundary, and will accept and route provisioned outbound remote-host connections.

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
  shared capability catalog, owns local MCP controller identity/policy/auditing, and routes to extensions.
- `Extensions/dgSpy.Extension` is a net48 MEF extension deployed as `dgSpy.Extension.x.dll`. It owns dnSpy
  services, session state, handles, events, evaluation queues, and transport lifetime.
- The extension binds RPC to `127.0.0.1` and authenticates every request with a generated or explicitly
  configured shared credential. The gateway verifies the extension's stable `host_id` during handshake.
  This remains the delivered local transport. For remote hosts, a centrally provisioned extension opens
  one persistent outbound connection and registers its expected identity with the Gateway; the same RPC
  messages then travel over that reverse stream. See [remote hosts](REMOTE_HOSTS.md). Each deployment
  selects plaintext or pinned mutual TLS; the separate MCP client-to-Gateway encrypted transport remains roadmap work.

Keep tool families in focused `RpcHost.<Family>.cs` partials under `Debugger/`, `Decompiler/`,
`Evaluation/`, `Events/`, `Handles/`, or `Identity/`. Keep shared dnSpy objects and shutdown ownership in
`Rpc/RpcHost.cs`. Pure policy belongs outside WPF/dnSpy implementation dependencies so the net7 test
project can exercise it.

## State and identity model

A host identity represents one trusted extension instance. The delivered local registry maps each
identity to an authenticated endpoint. The planned remote registry maps a provisioned identity to its
authenticated live outbound connection. Both reject ambiguous, unknown, duplicate, or mismatched
selection. A session represents one logical attachment or launch and reports `attaching`,
`running`, `paused`, `mixed`, `detaching`, `exited`, or `faulted`, plus a monotonic `state_version` and
event cursor.

Programs, processes, runtimes, modules, threads, frames, values, symbols, breakpoints, and object IDs use
typed or opaque identities. Display strings and dnSpy `RuntimeId.ToString()` are never durable identity.
Frame/value snapshots are invalid after resume unless an engine-backed object ID explicitly provides
persistence. An initialized MCP session controls a debugger session created by its `attach` or `launch`.
Inspection is shared; every session mutation requires that controller plus the exact
`expected_state_version`. A Gateway restart intentionally recovers debugger sessions as unowned, and
`claim_session` is the only recovery action. Ownership changes never change target state.

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
- MCP, Gateway, controller-expiry, and remote-host disconnects preserve the target's exact state. They
  never imply resume, detach, terminate, or restart.

## Security and trust boundary

Debugger access is equivalent to local process control and can become arbitrary target-code execution.
The delivered local boundary therefore requires loopback listeners, gateway `Origin` validation, and a
local shared secret. It does not claim that loopback alone is authentication or that function evaluation
is sandboxed.

Remote host transport uses centrally provisioned packages and optionally uses pinned self-signed mutual TLS: the
remote pins the Gateway certificate, and the Gateway pins one client certificate to each `host_id`, with
no OS trust-store changes. The authenticated loopback MCP boundary defaults to `full-control` and may be
started `inspect-only`; side effects are recorded in a bounded redacted local JSONL audit. Per-user and
per-target ACLs plus encrypted client-to-Gateway transport remain deferred while MCP is loopback-only and
all local callers share one trust level. Require those controls before remotely enabling artifact editing,
live patching, or dnSpy-host scripting.

## Verification rule

Claims are engine-specific. “Implemented” means the code and contract exist; “verified” means exercised
against a real dnSpy and target. Preserve separate CorDebug and Mono/Unity evidence with the relevant
test fixtures. The completed 2026-08-04 acceptance record is available in
[history](history/README.md).
