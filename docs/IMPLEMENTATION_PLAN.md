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

- **Complete:** stable extension `host_id`, authenticated gateway-to-extension RPC, and central-Gateway
  registration/discovery/routing through loopback tunnel endpoints. Requests fail closed on missing or
  invalid credentials, ambiguous/unknown hosts, mismatched identity, and non-loopback registry addresses.
- **Open:** self-contained remote-host packaging, live SSH-tunnel acceptance, MCP client identity, leases,
  permissions, disconnect policy, audit records, client-to-Gateway HTTPS, and any direct encrypted
  listener.

1. Produce a self-contained x64 remote debugger-host bundle built on the central development machine:
   dnSpyEx, a compatible dgSpy extension target, protocol/runtime dependencies, and a process-scoped
   launcher. A remote Windows host must not need Git, an SDK, Visual Studio/MSBuild, a separately
   installed .NET runtime, a VC++ redistributable installer, or a local Gateway. Include any required
   managed and native runtime files in the bundle and publish a deterministic manifest with hashes.
2. Verify the bundle on a clean Windows Server host using copy/extract plus the launcher only. Prove MEF
   composition, authenticated port 7351 startup, host identity persistence, debugger attach/control, safe
   detach, restart, and removal without leaving machine-wide configuration behind.
3. Verify multi-host registration, discovery, and routing over live SSH tunnels.
4. Retain authenticated extension RPC and loopback binding as the default endpoint boundary.
5. Complete MCP Streamable HTTP session behavior required by strict clients.
6. Define session ownership or leases before supporting competing clients or independent sessions.
7. Add per-client and per-target permissions for discovery, inspection, execution control, mutation,
   termination, host export, target-code execution, artifact editing, live patching, and host scripting.
8. Define disconnect behavior that never silently resumes, detaches, or terminates a paused target.
9. Add bounded, redacted audit records for side-effecting operations.
10. Document SSH or WireGuard tunnels as the default remote path. Direct exposure additionally requires
   TLS, mutual client authentication, request/rate limits, and explicit capability policies.

Exit criteria:

- An authenticated remote client can select a host and debug a target through the supported path.
- A supported remote Windows host can run the published debugger bundle after file deployment alone,
  without installing development tools, frameworks, runtimes, redistributables, or the Gateway.
- The debugger is unreachable from the VM or LAN except through that path.
- Reconnection preserves session state and event cursors when dnSpy survives.
- Authorization tests prove an inspection-only client cannot control, mutate, terminate, export,
  execute, edit, patch, or script.
- Ownership tests cover contention, disconnect, expiry, and recovery without implicit target control.

## 2. Close compatibility and quality gaps

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

## 3. Optional high-risk capabilities

Target-code execution, artifact editing/project export/live patching, and dnSpy-host scripting remain
unscheduled. Their trust boundaries, prerequisites, and exit criteria are consolidated in
[future capabilities](FUTURE_CAPABILITIES.md).

Do not implement one merely to make the tool surface broader. Start only for a concrete workflow, after
the remote permission and audit model can represent its risk independently.

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
