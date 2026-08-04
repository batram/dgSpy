# dgSpy implementation plan

This document contains open work and ordering decisions. Completed implementation details live in the
[delivered local core](DELIVERED_LOCAL_CORE.md), stable design rules in
[architecture](ARCHITECTURE.md), and verification evidence in [status and handoff](DGSPY_STATUS.md).

## Section 1 — Local debugger core: complete

The first implementation section, formerly Phases 0–8, is complete as of 2026-08-04. It delivered the
local x64 CorDebug and Mono/Unity debugger, decompiler, evaluation, breakpoint, analysis, export, event,
and low-level tool surface. The original phase-by-phase work lists and exit evidence have been moved out
of the active roadmap:

- [Delivered local core](DELIVERED_LOCAL_CORE.md) — concise completion boundary.
- [Status and handoff](DGSPY_STATUS.md) — detailed evidence and known gaps.
- [Tool and behavior reference](DGSPY_REFERENCE.md) — permanent operational semantics.
- [Build baseline](DGSPY_BASELINE.md) — build/deploy and dnSpy integration rules.
- [Unity checklist](DGSPY_UNITY_CHECKLIST.md) — Mono/Unity evidence.

The compatibility baseline is now a regression asset. Modernization must preserve or deliberately
replace its recorded behavior; “builds on newer sources” is not sufficient.

## Section 2 — Modernize the upstream baseline: next

The pinned dnSpy 6.1.8 dependency set is several years behind active upstreams. The completed
[upstream review](UPSTREAM_SOURCE_REVIEW.md) found that dnSpyEx is the practical coordinated upstream:
it maintains compatible forks of ILSpy v2, NRefactory, Roslyn.ExpressionCompiler, and
`Mono.Debugger.Soft`, plus dnlib and host changes. Modernization now precedes remote-host work.

Do this on a dedicated branch. Do not mix dependency migration with new MCP behavior, remote transport,
or optional execution/editing features.

### 2.1 Make the current baseline reproducible — complete

1. Choose the durable home for `Mono.Debugger.Soft` commit `888ded0f` (prefer a project fork; vendoring is
   the fallback) and update the tracked submodule URL or layout.
2. Put the dgSpy superproject on a writable project remote before publishing migration commits.
3. Verify a fresh recursive checkout can resolve every gitlink without the machine-local mirror.
4. Record the two deliberate upstream edits and their licenses in the build baseline.
5. Tag or otherwise preserve the last known-good pre-modernization revision and its evidence links.

Exit criteria:

- A fresh recursive checkout resolves without a local object database or absolute path.
- `build-dgspy.ps1` can build and deploy from that checkout.
- The three unit suites pass sequentially.
- The local frame-fetch timeout is reachable from a durable remote or is applied as a documented patch.

Completed 2026-08-04. The public superproject is
[`batram/dgSpy`](https://github.com/batram/dgSpy), the patched submodule is the public
[`batram/Mono.Debugger.Soft`](https://github.com/batram/Mono.Debugger.Soft) fork with `dgspy` as its
default branch, and tag `pre-modernization-2026-08-04` preserves the last known-good baseline. A
`--depth 1 --recurse-submodules --shallow-submodules` clone into an empty test directory resolved all
seven gitlinks, including `888ded0f`, without a local object database. From that clone, the documented
VS 2019 net48 build and `build-dgspy.ps1` build/deploy succeeded; the Protocol, Gateway, and Extension
suites then passed sequentially with 29, 182, and 18 tests respectively (229 total).

### 2.2 Establish a modernization regression gate — complete

1. Convert the existing phase evidence into a compact required gate list: build/deploy, the three unit
   suites, the CorDebug smoke, and the safe Unity checklist subset.
2. Add focused before/after fixtures for each upgrade claim rather than accepting a broad version bump as
   evidence: malformed or modern IL for decompiler changes, dynamic/in-memory metadata for dnlib, and
   watches/func-eval timeout behavior for Roslyn changes.
3. Capture current capability output and MCP tool discovery so dependency changes cannot silently remove,
   relabel, or weaken a tool.
4. Check for output/behavior drift separately from wire-contract drift.

Exit criteria:

- Every modernization stage has an explicit pre-change fixture and pass/fail command.
- A failed engine-specific check identifies whether the regression is CorDebug, Mono/Unity, or shared.
- Live checks remain bounded and use safe `detach`; test processes are checked before builds to avoid
  locked output artifacts.

Completed 2026-08-04. [`MODERNIZATION_GATE.md`](MODERNIZATION_GATE.md) records the stage matrix and
commands. `tests/run-modernization-gate.ps1` separates shared, CorDebug, and Unity failures; committed
capability and MCP tool-schema snapshots distinguish wire-contract drift from behavior drift. The
read-only Unity subset starts an isolated host and verifies safe detach and game liveness.

### 2.3 Update the decompiler compatibility line — bounded compatibility batch complete

Start with dnSpyEx's maintained `ilspyv2` and NRefactory forks, not original ILSpy 10. Port or advance in
small, reviewable batches. Prioritize runtime-async crash protection, stack-overflow/malformed-input
safety, modern IL disassembly, generic/extension-method correctness, and variable naming because these
directly affect `get_csharp`, `get_il`, search, and analyzer results.

For each batch:

1. Identify the exact dnSpyEx commits and the dgSpy paths/tools they affect.
2. Preserve dnSpy-facing APIs; do not combine this stage with a modern ILSpy API rewrite.
3. Run decompiler fixtures, all unit suites, and the CorDebug smoke.
4. Repeat the bounded Unity metadata/IL/C#/search subset before accepting it.

Exit criteria:

- Selected correctness fixes have fixtures that fail before and pass after, or a documented reason why
  the fixture is observational rather than executable.
- Existing method/type decompilation, metadata identities, sequence-point marking, and bounded searches do
  not regress on either engine.
- NRefactory remains until the ILSpy-v2/VB AST dependencies are actually removed; it is not replaced by
  Roslyn as an unrelated cleanup.

Completed 2026-08-04 for the first bounded correctness batch. The public
`batram/ILSpy:dgspy-modernization` branch at `8f6c0812` ports dnSpyEx runtime-async return handling
(`0b052bb5`, adapted without newer dnlib), parameterless generic extension decompilation (`a5dd6d5b`),
and malformed-property NRE protection (`68b487ee`). NRefactory remains at `79d99d6f` on the durable
`batram/NRefactory:dgspy-modernization` branch. The maintained heads were not accepted wholesale:
NRefactory alone broke the old VB visitor contract, and current ILSpy-v2 requires dnlib APIs reserved
for 2.4. Exact fixture classifications, deferred ports, commands, and results are in the
[modernization gate](MODERNIZATION_GATE.md).

### 2.4 Trial dnlib 4.5.0 independently

Update dnlib 3.3.2 to the dnSpyEx-aligned 4.5.0 in isolation. Resolve API/behavior changes without also
changing Roslyn, target frameworks, or debugger engines.

Required checks:

- Clean dnSpy/dgSpy build and sequential unit suites.
- CorDebug raw module, metadata, IL, C#, dynamic assembly, in-memory assembly, analyzer, and export paths.
- Mono/Unity file-backed and file-less metadata/IL/C#/raw-module paths.
- SHA-256, paging, truncation, malformed metadata, and engine identity for modules without usable paths.

Exit criteria:

- All required checks pass or a behavior change is explicitly accepted and documented.
- No regression is hidden by returning an empty result where the old contract returned a structured error.
- The upgrade stays independently revertible.

### 2.5 Update debugger and expression-compiler sources as a coordinated slice

Use dnSpyEx's aligned host, `Mono.Debugger.Soft`, Roslyn.ExpressionCompiler, and Roslyn package revisions as
the reference. Do not drop a current Roslyn expression compiler into the old host by itself.

1. Inventory dnSpyEx host contract changes required by Roslyn.ExpressionCompiler 5.6.0 and the chosen
   `Mono.Debugger.Soft` branch.
2. Reapply or replace the bounded Unity frame-fetch behavior; upstream still waits indefinitely in the
   checked branch.
3. Preserve old Unity protocol compatibility while evaluating newer Mono cancellation, connection,
   pointer-value, and protocol support.
4. Verify evaluation, watches, assignment, invocation/construction, compiler errors, hard func-eval
   timeout/recovery, object IDs, frame selection, and endpoint reattach.
5. Decide separately whether moving the host from .NET 5 to the dnSpyEx .NET 10 target is required now or
   can remain a later host migration. Keep the net48 extension deployment supported.

Exit criteria:

- CorDebug evaluation and low-level tests pass with the aligned compiler/host set.
- Live Unity attach, managed stack/locals, breakpoint, evaluate, continue, and safe detach pass without an
  unbounded frame wait.
- Capability advertisement accurately reports any engine behavior that changed.
- The migration documents which dnSpyEx commits were adopted, adapted, or intentionally omitted.

### 2.6 Decide whether to adopt the dnSpyEx host

After the bounded dependency stages, compare the resulting fork with dnSpyEx 6.6.x. A full host adoption
is justified only if the remaining host fixes outweigh the regression and maintenance cost.

Decision inputs:

- Runtime discovery, in-memory metadata, Raw Locals, Mono settings, breakpoint, environment-variable, and
  stack-overflow fixes still missing after the targeted stages.
- net48 extension compatibility and the cost of the host's .NET 10 build target.
- Ability to replay the complete CorDebug smoke and safe Unity checklist.
- Size and reviewability of the permanent dgSpy patch set relative to dnSpyEx.

Exit criteria:

- Record an explicit adopt/defer decision with commit-level evidence.
- If adopted, repeat the full regression gate and replace stale build/deployment documentation.
- If deferred, record the remaining useful host commits so the decision is reproducible.

## Section 3 — Secure remote hosts and ownership

Begin only after the modernization baseline is stable.

1. Add stable `host_id` registration and routing.
2. Add authenticated extension RPC; retain loopback binding by default.
3. Complete MCP Streamable HTTP session behavior needed by strict clients.
4. Document SSH/WireGuard tunnel recipes for VM and remote-host use.
5. For direct exposure, require TLS, mutual client authentication, request/rate limits, and explicit
   capability policies.
6. Add per-client/per-target permissions for discover, inspect, control, mutate, terminate, host export,
   target-code execution, artifact editing/live patching, and dnSpy-host scripting.
7. Add redacted audit records and defined disconnect behavior that never silently resumes, detaches, or
   terminates a paused target.
8. Define session ownership or leases before enabling competing clients or multiple independent sessions.

Exit criteria:

- A remote MCP client can securely discover and debug a target through the selected tunnel/listener.
- The debugger is not reachable from the VM/LAN without the selected authenticated path.
- Reconnection preserves session state and cursors when dnSpy survived.
- Authorization tests prove an inspection-only client cannot control, mutate, terminate, export, execute,
  edit, patch, or script.

## Section 4 — Remaining compatibility and quality work

These are open but do not precede modernization or secure remote transport unless a concrete workflow
requires them:

- CoreCLR and x86 support, each with its own engine fixtures and capability expectations.
- Visual Basic parity.
- A deterministic Unity fixture for currently manual or unsafe Mono verification gaps.
- Headless handling of dnSpy's modal connection-failure dialog.
- Multi-session isolation beyond multiple targets inside the default debugger manager.

## Section 5 — Documentation completion

Keep permanent documentation updated as behavior changes:

- Architecture and trust boundaries: [ARCHITECTURE.md](ARCHITECTURE.md).
- Local build/deployment: [DGSPY_BASELINE.md](DGSPY_BASELINE.md).
- Tool, state, handle, event, and capability semantics: [DGSPY_REFERENCE.md](DGSPY_REFERENCE.md).
- Runtime verification matrix and known gaps: [DGSPY_STATUS.md](DGSPY_STATUS.md) and
  [DGSPY_UNITY_CHECKLIST.md](DGSPY_UNITY_CHECKLIST.md).
- Remote installation, tunnel recipes, authorization, and security guidance: deliver with Section 3.
- Troubleshooting: extract recurring operational failures from the evidence ledger after modernization so
  commands and target-framework advice reflect the new baseline.

Optional high-risk work remains deliberately separate:
[TARGET_CODE_EXECUTION_PLAN.md](TARGET_CODE_EXECUTION_PLAN.md),
[ASSEMBLY_EDITING_PLAN.md](ASSEMBLY_EDITING_PLAN.md), and
[DNSPY_SCRIPTING_PLAN.md](DNSPY_SCRIPTING_PLAN.md).

## Product definition of done

The local debugger core is delivered. The broader product is complete when an authorized remote agent can
securely select a host and session; discover, attach or launch; control and wait; inspect/evaluate/mutate
within policy; navigate code and metadata; use capability-gated low-level operations; disambiguate multiple
targets; recover from disconnects and stale state; and perform all remote communication through an
authenticated, encrypted, auditable transport.
