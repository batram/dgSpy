# dgSpy implementation plan

This roadmap contains open work only. dgSpy now tracks dnSpyEx, and the former modernization work
through section 2.6 is complete. The implementation and its evidence are preserved in
[history](history/README.md); current behavior is defined by the [architecture](ARCHITECTURE.md),
[build baseline](DGSPY_BASELINE.md), and [tool reference](DGSPY_REFERENCE.md).

## Current baseline

The delivered product is a local x64 debugger bridge for .NET Framework CorDebug and Mono/Unity. It
provides lifecycle control, events, breakpoints, inspection, evaluation, code and metadata navigation,
analysis, export, memory access, and supported low-level debugger operations.

The host follows the bounded dnSpyEx 6.6 baseline and targets `net48` and `net10.0-windows`. dgSpy keeps
its headless activation patch, bounded `Mono.Debugger.Soft` frame retrieval, proven Mono/shared-debugger
orchestration, and engine-specific running-state behavior.

## Ongoing maintenance

dnSpyEx synchronization is routine repository maintenance, not a product milestone. Follow
[dnSpyEx synchronization](DNSPYEX_SYNC.md) when updating the baseline: inventory conflicts against the
retained patch set, keep unrelated MCP behavior out of synchronization commits, run the complete
regression gate, and record adopted, adapted, rejected, and still-local patches.

Upstream generally useful fixes from a branch based on `upstream/master`, never from `dgspy`. Add a
numbered roadmap item only when a specific upstream release or incompatibility creates a bounded change
set with concrete acceptance criteria.

Every accepted synchronization must still satisfy these maintenance gates:

- Both supported host targets and the dgSpy extension build and deploy.
- Protocol, Gateway, and Extension suites pass sequentially.
- Tool-schema and capability snapshots show no unexplained contract drift.
- CorDebug and bounded Unity gates pass, including safe detach and target liveness.
- The build baseline and retained-patch notes match the merged source.

## 1. Add secure remote hosts and ownership

This is the next product expansion. Do not expose the current extension RPC directly to a VM or LAN.

Progress:

- **Complete:** stable extension `host_id`, authenticated local gateway-to-extension RPC, and
  Gateway-local host selection. Requests fail closed on missing or invalid credentials,
  ambiguous/unknown hosts, and mismatched identity.
- **Complete:** self-contained x64 remote-host packing with bundle-local launcher state and a deterministic
  SHA-256 manifest.
- **Complete:** extension-initiated Gateway registration using centrally provisioned deploy ZIPs,
  including local packaged-host, Windows Server 2019, and clean Windows 11 debugger/Gateway-restart acceptance.
- **Complete:** optional pinned self-signed mutual TLS on that connection, with plaintext and TLS selected
  per deployment. Windows Server 2019 live acceptance covered routed launch, routed attach to an x64
  .NET Framework worker, and session/cursor preservation across a central Gateway restart.
- **Complete:** the trusted-local-agent control plane has MCP-session controller identity,
  one controller per debugger session, optimistic mutation guards, explicit orphan reclaim, invariant
  disconnect behavior, a coarse inspect-only mode, and bounded redacted local audit records.
- **Deferred until the trust boundary changes:** per-user/per-target ACLs and encrypted
  client-to-Gateway transport. MCP remains authenticated and loopback-only.

1. **Complete.** Add minimal outbound remote registration. The central machine produces a per-host ZIP containing a
   stable `host_id`, strong shared credential, Gateway endpoint, self-contained dnSpy host, extension,
   launcher, and deterministic manifest. After extract-and-run, the extension opens one persistent
   outbound connection, authenticates and registers its expected identity, receives routed RPC requests
   on that connection, reconnects with bounded backoff, and never exposes an inbound remote debugger
   listener. The Gateway keeps MCP on loopback, rejects unknown/duplicate/mismatched hosts, and reports
   connection state through `list_hosts`. Prove one and multiple packaged hosts, disconnect/reconnect
   with preserved dnSpy session state and event cursors, and removal by deleting the extracted package.
2. **Complete.** Add minimal mutual TLS to the outbound host connection. Generate one self-signed Gateway server
   certificate and one unique self-signed client certificate per package on the central machine. Load
   certificate files directly without modifying OS trust stores; the remote pins the exact Gateway
   certificate and the Gateway pins each client certificate to its configured `host_id`. Require TLS 1.2
   or later, reject every unpinned certificate or identity mismatch, and prove certificate replacement
   and revocation by explicit pin updates. Keep MCP authentication and transport independent.
3. **Complete.** Produce a self-contained x64 remote debugger-host bundle built on the central development
   machine: dnSpyEx, a compatible dgSpy extension target, protocol/runtime dependencies, and a
   process-scoped launcher. A remote Windows host must not need Git, an SDK, Visual Studio/MSBuild, a
   separately installed .NET runtime, a VC++ redistributable installer, or a local Gateway. Include any
   required managed and native runtime files in the bundle and publish a deterministic manifest with hashes.
4. **Complete.** File-deployed bundles on Windows Server 2019 proved manifest-valid extraction, MEF
   composition, authenticated local port 7351 startup, identity persistence, and both plaintext and
   pinned mutual-TLS outbound registration. Live remote acceptance routed launch and attach operations,
   pause/inspect/resume/safe-detach against an x64 .NET Framework `w3wp.exe`, breakpoint lifecycle,
   metadata/IL/C# inspection, raw-module and memory reads, and central Gateway restart with the same
   session and non-regressing event cursor. The worker remained alive and attachable after detach.
   A user-designated clean Windows 11 x64 VM then ran a newly provisioned TLS package after file
   deployment alone: it registered without a local Gateway or setup step, launched and inspected a
   CorDebug target, recovered the same running session with cursor 11 advancing to 44 after a central
   Gateway restart, and terminated only the disposable target. After closing dnSpy and deleting the
   extracted package, seven observations across 35 seconds stayed `host_unavailable` with no reconnect;
   the process-scoped launcher had installed no machine-wide configuration.
5. **Complete.** MCP Streamable HTTP negotiates supported protocol revisions, accepts notifications,
   rejects unsupported revisions, and explicitly returns 405 because dgSpy has no server SSE stream.
6. **Complete.** For the authenticated loopback deployment, assign each initialized MCP session a controller identity.
   `attach` and `launch` claim one debugger session; inspection remains shared, while session mutations
   require the controller and an exact `expected_state_version`. Expiry releases only controller
   ownership and never changes the target. Gateway restart leaves recovered debugger sessions unowned;
   `claim_session` explicitly recovers them. `release_session` relinquishes control without detach.
7. **Complete for the delivered trust model.** Provide a coarse `full-control` (default) or `inspect-only` Gateway profile. Defer per-user and
   per-target ACLs until MCP leaves loopback or differently trusted callers share the Gateway; require
   that model before remotely enabling artifact editing, live patching, or host scripting.
8. **Complete.** Codify and test the invariant disconnect policy: MCP, Gateway, or remote-host disconnect preserves
   the target's exact running/paused state. No timeout, ownership expiry, or reconnect may implicitly
   resume, detach, terminate, or restart a target.
9. **Complete.** Write bounded JSONL audit records for side-effecting requests with timestamp, audit ID, controller,
   host/session identifiers, operation, state-version guard, outcome, and error code. Never record
   credentials, expressions, evaluated values, memory, exported bytes, or artifact contents.

Exit criteria:

- An authenticated remote client can select a host and debug a target through the supported path.
- A supported remote Windows host can run the published debugger bundle after file deployment alone,
  without installing development tools, frameworks, runtimes, redistributables, or the Gateway.
- The remote host opens no network-reachable debugger listener; only its authenticated outbound Gateway
  connection carries remote debugger traffic. The existing loopback listener may remain for local use.
- Gateway and host mutually authenticate with exact certificate pins without OS trust-store setup.
- Reconnection preserves session state and event cursors when dnSpy survives.
- Authorization tests prove an inspection-only client cannot control, mutate, terminate, export,
  execute, edit, patch, or script.
- Ownership tests cover contention, disconnect, expiry, and recovery without implicit target control.

Trusted-local control-plane acceptance used two initialized MCP sessions against the TLS Windows Server
host. The non-controller received `session_owned`; missing and stale guards received
`state_version_required` and `stale_state`; refreshed pause/continue succeeded; release and explicit
claim transferred control. After central Gateway restart the same running debugger session returned
unowned, mutation failed `session_unowned`, explicit claim recovered it, and only the disposable target
was terminated. Packaged plaintext and TLS restart smokes passed the same unowned/claim/version path.
The HTTP smoke proved session-header issuance, inspect-only denial, ownership-tool discovery, and audit
redaction. Per-user/per-target ACLs and MCP-side TLS are not Section 1 exit criteria while MCP remains
authenticated, loopback-only, and shared by equally trusted local callers.

## 2. Optional high-risk capabilities

Target-code execution, artifact editing/project export/live patching, and dnSpy-host scripting remain
unscheduled. Their trust boundaries, prerequisites, and exit criteria are consolidated in
[future capabilities](FUTURE_CAPABILITIES.md).

Do not implement one merely to make the tool surface broader. Start only for a concrete workflow, after
the remote permission and audit model can represent its risk independently.

## 3. Optional compatibility and quality gaps

Take these as independent, fixture-led projects rather than one compatibility phase:

- **CoreCLR:** define engine capabilities, add deterministic targets, and prove lifecycle, inspection,
  evaluation, breakpoint, and detach behavior.
- **x86:** add an x86 build/deploy path and engine-specific regression fixtures.
- **Visual Basic parity:** first define the intended agent-facing surface; do not retain VB solely because
  legacy decompiler internals reference it.
- **Deterministic Unity fixture:** replace manual or game-dependent checks where practical, while keeping
  a real Unity smoke for protocol behavior.
- **Headless failure handling:** prevent or programmatically close dnSpy's modal Mono connection-failure
  dialog without weakening useful error reporting.
- **Multi-session isolation:** move beyond multiple targets in the default debugger manager only after
  host and session ownership semantics are implemented.

Each item needs explicit capability changes, engine-specific fixtures, bounded live checks where
applicable, and documentation updates. None should be bundled into routine dnSpyEx synchronization.

## Documentation rule

- Keep current contracts and invariants in `ARCHITECTURE.md`, `DGSPY_BASELINE.md`, and
  `DGSPY_REFERENCE.md`.
- Keep open work here; do not append completed phase narratives.
- Move dated investigations, acceptance runs, and superseded plans to `docs/history/`.
- Update `DNSPYEX_SYNC.md` and the retained-patch notes whenever the upstream relationship changes.

## Product definition of done

The broader product is complete when an authorized remote agent can securely select a host and session;
discover, attach or launch; control and wait; inspect, evaluate, and mutate within policy; navigate code
and metadata; use capability-gated low-level operations; disambiguate multiple targets; recover from
disconnects and stale state; and communicate through an authenticated, encrypted, auditable transport.
