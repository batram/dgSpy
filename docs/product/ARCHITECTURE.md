# dgSpy architecture

## Purpose

dgSpy exposes dnSpy's debugger, decompiler, metadata, and search capabilities to AI agents through MCP,
without automating the WPF UI. The extension owns debugger state; the Gateway translates MCP requests,
enforces the local HTTP boundary, and accepts and routes provisioned outbound remote-host connections.

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
- CoreCLR CorDebug, with exact debugger-component identity resolution from adjacent files, a private
  verified cache, or installed x64 `Microsoft.NETCore.App` runtimes.
- Mono/Unity endpoint attach as exercised against Ultimate Chicken Horse.
- One local gateway, one dnSpy instance, and one logical dgSpy session. dnSpy may own multiple selected
  processes/runtimes within that session.
- C# is the agent-facing source language. x86 and Visual Basic parity are not current gates. HookLab
  selects an explicit resident backend from the target's exact runtime identity: CLR v4 keeps the
  autonomous native initializer, while CoreCLR uses one run-all-threads debugger evaluation to load
  and commit the authenticated resident. CoreCLR hook source compiles through the hash-pinned Roslyn
  payload and patches through the matching CoreCLR Harmony backend.

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
  messages then travel over that reverse stream. See [remote hosts](../guides/REMOTE_HOSTS.md). Each deployment
  selects plaintext or pinned mutual TLS; the separate MCP client-to-Gateway encrypted transport remains roadmap work.

Every dispatched operation is also recorded into a bounded in-memory log that backs the **dgSpy MCP
Activity** tool window (`ToolWindows/`), so a human at the dnSpy window can see what an agent did
without reading the Gateway audit file. The log is a static singleton rather than a MEF export
deliberately: `RpcHost` is constructed directly by the extension entry point, and an unsatisfiable
MEF import there would delete the RPC host silently.

The Gateway also has a separate opt-in development transcript at the authenticated MCP `tools/call`
boundary. `DGSPY_TRANSCRIPT_FILE` enables it; unset means no transcript writer or default file. It
captures read-only discovery, Gateway rejection, and routed success/failure as one correlated redacted
request/response JSONL record. Authentication headers never enter the recorder. Request and response
payloads have independent bounds, the file rotates once, and write failure cannot fail the tool call.
This must remain separate from `GatewayAuditLog`: the default security audit intentionally records only
sparse operational facts for mutations, while a development transcript can retain sensitive debugger
material even after credential-field redaction.

Keep tool families in focused `RpcHost.<Family>.cs` partials under `Debugger/`, `Decompiler/`,
`Evaluation/`, `Events/`, `Handles/`, or `Identity/`. Keep shared dnSpy objects and shutdown ownership in
`Rpc/RpcHost.cs`. Pure policy belongs outside WPF/dnSpy implementation dependencies so the .NET 10 test
project can exercise it.

## State and identity model

A host identity represents one trusted extension instance. The delivered local registry maps each
identity to an authenticated endpoint. The delivered remote registry maps a provisioned identity to its
authenticated live outbound connection. Both reject ambiguous, unknown, duplicate, or mismatched
selection. A session represents one logical attachment or launch and reports `attaching`,
`running`, `paused`, `mixed`, `detaching`, `exited`, or `faulted`. `event_id` orders the event stream;
`lifecycle_version`, `execution_version`, and `breakpoints_version` are independent optimistic-concurrency
guards; and `stop_id` identifies one paused execution snapshot. The legacy all-event `state_version`
remains on the wire only for compatibility.

Programs, processes, runtimes, modules, threads, frames, values, symbols, breakpoints, and object IDs use
typed or opaque identities. Display strings and dnSpy `RuntimeId.ToString()` are never durable identity.
Frame/value snapshots are invalid after resume unless an engine-backed object ID explicitly provides
persistence. An initialized MCP session controls a debugger session created by its `attach` or `launch`.
Inspection is shared; every session mutation requires that controller plus its exact relevant scoped
revision. Frame-bound mutations also require the current `stop_id`. Thread and module notifications can
advance `event_id` without invalidating lifecycle or execution commands. A Gateway restart intentionally
recovers debugger sessions as unowned, and `claim_session` is the only recovery action. The recovery
contract deliberately remains an expiring controller lease plus inspected, warned `force=true` transfer:
there is no persisted or listable claim capability to steal, retain, revoke, or redact. Ownership changes
never change target state.

## HookLab runtime backends

`HookLabBackends` is the host's complete, concrete table of supported runtime backends - three rows,
`clrv4-x64`, `coreclr-x64`, and `mono-x64` - and one immutable `HookLabBackend` states everything that varies with
the runtime: family, architecture, how the payload arrives (native bootstrap or one debugger
evaluation), whether the runtime id is a constant or must be read from the live target, whether the
debugger is re-synchronised afterwards, and which payload matrix slots supply its patch engine and
compiler.

Selection is deterministic. A target that matches no row is refused by name; a process that has loaded
both runtimes - which really happens - resolves by declared priority rather than by the order of a
chain of conditionals, and CLR v4 wins. The .NET Framework runtime GUID alone is not an identity,
because CLR v2 shares it. Mono and the Mono embedded in Unity are one runtime backend with two debugger
runtime GUIDs; they share arrival and resident behavior rather than pretending the containing product is
a fourth runtime family.

The payload ids are the ids in the [resident payload matrix](HOOKLAB.md#resident-payload-matrix), so
the host's statement of what a runtime uses and the build's statement of what it ships for that runtime
are the same string, and a contract test requires them to agree. Adding a row is meant to require the
evidence a new row implies; it is a proved-support table, not a provider ecosystem.

Shared initialization orchestration reads the backend rather than re-deriving runtime facts. One place
still dispatches on the declared arrival mode, because the two mechanisms genuinely differ and both
need host services - but it is the only place that knows which is which.

## Standalone HookLab injection boundary

`HookLab.Injector` is the package-neutral standalone boundary for x64 desktop CLR v4 targets. It owns
validated hook definitions, exact live process identity, payload staging and lifetime, native bootstrap
loading, completion parsing, protected resident discovery, authenticated status, and desired-state
reconciliation. It has no dnSpy, debugger, Gateway, MCP, MEF, WPF, or decompiler dependency.

`HookLab.ApplyOnce` is a compatibility executable over that boundary. `HookLab.Watcher` owns package and
profile validation, process discovery, scheduling, audit, and command presentation; it does not own a
second initializer. Its explicit `apply --package ... --pid ...` and `status [--pid ...]` commands return
machine-readable JSON with stable exit categories. The dgSpy extension keeps its debugger-integrated
adapter, but both adapters use one protected discovery root and target identity. Each discovers,
authenticates, inventories, and adopts an existing resident before injection. The resident accepts
simultaneous authenticated controllers and keeps one shared hooks-version gate across them.

The adapters also consume one strict `ResidentInventoryParser` from `HookLab.Injector`. Probe-instance
identity, hook ownership, exact method guards, compiled-hook state, shadow detection, and hooks-version
therefore have one host-side wire interpretation. Session bookkeeping and UI source retention remain in
the debugger adapter; watcher scheduling and desired-state policy remain in the watcher. Arrival is still
separate: the standalone injector is CLR-v4 native injection, while the debugger adapter can also reach
CoreCLR and Mono through evaluation.

Resident hook IDs are controller-qualified (`dgspy:<id>` or `watcher:<id>`). Inventory reports that
owner and the exact case-sensitive CLR assembly simple name captured at installation. dgSpy exposes
foreign hooks as inspect-only; edit, enable, disable, removal, and `remove_all_hooks` operate only on
dgSpy-owned hooks. The watcher likewise reconciles only watcher-owned definitions. Unknown, legacy, and
foreign hooks survive adoption and owner-scoped cleanup; ownership transfer is not a supported operation.

`HookLab.Packaging` is the runtime-neutral writer for watcher deployment exports. The dgSpy extension
uses it to atomically freeze one retained compiled hook below `DGSPY_EXPORT_ROOT`; the watcher consumes
the result through its existing strict package loader. Every export includes a disabled profile, so
creating a package never opts a process into unattended injection. Before publication the extension
protects the closed export tree with the same narrow Windows principals required by watcher enrollment.
The installed watcher revalidates and atomically copies a canonical export into a separately protected
enrollment root, records its profile as disabled before publication, and validates the combined built-in
and enrolled catalog. Failed replacement restores both the previous tree and exact control state. Runtime
reload retains the last good complete catalog generation when a candidate generation is invalid.

The supported pipeline publishes the watcher as a closed, separately owned layout subtree. Its elevated
installer verifies the parent layout inventory, digests, owner/ACL, path containment, and reparse-point
absence before an atomic per-user install. A highest-available interactive logon task reads only the
installed packages, enrolled packages, and bootstrap payloads. Mutable control/status/audit state is
separate, and stdout is not an operational control channel. First install starts registered operation;
upgrade preserves exact verified running/stopped intent and accepts a started replacement only after
PID, creation identity, installed image, lifecycle, and catalog-health readback. Failed replacement
restores the prior tree, task, and operating state. Upgrade and uninstall stop only the exact installed
watcher image; they never terminate targets, remove resident patches, or delete live resident payload
leases.

`HookLab.Watcher.Companion` is an `asInvoker` notification-area process in that closed layout. It reads
the watcher's identity-validated status and bounded audit output, writes only the existing atomic control
contract, and asks Task Scheduler to start or restart the installed task. It does not host watcher logic,
self-elevate, stop the watcher on exit, or expose hook/package operations whose ownership contracts do not
exist.

Live discovery identity has a three-way operational boundary: an exact mismatch is quarantined, a
definitely exited identity is retired, and an identity that cannot be inspected is preserved while the
operation fails closed. Persisted watcher status similarly validates the exact PID plus creation time and
marks dead recorded active states as stale rather than presenting historical `running` as current.

The injector re-reads PID, creation time, and full image path immediately before acting. A profile-backed
watcher request also carries the image selected by the permitted-path filter, and the injector refuses a
different live image. Discovery credentials and resident status remain owned by
`HookLab.Host.Transport`; the shared root defaults to `%LOCALAPPDATA%\HookLab` and can be isolated with
`HOOKLAB_RESIDENT_STATE_ROOT`. Neither adapter invents a parallel credential format.

## Concurrency and lifetime invariants

- Marshal dnSpy-owned state through `DbgManager.Dispatcher`; never perform socket I/O, response
  serialization, decompilation, or target evaluation on that dispatcher.
- Serialize metadata/decompiler work on the evaluation queue because dnlib and dnSpy module caches are
  lazy and not safe for concurrent first access.
- Events and debugger output are separate bounded, non-destructive cursor streams. Concurrent waiters see
  the same event; stale cursors report truncation and the oldest available cursor.
- Local routing uses one authenticated connection per call. Outbound hosts multiplex request-ID-correlated
  calls over their one authenticated reverse connection and serialize only writes; a bounded wait cannot
  prevent an independent request from reaching the host. Responses may complete out of order. Cancellation
  abandons only its request, and connection loss fails every pending request explicitly.
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
[history](../history/README.md).
