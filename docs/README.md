# dgSpy documentation

The documentation is split by purpose so completed implementation history does not obscure the active
roadmap.

## Start here

- [Architecture](ARCHITECTURE.md) — stable system boundaries, scope, state model, and safety rules.
- [Delivered local core](DELIVERED_LOCAL_CORE.md) — the completed first implementation section
  (former Phases 0–8), its capability groups, verification boundary, and retained limitations.
- [Status and handoff](DGSPY_STATUS.md) — detailed live evidence, known gaps, and hard-won debugger facts.
- [Implementation plan](IMPLEMENTATION_PLAN.md) — open work only, beginning with dependency modernization.

## Build, operate, and verify

- [Build baseline](DGSPY_BASELINE.md) — supported toolchain, build/deploy commands, upstream edits, and
  dnSpy dispatcher rules.
- [Tool and behavior reference](DGSPY_REFERENCE.md) — MCP authentication, tool semantics, identities,
  handles, events, evaluation, and debugger behavior. The filename is historical; the content now covers
  the delivered local tool surface beyond the original milestone.
- [Unity verification checklist](DGSPY_UNITY_CHECKLIST.md) — repeatable Mono/Unity checks and evidence.
- [Modernization regression gate](MODERNIZATION_GATE.md) — dependency-stage fixtures, contract snapshots,
  and shared/CorDebug/Unity pass commands.
- [dnSpyEx synchronization](DNSPYEX_SYNC.md) — updating the integration branch and preparing focused
  upstream pull requests.
- [Upstream source review](UPSTREAM_SOURCE_REVIEW.md) — pinned-versus-current dependency assessment and
  modernization recommendations.

## Optional high-risk tracks

- [Target-code execution](TARGET_CODE_EXECUTION_PLAN.md)
- [Assembly editing](ASSEMBLY_EDITING_PLAN.md)
- [dnSpy-host scripting](DNSPY_SCRIPTING_PLAN.md)

`DGSPY_STATUS.md` is the evidence ledger, not the roadmap. Historical detail remains there so future
changes can distinguish “implemented” from “verified on CorDebug”, “verified on Mono”, and
“capability-gated”.
