# dgSpy documentation

This directory documents the product as it exists. Put implementation tasks, TODO lists,
investigations, handoffs, and run evidence in the separate `docs/local` work repository. See
[Documentation and work](DOCUMENTATION_AND_WORK.md) for the boundary.

## Product

- [Architecture](product/ARCHITECTURE.md) — current components, state, ownership, trust boundaries, and
  safety invariants.
- [HookLab](product/HOOKLAB.md) — resident model, supported hooks, GUI/MCP surfaces, watcher relationship,
  limitations, and safety invariants.

## Guides

- [Quick start](guides/GETTING_STARTED.md) — first-run setup, client startup, and local deployment.
- [Remote hosts](guides/REMOTE_HOSTS.md) — package and operate authenticated outbound debugger hosts.
- [HookLab watcher](guides/HOOKLAB_WATCHER.md) — install, control, upgrade, and uninstall the standalone
  watcher.
- [dnSpyEx synchronization](guides/DNSPYEX_SYNC.md) — maintain the upstream relationship and retained
  patches.

## Reference

- [Build and deployment baseline](reference/DGSPY_BASELINE.md) — supported toolchain, pipeline, layout,
  gates, and failure modes.
- [MCP tool and behavior reference](reference/DGSPY_REFERENCE.md) — operations, authentication,
  identities, events, evaluation, and engine behavior.

## Roadmap

- [Dream of roads](roadmap/dream_of_roads.md) — current aspirational direction and rough ordering.
- [Future high-risk capabilities](roadmap/FUTURE_CAPABILITIES.md) — deliberately unscheduled target
  execution, editing/live replacement, and host scripting boundaries.

## Examples and history

- [Differential debugging across two processes](example/differential-debugging-two-processes.md) — a
  worked live-debugging example.
- [History](history/README.md) — completed plans, dated acceptance, and superseded investigations.

`IMPLEMENTATION_PLAN.md` remains only as a compatibility pointer for older links.
