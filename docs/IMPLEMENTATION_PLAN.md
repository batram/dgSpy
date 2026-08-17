# dgSpy implementation plan

The current ordered roadmap is [Dream of roads](dream_of_roads.md). This filename remains as a compact
baseline and compatibility target for older links; it is no longer a completion diary or a second
roadmap.

## Delivered baseline

dgSpy is a local and remote x64 debugger bridge for .NET Framework CorDebug and bounded Mono/Unity
workflows. The supported distribution provides lifecycle control, events, breakpoints, inspection,
evaluation, navigation and analysis, export, memory access, low-level engine operations, client-spawned
stdio MCP startup, workflow discovery, versioned local deployment, and remote provisioning.

The remote-host and trusted-local control-plane expansion is delivered:

- authenticated loopback Gateway-to-extension RPC and stable host identity;
- self-contained centrally provisioned remote packages and outbound host registration;
- optional exactly pinned mutual TLS without OS trust-store installation;
- session controller identity, optimistic lifecycle/execution/breakpoint guards, explicit release,
  expiry-based reclaim, inspected `force=true` takeover, and invariant disconnect behavior;
- coarse `full-control` and `inspect-only` access profiles;
- bounded redacted audit records for side-effecting requests.

HookLab is also delivered at its bounded x64 desktop-CLR-v4 scope: autonomous resident initialization,
guarded observation hooks, compiled Prefix/Postfix/Finalizer/Transpiler hooks, replacement rollback,
enable/disable/removal, GUI authoring, immutable packages, and an installed standalone per-user watcher.

Current behavior is defined by [Architecture](ARCHITECTURE.md), [Build baseline](DGSPY_BASELINE.md),
[Tool and behavior reference](DGSPY_REFERENCE.md), [Remote hosts](REMOTE_HOSTS.md), and
[HookLab watcher operation](HOOKLAB_WATCHER.md). Dated acceptance evidence lives in
[History](history/README.md) and `docs/local`.

## Current open sequence

Do not infer order from old numbered phases. Follow [Dream of roads](dream_of_roads.md):

1. live-accept dgSpy hook export, then add verified enrollment and explicit enablement;
2. preserve intentional watcher run state across install and upgrade;
3. support authenticated dgSpy/watcher adoption of either side's resident generation;
4. add an honest unelevated watcher management companion;
5. settle session-owner continuity and independently dispatched safe stimulus during waits;
6. fix reproduced MCP correctness/usability defects one fixture at a time;
7. pull richer HookLab authoring or high-risk capabilities only from concrete workflows.

The older claim-token proposal is not the only recovery mechanism: current code supports expiry-based
reclaim and deliberate inspected `force=true` transfer. Whether continuity additionally needs a
persisted, non-listable, revocable claim capability remains a product decision. Do not implement it as
if the current takeover surface did not exist.

## Parked scope

CoreCLR, x86, native debugging, broad Mono HookLab support, reverse patches, generic hook providers,
profiler/ReJIT, and Visual Basic parity are outside the current product scope. Their notes are useful
inputs if scope changes, not active milestones.

General target-code execution outside HookLab, assembly editing, project export, live method-body
replacement, and dnSpy-host scripting remain independent high-risk capabilities governed by
[Future capabilities](FUTURE_CAPABILITIES.md). None follows automatically from HookLab's bounded custom
C# compiler.

## Maintenance

dnSpyEx synchronization is routine repository maintenance, not a product milestone. Follow
[dnSpyEx synchronization](DNSPYEX_SYNC.md), preserve every intentional divergence in the drift
allowlist, run the supported pipeline and applicable gates, and keep synchronization changes separate
from feature work.

Keep current contracts in architecture/baseline/reference documents, open ordering in
`dream_of_roads.md`, and dated investigations in history or the ignored local overlay. Delete completed
roads instead of adding another completion narrative here.
