# Build and Packaging Simplification TODO

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

Create a small C# tool under `Build/DgSpyTool` with commands such as:

```powershell
dotnet run --project Build/DgSpyTool -- compose
dotnet run --project Build/DgSpyTool -- verify
dotnet run --project Build/DgSpyTool -- install
dotnet run --project Build/DgSpyTool -- pack-remote
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

## Migration order

1. Add `Build/DgSpyTool` with `compose` and `verify` commands.
2. Define one typed layout specification containing every shipped file and its ownership category.
3. Consume the existing build outputs and produce a fresh immutable runnable layout.
4. Port collision handling, protocol/extension matching, HookLab payload verification, manifests, and
   complete-tree verification into the tool.
5. Change local, live, composition, and GUI tests to use only the completed layout artifact.
6. Make installers consume verified packages only; remove implicit repository builds.
7. Port atomic install, rollback, host-only compatibility checks, and remote-host packaging.
8. Introduce the dgSpy-only build graph and remove the hand-ordered project builds.
9. Replace `build.ps1`'s in-place root/`bin` reshaping with a fresh host-layout output.
10. Reduce the old PowerShell scripts to compatibility wrappers, then remove obsolete recovery code.

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
