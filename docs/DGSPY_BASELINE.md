# dgSpy build and deployment baseline

## Supported scope

- Windows x64 host.
- Self-contained `net10.0-windows` dnSpy/dgSpy package.
- CLR v4 CorDebug and Mono/Unity debug targets.
- HookLab payload targets CLR v4 and therefore remains `net48` internally.
- x86, CoreCLR debug targets, and the old net48 dnSpy host are out of scope.

## Prerequisites

- .NET SDK 10.
- .NET Framework 4.8 developer pack for CLR v4 fixtures and HookLab payload compilation.
- Visual C++ x64 build tools for `HookLab.NativeBootstrap`.
- Recursive Git submodules.

```powershell
git submodule update --init --recursive
```

## One build path

```powershell
dotnet run --project Build\DgSpyTool -- pipeline
```

The completed outputs are:

- `artifacts\host-raw\local`: immutable host compiler artifact.
- `artifacts\dgspy-components\local`: immutable dgSpy component artifact.
- `artifacts\layouts\local`: verified runnable layout.
- `artifacts\packages\dgspy-win-x64\local`: verified installable package.

The pipeline never deploys into a compiler output directory. Composition starts in fresh staging,
records every file's size, SHA-256, and owner, verifies the complete inventory, and publishes by
directory rename. Failed composition or installation leaves the previous completed tree available.

Lower-level `build`, `build-host`, `build-components`, `compose`, `verify`, `package`, `verify-package`,
and `snapshot` verbs exist for diagnostics and CI. `pipeline` is the normal shipping command.

## Install and register

From a repository checkout after running the pipeline:

```powershell
.\install-dgspy.ps1 codex
```

An extracted release carries the same tool:

```powershell
.\install-dgspy.exe codex
```

Use `.\install-dgspy.exe claude` for Claude Code. Registration is part of the install transaction. If validation
or registration fails after the swap, the previous installation is restored.

A full install replaces an old installation wholesale. Legacy manifests and mutable publish trees are
not adopted. If a process is running from the install directory, installation refuses and names it.
Run from a plain terminal with `--force true` only when you intentionally want to terminate those
processes; restart the agent afterward.

After one full new-format install, a compatible host-only update is available:

```powershell
.\install-dgspy.ps1 host-only
```

Host-only mode copies only files owned by the host, extension, and HookLab. It refuses a protocol
change before modifying the installation and preserves the CLI, Gateway, installer, and registration.

## Test

```powershell
.\tests\run-modernization-gate.ps1 -Stage CorDebug
```

To consume a package exactly as CI does:

```powershell
.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild `
  -LayoutRoot artifacts\packages\dgspy-win-x64\local\cli
```

GUI tests must run through `Invoke-OnHiddenDesktop.ps1`; see `AGENTS.md`.

CI builds the net10 package once and passes the identical verified artifact to CorDebug and all three
Mono/Unity jobs. The local Unity fixture is `C:\Users\mjb\develop\UCH-dev\uch-debug-target`.

## Layout invariants

- `dnSpy.exe` and `dnSpy.Console.exe` are at the layout root.
- Runtime assemblies and tools are under `bin`.
- The extension is under `bin\Extensions\dgSpy` and is named `dgSpy.Extension.x.dll`.
- HookLab payload files are under `hooklab`, outside every assembly scan path.
- `bin\dgSpy.Protocol.dll` and the extension copy are byte-identical.
- `dgspy-layout.json` is the authoritative complete file inventory.
- Package `manifest.json` hashes the layout manifest and records the complete package shape.

HookLab independently verifies its payload-specific manifest and the payload entry in
`dgspy-layout.json`. Developer build-output layouts are not supported.

## Silent failures

MEF composition can remove an extension part without a release log when imports are unsatisfied.
`tests\dgSpy.Composition.Tests` loads the completed layout and makes composition errors fatal. Always
run it against the packaged layout rather than a project `bin` directory.

Do not add project references from the extension to dnSpy implementation projects. The component graph
builds the extension last against the already-built host contracts, preventing project builds from
writing into the packaged host root.

## Upstream drift

Every intentional difference from the pinned dnSpyEx baseline must be accounted for:

```powershell
powershell -NoProfile -File tools\check-upstream-drift.ps1 -Revision HEAD -Report
```
