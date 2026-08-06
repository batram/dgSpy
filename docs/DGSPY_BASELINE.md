# dgSpy build and deployment baseline

## Scope

| | Current scope |
|---|---|
| Architecture | x64 only |
| dnSpy build | `net48` and `net10.0-windows`, Release |
| Debug engines | .NET Framework CorDebug (`CLR v4.0.30319`, covering 4.0–4.8) and Mono/Unity (UCH) |
| Out of scope for now | x86, CoreCLR, clean-host remote acceptance, multi-session |

## Toolchain

- Windows 10 or later, x64.
- .NET SDK 10 (builds the modern host, gateway, and other SDK projects).
- .NET Framework 4.8 developer pack (extension and test target).
- MSBuild from a full Visual Studio installation for the net48 host, whose COM references are not supported by `dotnet build`.

## Projects

| Project | TFM | Notes |
|---|---|---|
| `dgSpy.Protocol` | `netstandard2.0` | DTOs only. Referenced by both sides, so it must stay loadable from net48 and the modern .NET host. |
| `Extensions/dgSpy.Extension` | `net48`, `net10.0-windows` | The net48 target remains the local baseline; the net10 target is packed with the self-contained remote host. Output is `dgSpy.Extension.x.dll` — dnSpy's scanner only loads `*.x.dll`. |
| `dgSpy.Cli` | `net10.0` | Console entrypoint, packaged with the Gateway in one shared self-contained runtime. |
| `dgSpy.Gateway` | `net10.0` | Standalone process, packaged with the CLI in one shared self-contained runtime. |
| `tests/TestTargets/Milestone1Target` | `net48`, x64 | CorDebug smoke target; the project pins `PlatformTarget=x64` and the smoke test verifies dnSpy reports `X64`. |

The dgSpy projects are intentionally **not** in `dnSpy.sln`, and dgSpy builds through `build-dgspy.ps1`.
The two deliberate upstream patch sets are recorded below because each is a modernization/rebase
obligation. The dgSpy superproject is distributed under GPLv3, matching the inherited dnSpy license.
The patched `Mono.Debugger.Soft` sources retain their permissive MIT-style source notices; for example,
see `Locale.cs` and `Properties/AssemblyInfo.cs` in that fork.

### Edits to upstream dnSpy sources

Keep this list small: every line is a merge conflict with upstream and a behavior difference that must
survive or be proven obsolete during modernization.

| Patch set | Files | Change and reason | License |
|---|---|---|---|
| dgSpy changes to dnSpy | `dnSpy/dnSpy.Contracts.Debugger/DbgMessageEventArgs.cs`; `Extensions/dnSpy.Debugger/.../DbgUI/DgSpyWindowActivation.cs`; `DebuggerImpl.cs`; `WpfCurrentStatementUpdater.cs` | Preserve process/thread exit codes for lifecycle events and add the opt-in `--dgspy-no-window-activation` behavior needed by a headless debugger host. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
| Bounded Mono frame fetch | `Extensions/dnSpy.Debugger/Mono.Debugger.Soft` gitlink at `888ded0f` | Bounds `ThreadMirror.GetFrames()` to three seconds. A Unity thread can disappear while frames are requested and some runtimes never reply, wedging dnSpy's Mono debugger thread. Neither checked Mono nor dnSpyEx contains an equivalent bound. | MIT-style Mono source notices retained in the fork; see `Locale.cs` and `Properties/AssemblyInfo.cs`. |

## Reproducible source baseline

- Superproject: `https://github.com/batram/dgSpy.git`.
- Patched submodule: `https://github.com/batram/Mono.Debugger.Soft.git`, branch `dgspy`, commit
  `888ded0f0284500b32c94abc5595410342c28810`.
- Last known-good pre-modernization tag: `pre-modernization-2026-08-04`.
- Historical acceptance evidence: [status ledger](history/DGSPY_STATUS.md), [Unity checklist](history/DGSPY_UNITY_CHECKLIST.md),
  and the build/test commands in this document.

A fresh checkout must not borrow objects from an existing clone:

```powershell
git clone --depth 1 --recurse-submodules --shallow-submodules https://github.com/batram/dgSpy.git
```

## Build and deploy

By default the extension persists its stable identity and RPC credential in
`%LOCALAPPDATA%\dgSpy\host.id` and `%LOCALAPPDATA%\dgSpy\rpc.token`. Managed hosts may set
`DGSPY_HOST_ID` and `DGSPY_RPC_TOKEN` for both dnSpy and the gateway. `DGSPY_TOKEN` remains the separate
client-to-gateway MCP credential.

```powershell
.\build.ps1 netframework
```

If MSBuild is installed but not on PATH, pass its resolved executable explicitly:

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\path\to\MSBuild.exe'
```

### Verified net48 build

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
```

`build.ps1` supplies Visual Studio 18 with the installed SDK resolver path. If that installation path
changes, discover the full Visual Studio installations and pass the appropriate `MSBuild.exe`:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -format value -property installationPath
```

Build the self-contained modern x64 host through the SDK-native driver. Full-framework MSBuild 18
cannot load the SDK 10 `Microsoft.Deployment.DotNet.Releases` task dependency during this publish;
that is a build-driver limitation, not a source failure.

```powershell
.\build.ps1 net-x64 -NoMsbuild
```

`pack-dgspy.ps1` starts with the self-contained dnSpy tree, then merges the CLI and Gateway publishes
into its `cli\bin` runtime directory. All three applications therefore ship one .NET 10 runtime.
Duplicate files must have identical SHA-256 hashes except for the explicit Windows Desktop framework
variants retained from dnSpy at the same .NET servicing version. Packaging fails on every unknown or
version-mismatched collision.

Build the deploy-only remote host archive on the development machine:

```powershell
.\pack-remote-host.ps1 -HostId 'win11-clean' -GatewayAddress '192.168.250.1'
```

Add `-UseTls -GatewayTlsPort 7353` for a pinned mutual-TLS package. Omitting `-UseTls` deliberately
keeps the authenticated plaintext deployment option.

This publishes `artifacts\remote-host\dgSpy-remote-host-win11-clean-win-x64.zip`. It contains the self-contained
net10 x64 dnSpy host, the matching dgSpy extension and its private dependencies, a process-scoped
launcher, and `manifest.json` with a stable sorted SHA-256 inventory. The Gateway is intentionally not
included. On the remote machine, extract the archive and run:

```powershell
.\launcher\Start-dgSpyRemoteHost.cmd
```

The launcher creates `state\host.id` and `state\rpc.token` inside the extracted bundle and reuses them
across restarts. It changes environment variables only for dnSpy's child process; it does not install a
runtime or redistributable, modify PATH or the registry, add a service/firewall rule, or write
machine-wide configuration. Copy `state\rpc.token` through the administrative channel when configuring
the central Gateway. Use `-InitializeOnly` to create or inspect the paths without starting dnSpy.
Removing the extracted directory removes the host and its state.

Validate an archive's complete hash inventory and launcher-state persistence with
`.\tests\verify-remote-host-package.ps1 -ArchivePath <zip>`. Run the local reverse-registration
acceptance with `.\tests\run-remote-registration-smoke.ps1`; add `-UseTls` to exercise TLS. It deploys a package, launches a CorDebug
target through the outbound connection, restarts the Gateway, proves the same session and event cursor
remain available, terminates the target, and removes the deployment.

Run `.\tests\run-gateway-control-smoke.ps1` for the real HTTP boundary: MCP controller issuance,
ownership-tool discovery, inspect-only denial, and redacted audit output.

### Do not substitute `dotnet build` for the dnSpy baseline

`build.ps1` uses MSBuild because dnSpy has COM references, and that is not the only reason to leave it
alone. `dotnet build` on `dnSpy\dnSpy\dnSpy.csproj` *succeeds* and then leaves the packaged output
flattened — `net48\bin\` gone, dependencies loose in `net48\` — which is not a layout dnSpy can load
extensions from. It looks like a working build and is not one. Recovery is simply to run the documented
`build.ps1` line again: it clears and repackages, so the damage is not sticky.

The framework build clears its validated `dnSpy\dnSpy\bin\Release\net48` output before compiling and
then packages dependencies under `net48\bin`. This makes repeated builds idempotent; a second run must
not produce `net48\bin\bin`. It also wipes the deployed extension, so `build-dgspy.ps1` has to run
after it, not before.

```powershell
.\build-dgspy.ps1
```

The second command builds the three dgSpy projects and copies the extension into
`dnSpy\dnSpy\bin\Release\net48\bin\Extensions\dgSpy\`. In the packaged net48 layout,
`AppDirectories.BinDirectory` is the dependency `bin` containing `dnSpy.Contracts.DnSpy.dll`; dnSpy
scans that directory and its `Extensions` children for
`*.x.dll` at startup, so no registration step is needed. Use `-DnSpyDir <path>` to deploy into an
installed dnSpy instead of the build output.

On startup, dnSpy's Output window reports the loopback endpoint. If that line is missing, the
extension did not load. The two things that actually cause this:

- **A `System.Text.Json` compatibility assembly is missing from the deploy directory.** The net48
  extension loads its package-pinned JSON dependency set from beside `dgSpy.Extension.x.dll`.
  `build-dgspy.ps1` copies that explicit set; without it the extension can be dropped silently.
- **A stale copy of `dgSpy.Extension.x.dll` in dnSpy's bin directory.** dnSpy scans both the bin
  directory and `Extensions\*`, so a leftover copy is composed twice. `build-dgspy.ps1` removes any
  before deploying.

The inverse also holds: dnSpy's *own* contract assemblies must **not** be copied into the deploy
directory. `dgSpy.Extension` references `dnSpy.Contracts.Debugger.DotNet.Mono` (for `attach_endpoint`),
and MSBuild puts it in the extension's build output — but `build-dgspy.ps1` copies an explicit file
list rather than the whole directory, so it is left behind. A second copy would load into the
`LoadFrom` context and its types would not match the ones dnSpy already has.

## MEF composition fails silently, and it looks exactly like a bad build

**The build is deterministic. It does not need to be run twice.** When something is "missing" from
dnSpy after a change, suspect MEF before suspecting the build.

A part whose imports cannot be satisfied is not an error in dnSpy — it simply does not exist. Nothing
is logged, no dialog appears, and everything that imported it disappears too. The symptom is UI that
is quietly *reduced*: menu entries gone, a toolbar missing buttons, a tool window empty. That reads
as "the build left something out", which sends you into the build system, which is the wrong place.

Observed on 2026-08-04, and the reason this section exists: adding `IAppCommandLineArgs` to
`DebuggerImpl`'s `[ImportingConstructor]` emptied dnSpy's whole Debug menu — `Start Debugging`,
`Attach to Process...`, `Toggle Breakpoint`, `Options...` all vanished, leaving only `Windows` and
`Attach to Process (Unity)...`. `DebuggerImpl` exports `Debugger`, every one of those commands imports
`Debugger`, and the import chain broke at the root. Two full clean rebuilds "reproduced" it, which
looked like build flakiness and was not: the build was faithfully compiling broken code.

Two rules follow:

- **`IAppCommandLineArgs` is not a MEF export.** Nothing imports it; `App` threads it through by hand.
  Do not put it in an `[ImportingConstructor]`. To read a command-line switch from inside an
  extension, read `Environment.GetCommandLineArgs()` — see `DbgUI\DgSpyWindowActivation.cs`.
- **Before adding any constructor import to a dnSpy part, confirm the type is actually exported**
  (`[Export(typeof(T))]`, or an existing `[ImportingConstructor]` that already takes it). A type being
  public and obviously available is not evidence.

**The automated suites cannot catch this.** dgSpy's extension talks to `DbgManager` directly, so the
RPC surface, the smoke script and all three unit suites stayed green the entire time the Debug menu
was broken. UI composition regressions in the fork are only visible by looking at the window.

## Window-activation compatibility patch

The complete upstream-edit inventory is above. These details explain the UI-specific part of it.

| File | Change |
|---|---|
| `Extensions\dnSpy.Debugger\...\DbgUI\DgSpyWindowActivation.cs` | New. Reads `--dgspy-no-window-activation` from the process command line. |
| `Extensions\dnSpy.Debugger\...\DbgUI\DebuggerImpl.cs` | 5 added lines: early return in `ActivateWindow_UI`. |
| `Extensions\dnSpy.Debugger\...\DbgUI\WpfCurrentStatementUpdater.cs` | 3 added lines: early return in `ActivateMainWindow_UI`. |

`--dgspy-no-window-activation` stops dnSpy pulling itself to the foreground on every debugger stop,
which otherwise steals focus from whatever the user is typing into on each breakpoint hit. It
suppresses *only* the foreground grab: the window still opens, still navigates to source, and keeps
every command, so a user can take over an automated session by clicking on it. All dgSpy launchers
pass it — `run-milestone1-smoke.ps1` and `ps_scratch\Start-DnSpyPhase6Uch.ps1`.

Nothing outside the process can substitute for this. `-WindowStyle Hidden` sets only the initial show
state, and an external hide-or-restyle loop is permanently racing code inside the app that owns the
window — it loses by however long its poll interval is, which is what the user sees as a flicker.

## Thread-affinity rules

These rules govern everything the extension does with dnSpy objects. They belong with the RPC
dispatcher abstraction, and violating them is the most likely source of hangs.

1. **`DbgManager.Dispatcher` is the debugger thread, not the WPF thread.** All `DbgManager` events
   are raised on it, and `DbgObject` instances (processes, threads, frames, value nodes) may only be
   touched there.
2. **RPC threads never touch dnSpy objects directly.** Every access goes through the dispatcher
   helper, which marshals via `Dispatcher.BeginInvoke`.
3. **Nothing else may run on the dispatcher thread.** While it is blocked, no debugger event is
   delivered, so a blocked dispatcher stalls every session at once — including `wait_for_stop`
   waiters on other connections. Specifically:
   - JSON serialization and socket writes must resume on a thread-pool thread. A
     `TaskCompletionSource` completed on the dispatcher runs its continuations inline unless it is
     created with `TaskCreationOptions.RunContinuationsAsynchronously`.
   - Expression evaluation and value formatting block on the engine's own thread, and once func-eval
     is enabled they run code inside the target. They therefore run on `EvaluationQueue` — one
     dedicated non-dispatcher thread — mirroring dnSpy, which evaluates on its UI thread. A single
     thread keeps evaluations serialized, which the object model wants anyway.
   - The split is: read identity (`DbgObject` properties) on the dispatcher, evaluate off it. Because
     the dispatcher is then free to process a resume mid-read, an evaluation must check
     `DbgObject.IsClosed` and fail with `stale_handle` rather than patch up a torn snapshot.
4. **Frames and value nodes are valid only while paused** and only for the state version they were
   captured under. Close them with `DbgManager.Close`, which marshals the close to the dispatcher
   for you.
5. **`AttachableProcessesService.GetAttachableProcessesAsync` is not a dispatcher call.** It is
   awaited on the RPC thread. Called without a provider filter it runs Unity's UDP multicast
   discovery, which costs roughly two seconds per enumeration.

## Engine notes

- **CorDebug / .NET Framework**: the runtime's durable discriminator is the CLR version string
  (`v4.0.30319`). `RuntimeId` has no meaningful `ToString()` — it implements only `Equals` and
  `GetHashCode`, so identities must be composed from typed fields.
- **Mono / Unity**: attach happens over a Mono soft-debugger endpoint discovered by UDP multicast;
  the discriminator is address plus port, and the PID reported can belong to another machine.
- Both engines report the same `RuntimeKindGuid` (`DotNet_Guid`). They are distinguishable only by
  `RuntimeGuid` (`DotNetFramework_Guid` vs `DotNetUnity_Guid`).
