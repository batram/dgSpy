# dgSpy build and deployment baseline

## Supported scope

- Windows x64 host.
- Self-contained `net10.0-windows` dnSpy/dgSpy package.
- CLR v4 and CoreCLR CorDebug targets, plus Mono/Unity debug targets.
- HookLab supports CLR v4 and CoreCLR residents through separate explicit backends; its shared payload
  remains `net48` internally and carries runtime-specific compiler and patch-engine assets.
- x86 targets and the old net48 dnSpy host are out of scope.

## Prerequisites

- .NET SDK 10.
- .NET Framework 4.8 developer pack for CLR v4 fixtures and HookLab payload compilation.
- An installed matching x64 runtime for the ordinary CoreCLR debugger and HookLab fixtures.
- Visual C++ x64 build tools for `HookLab.NativeBootstrap`. DgSpyTool locates them through
  `vswhere` (any Visual Studio edition with the VC x86/x64 toolchain component), with a hardcoded
  path fallback.
- Recursive Git submodules.

```powershell
git submodule update --init --recursive
```

## One build path

```powershell
dotnet run --project Build\DgSpyTool -- pipeline
```

Run restore, build, test, and pipeline commands under the normal Windows identity from the first
attempt. The Codex sandbox blocks NuGet HTTPS requests and may not be able to overwrite `bin`/`obj`
intermediates produced by the normal identity. A socket-denied `NU1301` or cross-identity access
failure is an execution-environment failure; rerun the unchanged command under the normal identity.
Do not weaken NuGet verification or add an offline dependency path for this repository.

The component build restores in a separate MSBuild evaluation pass (`dotnet msbuild /restore`).
Do not reintroduce a Restore dependency inside the Build invocation: on a fresh checkout, projects
evaluated before NuGet writes their assets keep the empty evaluation and fail with a `CS0518`
cascade.

The completed outputs are:

- `artifacts\host-raw\local`: immutable host compiler artifact.
- `artifacts\dgspy-components\local`: immutable dgSpy component artifact.
- `artifacts\layouts\local`: verified runnable layout.
- `artifacts\packages\dgspy-win-x64\local`: verified installable package.

The pipeline never deploys into a compiler output directory. Composition starts in fresh staging,
records every file's size, SHA-256, and owner, verifies the complete inventory, and publishes by
directory rename. Failed composition or installation leaves the previous completed tree available.

The verified layout also contains the standalone HookLab watcher under `hooklab-watcher`, including its
unelevated `HookLab.Watcher.Companion.exe` notification-area operator surface, closed deployments, and
native/managed bootstrap payloads. Install its per-user scheduled operation only
from that layout:

```powershell
.\artifacts\layouts\local\hooklab-watcher\HookLab.Watcher.exe install
```

See [HookLab watcher operation](../guides/HOOKLAB_WATCHER.md) for verification, control, upgrade, and uninstall behavior.

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
Run from a plain terminal with `--force` only when you intentionally want to terminate those
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

The gate's `Shared` stage runs the HookLab resident compatibility probe before anything that needs a
GUI. It drives a full resident lifecycle against real CLR v4 and CoreCLR targets in seconds and fails
with a named stage and payload identity, so a wrong runtime asset stops the gate early instead of
surfacing as a timeout inside a live leg. Run it alone with:

```powershell
dotnet run --project tests\HookLab.CompatibilityProbe -c Release -- --negative
```

It reads the payload from `DGSPY_LAYOUT_ROOT` or the newest composed layout. See
[Resident compatibility probe](../product/HOOKLAB.md#resident-compatibility-probe) for what it proves
and, just as importantly, what it leaves to the packaged live gates.

To consume a package exactly as CI does:

```powershell
.\tests\run-modernization-gate.ps1 -Stage CorDebug -SkipHostBuild `
  -LayoutRoot artifacts\packages\dgspy-win-x64\local\cli
```

GUI tests must run on a hidden desktop so no window can steal focus; use the hidden-desktop helper
from your agent environment (the script is not part of this repository).

Wrapping the gate in that helper is not enough for the two cross-identity legs. They run from Task
Scheduler, and the desktop a process lands on is chosen by whatever calls `CreateProcess` - the
scheduler, not the wrapper - so they arrive on the interactive desktop and dnSpy appears on screen.
`-WindowStyle Hidden` on the task hides only its PowerShell console; WPF ignores it. Point the legs at
the launcher instead, and `Invoke-CrossIdentitySmokeTask.ps1` relaunches the smoke on a hidden desktop
from inside the task:

```powershell
$env:DGSPY_HIDDEN_DESKTOP_LAUNCHER = 'C:\path\to\Invoke-OnHiddenDesktop.ps1'
```

With the variable unset the legs still pass; they are simply visible. The request file records
`hidden_desktop_launcher: null`, which is the thing to check when a gate run flashes a dnSpy window.

One dialog escapes a hidden desktop no matter what. `StartUpClass.AskReadSettings` - "Do you want to
load the saved settings?", shown when Shift is held as dnSpy starts - uses
`MessageBoxOptions.DefaultDesktopOnly`, which is `MB_DEFAULT_DESKTOP_ONLY` and puts the box on the
interactive window station's default desktop. It is upstream dnSpyEx code and deliberate there. Seeing
it therefore proves nothing about which desktop dnSpy is on, either button is safe, and it needs a held
Shift, so it is rare.

CI builds the net10 package once and passes the identical verified artifact to CorDebug and all three
Mono/Unity jobs. The local Unity fixture is `C:\Users\mjb\develop\UCH-dev\uch-debug-target`.

## Layout invariants

- `dnSpy.exe` and `dnSpy.Console.exe` are at the layout root.
- Runtime assemblies and tools are under `bin`.
- The extension is under `bin\Extensions\dgSpy` and is named `dgSpy.Extension.x.dll`.
- HookLab payload files are under `hooklab`, outside every assembly scan path.
- The independently runnable watcher and its closed packages/payloads are under `hooklab-watcher` and
  are owned as `hooklab-watcher` in the complete layout manifest.
- `bin\dgSpy.Protocol.dll` and the extension copy are byte-identical.
- `dgspy-layout.json` is the authoritative complete file inventory.
- Package `manifest.json` hashes the layout manifest and records the complete package shape.

HookLab independently verifies its payload-specific manifest and the payload entry in
`dgspy-layout.json`. Developer build-output layouts are not supported.

`hooklab\hooklab-payload-manifest.json` describes the payload file the host opens.
`hooklab\hooklab-payload-matrix.json` describes what is inside it: every resident payload's role,
carrier, runtime family, framework, architecture, assembly identity, provenance, dependencies, and
digest. `compose`, `verify`, `package`, and `verify-package` all re-read the matrix out of the packaged
payload and prove it against that payload's own bytes, in both directions - every declared slot must be
present at the declared identity and digest, and every embedded payload resource must be declared. A
payload added to the bootstrap or to the resident probe without a matrix entry fails package
verification rather than shipping unnoticed. See
[Resident payload matrix](../product/HOOKLAB.md#resident-payload-matrix) for what the matrix states and
what it deliberately does not.

## A stale nested publish bin ships as bin/bin

`dotnet publish` never removes files it no longer produces. The whole publish directory becomes the
layout's `bin`, so a `bin` left inside the publish output by an older layout scheme becomes `bin/bin`
and ships: a second copy of every host contract and extension assembly, surviving every rebuild.

Found on 2026-08-20 carrying 866 files, some dated 2017. It was invisible for as long as the duplicates
stayed interface-compatible. When a host contract interface gained one method, the stale extension
beside the fresh one still implemented the old interface, its `[Export]` lost its match, and the whole
part - plus everything that imported it - disappeared from MEF composition without a log line.

`build-host` now refuses a publish directory containing a nested `bin`, and `verify` refuses a layout
containing `bin/bin`. If either fires, delete the named directory and build again; on a clean clone
neither exists.

## Silent failures

MEF composition can remove an extension part without a release log when imports are unsatisfied.
`tests\dgSpy.Composition.Tests` loads the completed layout and makes composition errors fatal. Always
run it against the packaged layout rather than a project `bin` directory.

Do not add project references from the extension to dnSpy implementation projects. The component graph
builds the extension last against the already-built host contracts, preventing project builds from
writing into the packaged host root.

For packaged builds, `DgSpyTool` passes the immutable `host-raw\<build-id>\content\bin` directory as
`DgSpyHostContractsRoot`. The extension then uses explicit file references for dnSpy contracts and
their host compile dependencies; its dgSpy and HookLab dependencies remain normal project references.
Do not restore a global `BuildProjectReferences=false` workaround: it hides the real dependency graph
and suppresses owned project dependencies along with upstream ones.

## Upstream drift

Every intentional difference from the pinned dnSpyEx baseline must be accounted for:

```powershell
powershell -NoProfile -File tools\check-upstream-drift.ps1 -Revision HEAD -Report
```
