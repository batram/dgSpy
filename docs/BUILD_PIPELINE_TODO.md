# Immutable Build and Packaging Pipeline

Status: implemented for the shipping win-x64/net10 flow. The legacy scripts remain only for the
retained net48 compatibility path while that target is in scope.

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
- Release and remote-host layouts use one authoritative file/dependency specification.
- Hashing, collision detection, manifests, verification, installation, and rollback have one implementation.

## Intended implementation

The C# tool under `Build/DgSpyTool` is the authoritative entry point:

```powershell
dotnet run --project Build/DgSpyTool -- pipeline --repo . --artifacts artifacts --build-id local
dotnet run --project Build/DgSpyTool -- verify --layout artifacts/layouts/local
dotnet run --project Build/DgSpyTool -- install --package artifacts/packages/dgspy-win-x64/local --install <directory>
```

MSBuild remains responsible for compilation, generated resources, dependency ordering, and apphost work
that belongs to a project build. `DgSpyTool` owns directory composition, file-collision policy,
deterministic manifests, hashing, atomic publication, installation, rollback, and remote bundles.

PowerShell wrappers may remain for convenience, but they should only locate `dotnet`, forward arguments,
and return its exit code.

## Dependency cleanup

Create a dgSpy-only solution or build project that:

- builds dgSpy, HookLab, Gateway, CLI, and their real project dependencies normally;
- consumes already-built dnSpy contract assemblies as file references;
- cannot rebuild dnSpy implementation projects or write into the host publish tree;
- replaces the hand-maintained sequence of dependency builds in `build-dgspy.ps1`.

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
8. The managed component graph restores/builds in one MSBuild process; CLI and Gateway publish in
   parallel without a second restore, and hashing/copy verification uses bounded parallel work.
9. CI builds the verified net10 package once and passes that exact artifact to CorDebug and every
   Mono matrix job.

Remaining cleanup: structurally separate the dgSpy extension's upstream contract references so the
last `BuildProjectReferences=false` can disappear, and port the retained net48, agent-registration,
and remote-host compatibility flows before deleting their legacy scripts.

## First milestone

Keep the existing compilation commands temporarily, but replace their shared mutable deployment tree
with immutable composition:

> Existing compilation, new immutable composition.

The milestone is complete when:

- composition always starts from empty staging;
- it never writes into project `bin` or `publish` directories;
- the complete layout is verified before publication;
- publication is an atomic rename;
- tests and packaging consume the published layout;
- rerunning composition produces the same manifest from the same inputs;
- an interrupted composition leaves the previous completed layout usable.

Do not combine this milestone with a wholesale dnSpy build-system rewrite. Removing the shared mutable
tree delivers the largest reliability improvement while keeping compilation behavior stable.
