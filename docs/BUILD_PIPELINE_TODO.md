# Immutable Build and Packaging Pipeline

Status: implemented. The old dgSpy build, pack, install, launcher, remote-pack, and net48-host
compatibility scripts have been removed.

## Goal

Replace the current chain of mutually dependent PowerShell scripts and mutable publish directories with
an immutable, staged pipeline. Building, composing a runnable layout, packaging, and installing must be
separate operations with explicit inputs and outputs.

The desired flow is:

```text
compile host
    -> artifacts/host-raw/<build-id>

compile dgSpy and HookLab components
    -> artifacts/dgspy-components/<build-id>

compose and verify a fresh runnable layout
    -> artifacts/packages/dgspy-win-x64/<package-id>

install an already verified package
    -> versioned installed tree
```

No stage may modify another stage's output. A completed directory is published by atomic rename.

## Rules

- The dnSpy build output is never also a deployment target, live-test host, or packaging workspace.
- Packaging never invokes compilation implicitly.
- Installation never invokes compilation or packaging implicitly.
- Repository and GUI tests launch only from a completed, verified layout artifact.
- Every command accepts explicit input and output paths.
- Failed work preserves the last completed artifact and prints an exact retry command.
- Host-only installation is allowed only when the package uses the installed protocol contract.
- Full protocol or MCP schema changes require a full control-plane update and restart.
- Release layouts use one authoritative file/dependency specification.
- Hashing, collision detection, manifests, verification, installation, and rollback have one implementation.

## Intended implementation

The C# tool under `Build/DgSpyTool` is the authoritative entry point:

```powershell
dotnet run --project Build/DgSpyTool -- pipeline
dotnet run --project Build/DgSpyTool -- verify --layout artifacts/layouts/local
dotnet run --project Build/DgSpyTool -- install --package artifacts/packages/dgspy-win-x64/local --install <directory>
```

MSBuild remains responsible for compilation, generated resources, dependency ordering, and apphost work
that belongs to a project build. `DgSpyTool` owns directory composition, file-collision policy,
deterministic manifests, hashing, atomic publication, installation, rollback, and agent registration.

## Dependency cleanup

Create a dgSpy-only solution or build project that:

- builds dgSpy, HookLab, Gateway, CLI, and their real project dependencies normally;
- consumes already-built dnSpy contract assemblies as file references;
- cannot rebuild dnSpy implementation projects or write into the host publish tree;
- replaces the deleted hand-maintained dependency build sequence.

Do not assume `Private="false"` or `ExcludeAssets="runtime"` prevents a `ProjectReference` from being
built. Those settings primarily affect copied runtime assets. The separation from the upstream host
build must be structural.

## Implemented flow

1. `pipeline` compiles host and components into independently published, hashed artifacts.
2. `compose` merges those inputs in fresh staging with typed ownership and explicit collision rules.
3. `verify` checks the complete inventory, size and SHA-256 of every file plus required layout contracts.
4. `package` consumes only a verified layout; `install` consumes only a verified package.
5. Publication and installation use rename-based swaps and restore the previous completed tree on failure.
6. Host-only installation is ownership-based and refuses protocol drift before changing any file.
7. CI and the modernization gate launch and inspect only the completed composed layout.
8. The managed component graph restores/builds in one MSBuild process; CLI, Gateway, and installer publish in
   parallel without a second restore, and hashing/copy verification uses bounded parallel work.
9. CI builds the verified net10 package once and passes that exact artifact to CorDebug and every
   Mono matrix job.
10. Full install replaces legacy trees rather than migrating them, registers Codex or Claude, and
    restores the previous tree if registration fails.

Remaining cleanup: structurally separate the dgSpy extension's upstream contract references so the
last `BuildProjectReferences=false` can disappear.

## Completed first milestone

The milestone completed when:

- composition always starts from empty staging;
- it never writes into project `bin` or `publish` directories;
- the complete layout is verified before publication;
- publication is an atomic rename;
- tests and packaging consume the published layout;
- rerunning composition produces the same manifest from the same inputs;
- an interrupted composition leaves the previous completed layout usable.
