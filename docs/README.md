# dgSpy documentation

- [Quick start and local deployment](GETTING_STARTED.md)

This directory documents the product as it exists. Put implementation tasks, TODO lists,
investigations, handoffs, and run evidence in the separate `docs/local` work repository. See
[Documentation and work](DOCUMENTATION_AND_WORK.md) for the boundary.

## Start here

- [Dream of roads](dream_of_roads.md) — current aspirational roadmap and rough ordering of open work.
- [Implementation plan](IMPLEMENTATION_PLAN.md) — compact delivered baseline and compatibility pointer;
  retained for existing links.
- [Architecture](ARCHITECTURE.md) — current boundaries, state model, ownership, and safety rules.
- [Build baseline](DGSPY_BASELINE.md) — supported toolchain, build/deploy workflow, and retained patches.
- [Tool and behavior reference](DGSPY_REFERENCE.md) — MCP tools, authentication, identities, events,
  handles, evaluation, and engine behavior.
- [dnSpyEx synchronization](DNSPYEX_SYNC.md) — maintaining the integration branch and contributing
  focused fixes upstream.
- [Remote hosts](REMOTE_HOSTS.md) — deploy packages and the target outbound registration/TLS design.

## Feature design and delivered boundaries

- [HookLab watcher implementation plan](HOOKLAB_WATCHER_IMPLEMENTATION_PLAN.md) — delivered standalone
  watcher design plus the still-open enrollment, upgrade-state, and interoperability boundaries.
- [HookLab implementation plan](HOOKLAB_IMPLEMENTATION_PLAN.md) — delivered scope: one direct GUI
  and MCP path for guarded Harmony observation and custom hooks, plus a small deferred backlog
  (generic-method templates, editor UX).

## Completed proposals kept in place

- [Search proposal](SEARCH_PROPOSAL.md) — implemented; retained as the standing rationale for the
  ported dnSpy matching rules cited by the reference.
- [CoreCLR follow-ups](DNSPY_CORECLR_TODOS.md) — parked investigation notes; CoreCLR is outside the
  current dgSpy product scope and gate.

## Worked examples

- [Differential debugging across two processes](example/differential-debugging-two-processes.md) —
  finding why one Hyper-V VM window resizes and another does not, by comparing live state in two
  instances of the same binary. Covers elevated attach, decompilation, proving a negative with a
  breakpoint, and confirming causality by writing state back.

## Future capability design

- [Future capabilities](FUTURE_CAPABILITIES.md) — unscheduled target-code execution, assembly
  editing/project export/live patching, and dnSpy-host scripting.

The approved HookLab plans above supersede the runtime-hooking portion of that speculative policy;
general target execution, assembly editing/live replacement, and host scripting remain unscheduled.

## Historical evidence

[History](history/README.md) contains completed implementation records, dated verification ledgers,
modernization evidence, and superseded upstream investigations. Historical documents are evidence, not
the current roadmap or operational contract.
