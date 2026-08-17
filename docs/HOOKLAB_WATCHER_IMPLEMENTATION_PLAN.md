# HookLab standalone injector and watcher implementation plan

Status: phases 0-4 and the installed H1/H2 watcher path are delivered. This document remains the design
authority for component, trust, lifecycle, and package boundaries; it is not the current ordered
roadmap. Follow [Dream of roads](dream_of_roads.md) for sequence. The actionable remainder is the
dgSpy export/enrollment, upgrade-state, management, and cross-controller adoption work under
**Open interoperability follow-ups**.

## Product goal

Run approved HookLab packages automatically in matching x64 CLR v4 processes without requiring dnSpy,
the dgSpy extension, Gateway, or MCP at deployment time.

dgSpy remains the authoring, inspection, and debugging environment. It discovers exact module and method
identity, develops and live-verifies hooks, and exports guarded packages. A small standalone watcher is
the deployment environment. It discovers matching processes, initializes or adopts the resident HookLab
runtime, reconciles the desired package, and reports the result.

The first delivered use is the confirmed VMConnect fullscreen fix. Prove the standalone injection path
against an explicitly selected live process first, then use the same executable and package format from
the per-user global watcher. The watcher must not embed VMConnect-specific behavior in its process or
injection machinery.

```text
dgSpy / HookLab GUI                 HookLab.Watcher
author, inspect, verify             discover, initialize, reconcile
          |                                      |
          +---- guarded HookLab package --------+
                                                 |
                                      HookLab.Injector
                                                 |
                                  native bootstrap + resident pipe
                                                 |
                                     x64 CLR v4 target process
```

## Supported boundary

- Windows x64 host and x64 target processes only.
- Desktop CLR v4 / .NET Framework 4.x targets only.
- Per-user interactive-session deployment.
- Compiled HookLab Prefix, Postfix, paired Prefix/Postfix, Finalizer, and Transpiler packages supported
  by the existing resident runtime.
- Exact MVID, metadata token, method signature, and IL SHA-256 guards remain mandatory.
- One resident HookLab generation per target process.
- VMConnect is the first profile, not a privileged special case in the engine.

The first version does not support x86, CoreCLR-only targets, Mono/Unity endpoints, cross-user session
injection, pre-logon processes, or a Windows service. Do not add those targets or their abstractions to
the prototype.

## Existing foundation

The difficult target-side work already exists and is package-proven:

- `HookLab.NativeBootstrap.x64.dll` enters an ordinary running x64 CLR v4 process.
- `HookLab.Bootstrap.dll` embeds and verifies the contracts and CorDebug resident probe payloads.
- The resident runtime compiles, verifies, applies, updates, toggles, lists, and removes guarded hooks.
- `HookLab.Host.Transport` provides the resident named-pipe protocol, authentication, health checks,
  discovery records, credential rotation, and target identity types.
- `NativeHookLabInitializer.Load(processId, nativePath)` is already the native initialization edge used
  by the dgSpy extension.
- The current extension stages the native and managed bootstrap, writes initialization parameters,
  invokes the native initializer, reads the completion report, verifies the pipe, and then sends hook
  operations through the resident transport.

The standalone path must extract and reuse this behavior. It must not copy the extension's private
implementation into a second product.

## Non-goals

- Do not ship dnSpy, dgSpy Gateway, MCP, MEF, WPF, debugger contracts, a decompiler, or a debugger engine
  with the watcher.
- Do not patch Microsoft executables or assemblies on disk.
- Do not replace `VmConnect.exe` or intercept the Hyper-V snap-in's `Process.Start` call.
- Do not create a custom MMC snap-in or `.msc` format.
- Do not treat filename-only matching as authority to inject.
- Do not build a general arbitrary-code autoinjector. Only locally approved, integrity-checked HookLab
  packages with exact target guards are eligible.
- Do not make global watching the first acceptance environment. Prove explicit-PID application against
  VMConnect first.

## Target component graph

### `HookLab.Injector`

Add a small host-side library under `HookLab/HookLab.Injector`. It is the sole owner of standalone
target initialization, resident adoption, and desired-state reconciliation. It must not reference
dnSpy, dgSpy Protocol, Gateway, MCP, MEF, WPF, or debugger implementation assemblies.

It owns:

- stable live-process identity: PID plus process creation time, full image path, session ID, and
  architecture;
- bounded CLR v4 readiness detection;
- package manifest parsing and integrity verification;
- safe payload staging and cleanup;
- the extracted `NativeHookLabInitializer` implementation;
- initialization parameter construction and completion-report parsing;
- existing-resident discovery, authentication, recovery, and adoption;
- typed resident transport operations;
- desired-versus-observed hook reconciliation;
- stable result and failure contracts suitable for both dgSpy and the watcher;
- cancellation, deadlines, logging events, and post-timeout readback rules.

Prefer a `net10.0-windows` host library if the current Windows APIs and packaging remain simplest there.
The injected payload remains CLR v4/net48. Do not retarget target-side assemblies merely to share the
host library.

### `HookLab.Watcher`

Add a small Windows executable under `HookLab/HookLab.Watcher`. It consumes `HookLab.Injector` and owns:

- command-line modes;
- profile loading and enablement;
- process-start observation and startup reconciliation;
- bounded concurrent work scheduling and PID/creation-time deduplication;
- per-profile and global pause controls;
- user-facing status, failure notification, and a bounded local audit log.

It contains no hook-specific C# source and no VMConnect-specific injection branch. The VMConnect package
is ordinary input selected by its profile.

### dgSpy extension

Refactor `Extensions/dgSpy.Extension` to consume the shared injector rather than retain a private native
initializer and autonomous staging path. dgSpy continues to own debugger selection, running/paused
state preservation, GUI state, MCP contracts, method discovery, and authoring. It delegates standalone
initialization/adoption and resident commands through the shared host-side boundary where practical.

If framework constraints prevent the net48 extension from referencing the `net10.0-windows` injector
directly, split the reusable code at the narrowest real boundary:

- a framework-neutral contracts/package/transport library shared by both;
- one small Windows native-loader implementation per host framework only where required.

Do not solve a framework mismatch by duplicating package validation, discovery identity, adoption, or
reconciliation policy.

## Package and profile contract

A HookLab package is immutable deployment input, not a loose source-file reference. Define a versioned
manifest containing at least:

- schema version and package ID;
- package revision and display name;
- target executable filename and normalized full-path policy;
- optional publisher/file-version constraints used for host-side selection;
- required architecture and runtime family;
- module identity including exact expected MVID;
- declaring type, method name, metadata token, signature, and IL SHA-256;
- hook ID, kind, revision, enabled state, and complete C# source;
- SHA-256 for every package entry;
- minimum compatible bootstrap/probe protocol version.

The package must be a closed directory or archive with a manifest-owned inventory. Reject undeclared
files, missing entries, duplicate normalized paths, path traversal, reparse points, digest mismatches,
unsupported schema versions, and source references outside the package. The existing build layout and
HookLab payload-manifest verification are the model; do not invent a mutable search path.

A profile is local deployment policy and refers to a package by stable ID and digest. It controls:

- enabled/disabled state;
- `explicit` or `user` scope;
- permitted normalized executable paths;
- notification policy;
- bounded CLR-readiness and initialization deadlines.

Profiles never weaken the method guards stored in the package.

The initial VMConnect package must be produced from the already verified hook identity and source in
`docs/local/vmconnect-fullscreen-login-investigation.md`. Re-read the live method identity during
implementation; do not copy illustrative module names or placeholder guards from planning text.

## Process identity and matching

Process matching is a two-stage operation.

### Discovery filter

Use cheap host-side facts to identify candidates:

- executable filename;
- normalized full image path;
- Windows session ID;
- x64 architecture;

Filename is only a filter. Before obtaining injection rights, and again immediately before native load,
verify PID plus creation time and full image path so PID reuse or a changed candidate cannot redirect the
operation.

### In-target authority

The resident runtime remains authoritative for module and method selection. It must rediscover the exact
expected MVID and then require the configured token, signature, and IL digest. Zero matches, multiple
matches, and any guard mismatch fail explicitly. Never select the first display-name or filename match.

## Resident lifecycle and adoption

Adoption is a prerequisite for a reliable watcher, not deferred polish. A watcher restart must not
force the user to restart every hooked target or create a second resident generation.

For each candidate process:

1. Establish its stable live identity.
2. Query `ProbeDiscoveryStore` for records bound to that exact live identity.
3. Verify the resident endpoint with authenticated challenge-response.
4. If valid, recover/rotate credentials as defined by the transport and adopt the endpoint.
5. Read resident status and current hooks before issuing any mutation.
6. If no valid resident exists, perform one native initialization.
7. After an initialization timeout, read the completion/discovery state before considering a retry.
8. If a resident generation exists but cannot be authenticated or adopted, return a stable
   `resident_not_adoptable` failure. Never inject a second generation blindly.
9. Reconcile the package against the observed resident hook inventory.

Reconciliation rules:

- exact package hook ID, target identity, source digest, revision, and enabled state already present:
  no-op success;
- same hook and target with an older revision: update once;
- desired disabled state differs: toggle without recompilation;
- compilation or update failure: preserve the last good resident revision and report failure;
- target identity differs under an existing hook ID: refuse rather than replace;
- unknown resident hooks: list and preserve them unless a future explicit ownership/removal policy says
  otherwise.

The watcher owns only hooks installed from its package namespace. It must not implement a process-wide
`Remove All` during shutdown or reconciliation.

## Watcher commands and operating modes

Define a small, scriptable CLI before adding shell integration:

```text
HookLab.Watcher.exe validate-package <package>
HookLab.Watcher.exe apply --package <package> --pid <pid>
HookLab.Watcher.exe status [--pid <pid>]
HookLab.Watcher.exe run --profiles <directory>
```

Exit codes and JSON output must distinguish invalid input, no match, unsupported target, access denied,
runtime-not-ready, initialization failure, resident-not-adoptable, guard mismatch, compiler failure,
transport loss, timeout-with-known-state, and success/no-op success.

### Explicit mode

`apply --pid` performs one bounded adoption-or-initialization and reconciliation. It is the first
standalone acceptance path and the diagnostic primitive used by later modes.

### Per-user global mode

After explicit-PID acceptance, `run` observes all enabled profiles in the current interactive user session.
Subscribe to process-start events, then reconcile already-running candidates so the subscription has no
startup gap. Use PID plus creation time for deduplication. Bound parallel work so one process waiting for
CLR initialization cannot block other profiles.

Start global mode through an explicitly installed per-user logon task. Do not add a Windows service in
this plan. If elevation is required, use a narrowly ACLed highest-privilege scheduled task in the same
interactive user session and secure every writable input boundary accordingly.

## Elevation and security model

Injection is equivalent to local process control. The minimal executable must be smaller than dgSpy but
not less strict.

- Request only the process rights demonstrated necessary by the native initializer.
- Do not enable debug privilege globally unless a proven target requires it; scope and restore it if it
  becomes necessary.
- Never cross into another user's Windows session in the supported version.
- Keep packages and executable payloads in a directory not writable by less-privileged identities than
  the watcher. Validate owner, ACL expectations, and reparse-point absence before elevated use.
- Verify the native bootstrap, managed bootstrap, package manifest, and every package entry before
  opening the target for injection.
- Bind discovery credentials to stable target identity and protect them with the existing DPAPI/ACL
  policy.
- Never log pipe secrets, endpoint nonces, bootstrap credentials, or complete secret-bearing parameter
  payloads.
- Log package ID/digest, profile ID, process ID/creation time, image path, target MVID, hook ID/revision,
  operation, duration, and sanitized result.
- A changed executable, module, method body, payload, or package fails closed and produces one actionable
  notification rather than an unbounded retry loop.
- Global pause and per-profile disable must stop new work. They do not silently remove already-installed
  hooks; removal is a separate explicit operation.

## Failure and concurrency rules

- Every wait is bounded and cancellation-aware.
- Process exit is a normal terminal result and cancels pending work for that identity.
- Serialize initialization per stable target identity; allow bounded concurrency across different
  targets.
- Deduplicate process events and concurrent profile matches before mutation.
- Mutation timeouts require status/readback before retry because the operation may have completed after
  the caller stopped waiting.
- Preserve completed package verification, process evidence, and resident status in diagnostic output.
- Cleanup only staging owned by the current operation and prove the resolved path is within the dedicated
  staging root.
- Watcher or transport failure must not terminate, pause, resume, or otherwise change the target process.
- Shutdown closes watcher-side connections and subscriptions but does not remove owned hooks unless the
  user explicitly requests removal.

## Implementation phases

### Phase 0 - freeze contracts and characterize the current path

Before refactoring:

- add focused characterization tests for `NativeHookLabInitializer`, parameter serialization,
  completion parsing, payload staging, discovery identity, pipe health verification, and current compiled
  hook commands;
- record the minimum process rights used by native loading;
- prove the current package's VMConnect hook identity from live/current evidence;
- define stable injector result codes and the package/profile schemas;
- document which current extension behaviors depend on debugger pause/resume state and keep those in the
  extension adapter.

Acceptance: no behavior change; current HookLab extension and CorDebug gates remain green.

### Phase 1 - extract the standalone injector core

- create `HookLab.Injector`;
- move or extract native loading, staging, parameter construction, completion parsing, discovery/adoption,
  transport connection, and reconciliation into it;
- keep one authoritative implementation of identity and package validation;
- adapt the dgSpy extension to the shared boundary without changing MCP or GUI behavior;
- remove the old private implementation only after shared-path parity is proven.

Acceptance:

- existing dgSpy HookLab GUI/MCP tests pass unchanged at the public boundary;
- exact-package CorDebug acceptance still initializes, creates, updates, toggles, removes, and detaches
  cleanly;
- no new dnSpy or dgSpy dependency appears below `HookLab.Injector`;
- package and upstream-drift verification pass.

### Phase 2 - explicit-PID standalone application

- create `HookLab.Watcher` with `validate-package`, `apply`, and `status`;
- implement package verification and stable target identity checks;
- initialize a fresh target without starting dnSpy, Gateway, or MCP;
- adopt an existing resident after watcher restart;
- reconcile one compiled package idempotently.

Acceptance against a disposable x64 CLR v4 fixture:

- fresh initialization and hook creation succeed;
- a second identical apply is a no-op;
- watcher restart adopts the same resident and does not create another generation;
- older revision updates once and compiler failure preserves the last good revision;
- wrong executable identity, runtime, MVID, token, signature, and IL digest each fail closed;
- target exit during every major stage is clean;
- dnSpy, dgSpy Gateway, and MCP are absent for the entire run.

### Phase 3 - VMConnect package and explicit live acceptance

- export the confirmed fullscreen Prefix as the first immutable package;
- add the guarded VMConnect profile for later per-user observation;
- apply and reconcile the package against explicitly selected live VMConnect processes;
- show one concise notification and write an actionable log on failure.

Acceptance on the normal desktop, with GUI launches following the repository's hidden-desktop policy
except for an explicitly requested visible final demonstration:

- an explicitly selected VMConnect process receives the hook without dnSpy, Gateway, or MCP;
- two simultaneous VMConnect processes are handled independently;
- fullscreen login resizes correctly without the manual fullscreen toggle;
- already-running and very-short-lived candidates do not corrupt watcher state;
- invalid guards after a simulated package/target mismatch fail closed and visibly.

### Phase 4 - per-user global watcher

- implement `run` with process-start subscription plus startup reconciliation;
- load multiple enabled profiles;
- add bounded concurrency, deduplication, pause/disable, audit rotation, and logon-task installation;
- reuse the exact `apply` pipeline; global mode adds scheduling only.

Acceptance:

- a matching process started before and after the watcher is handled once;
- unrelated processes with the same basename but wrong path/identity are refused;
- multiple profiles and targets cannot block or overwrite one another;
- watcher restart adopts live residents and reconciles without target restarts;
- logon startup runs in the intended interactive session and validates all privileged inputs;
- stopping the watcher leaves targets and installed hooks intact;
- uninstall removes the watcher, task, profiles, and watcher-owned state without touching target binaries
  or unrelated dgSpy state.

### Phase 5 - dgSpy package export and management UX

Only after the standalone deployment path is stable:

- export the selected live-verified hook and its exact guards as a package;
- validate package compatibility before export;
- add an unelevated notification-area companion that starts at user logon independently of the elevated
  watcher and reads the authenticated status/control boundary rather than hosting UI in the watcher;
- make its icon and tooltip distinguish running, paused, degraded/error, stale, and stopped state from an
  exact watcher process identity, without treating an old status file as live;
- expose only real management operations: status summary, pause/resume, enabled-profile checkmarks and
  enable/disable, start/restart of the installed task, and opening the bounded audit log;
- make **Exit tray** close only the companion. Stopping the watcher is a separate explicit action and
  still leaves target processes and resident hooks untouched;
- honor each profile's notification policy for concise tray notifications; do not create success spam or
  a second audit/status store;
- show installed watcher packages/profiles and their last sanitized result in a fuller management window
  reachable from the tray icon;
- keep package editing, hook removal, and other unavailable mutations absent until authoritative backend
  operations exist for them;
- keep watcher enablement and automatic deployment an explicit user action.

This phase is not required for the VMConnect prototype; the first package may be produced by a repository
tool using the same schema and verifier.

## Tests and verification

Add focused unit and integration coverage for:

- package canonicalization, inventory, digest, traversal, duplicate, and reparse-point rejection;
- stable process identity and PID-reuse rejection;
- CLR readiness timeout and process-exit races;
- discovery record authentication, rotation, stale cleanup, and adoption;
- one-generation enforcement;
- initialization completion success, malformed output, timeout, late success, and cleanup;
- reconciliation for absent, identical, older, disabled, conflicting, and unknown hooks;
- mutation timeout followed by authoritative readback;
- event deduplication and bounded scheduling;
- profile enable/disable and global pause behavior;
- sanitized audit output and rotation;
- exact command exit codes and JSON result contracts.

Use a disposable x64 CLR v4 fixture for injection tests. The VMConnect test is a separate machine-specific
acceptance leg and must not replace deterministic fixture coverage.

Run repository verification through the supported path:

```powershell
dotnet run --project Build\DgSpyTool -- pipeline
.\tests\run-modernization-gate.ps1 -Stage CorDebug
powershell -NoProfile -File tools\check-upstream-drift.ps1 -Revision HEAD -Report
```

Extend `DgSpyTool` ownership, composition, layout, and package verification for the new artifacts. Do not
introduce a second ad hoc build or publish route. If the standalone watcher is later distributed without
the full dgSpy package, add a verified package flavor to `DgSpyTool`; it must still derive from the same
immutable component outputs and manifests.

## Documentation deliverables

Before declaring each phase complete, update:

- `docs/ARCHITECTURE.md` with the shared injector and standalone deployment boundary;
- `docs/DGSPY_BASELINE.md` with the verified watcher package/build/install commands;
- `docs/DGSPY_REFERENCE.md` only where dgSpy's public HookLab behavior changes;
- `docs/local/vmconnect-fullscreen-login-investigation.md` with current standalone acceptance evidence;
- a watcher user guide covering package trust, profile scopes, logs, pause/disable, update mismatch, and
  uninstall behavior.

Keep implementation claims, fixture measurements, and live VMConnect acceptance evidence separate.

## Open interoperability follow-ups

- Make dgSpy adopt and authenticate an existing HookLab resident initialized by the standalone watcher,
  then read its generation and hook inventory before adding dgSpy-owned hooks. It must preserve unknown
  and watcher-owned hooks and must never inject a competing resident generation.
- When dgSpy initialization encounters a valid resident that it cannot adopt, return the stable
  `resident_not_adoptable` failure with actionable ownership/authentication detail instead of allowing
  the operation to end as a generic `hook_operation_timed_out`.
- Make watcher `status` preserve its valid watcher lifecycle and control output when an individual
  resident cannot be inspected. Return the failed resident as a structured per-resident error rather
  than replacing the entire status response with one top-level `operation_failed` message.
- Make HookLab target discovery round-trip the exact case-sensitive CLR assembly simple name used by
  `create_hook`. At minimum, `get_hook_template` must return `AssemblyName.Name` with the other guarded
  target fields, and the MCP schema/reference must distinguish it from the module filename, path, and
  display name so callers never infer values such as `VmConnect` from `VmConnect.exe` when the runtime
  identity is `vmconnect`.
- Preserve watcher run state across a successful upgrade. If the exact verified installed watcher/task
  was running before replacement, restart the newly registered task, read back its new PID plus process
  creation identity and installed image path, and require its live catalog/status to become healthy
  before reporting the upgrade complete. Preserve an intentionally stopped watcher as stopped, and make
  first-install start policy explicit rather than depending on the next logon.
- Add an integrity-checked atomic package-enrollment operation so a canonical dgSpy export can be
  installed and explicitly enabled without rebuilding or replacing the watcher executable. Keep the
  executable installation tree closed and immutable; publish packages/profiles through a separately
  protected, generation-based catalog with staging, package/digest/ACL/reparse verification, same-ID
  conflict refusal, last-good rollback, and live catalog-generation readback. Export remains disabled
  and unenrolled by default; automatic deployment still requires an explicit enable decision.
- Add live acceptance coverage for both ownership directions: watcher adopts a dgSpy-created resident,
  and dgSpy adopts a watcher-created resident while the existing hook remains active.

## Completion criteria

This plan is complete when all of the following are true:

- a verified VMConnect package can be applied to an explicit PID without dnSpy, dgSpy Gateway, or MCP;
- the same unmodified `apply` pipeline works from the per-user global watcher;
- watcher restart adopts an existing resident runtime without reinjection or target restart;
- exact target guards and package integrity fail closed;
- dgSpy and the watcher consume one authoritative injector/adoption implementation;
- the supported build pipeline produces and verifies every standalone artifact;
- normal dgSpy HookLab GUI/MCP behavior and existing CorDebug coverage remain green;
- watcher shutdown, failure, update mismatch, and uninstall never terminate or modify target binaries.
