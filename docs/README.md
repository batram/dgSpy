# dgSpy documentation

- [Quick start and local deployment](GETTING_STARTED.md)

## Start here

- [Implementation plan](IMPLEMENTATION_PLAN.md) — open work and ordering.
- [Architecture](ARCHITECTURE.md) — current boundaries, state model, ownership, and safety rules.
- [Build baseline](DGSPY_BASELINE.md) — supported toolchain, build/deploy workflow, and retained patches.
- [Tool and behavior reference](DGSPY_REFERENCE.md) — MCP tools, authentication, identities, events,
  handles, evaluation, and engine behavior.
- [dnSpyEx synchronization](DNSPYEX_SYNC.md) — maintaining the integration branch and contributing
  focused fixes upstream.
- [Remote hosts](REMOTE_HOSTS.md) — deploy packages and the target outbound registration/TLS design.

## Active feature plan

- [HookLab implementation plan](HOOKLAB_IMPLEMENTATION_PLAN.md) - one direct GUI and MCP path for
  guarded Harmony observation hooks. It replaces the earlier provider, atomic-action, and micro-task
  plans.

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
