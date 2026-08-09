# dgSpy build and deployment baseline

## Scope

| | Current scope |
|---|---|
| Architecture | x64 only |
| dnSpy build | `net10.0-windows` (default, and what ships), Release; `net48` retained as fallback |
| Debug engines | .NET Framework CorDebug (`CLR v4.0.30319`, covering 4.0–4.8) and Mono/Unity (UCH) |
| Out of scope for now | x86, debugging CoreCLR targets, clean-host remote acceptance, multi-session |

CoreCLR is out of scope as a *debuggee* runtime. It says nothing about the host: the shipping dnSpy
runs on net10 and debugs `CLR v4.0.30319` targets through CorDebug, which is a COM API and does not
require the debugger to share the target's runtime.

## Toolchain

- Windows 10 or later, x64.
- .NET SDK 10 (builds the modern host, gateway, and other SDK projects).
- .NET Framework 4.8 developer pack (extension and test target).
- MSBuild from a full Visual Studio installation, **only** for the retained net48 host. The default
  net10 build needs nothing beyond the .NET SDK.
  - The long-standing justification for this requirement was that the net48 host has COM references
    `dotnet build` cannot handle. That no longer matches the tree: the only `COMReference` is in
    `Extensions/ILSpy.Decompiler/.../ILSpy.AddIn/ILSpy.AddIn.csproj`, which is not in `dnSpy.sln` and
    is never built. Whether `build.ps1 netframework -NoMsbuild` (plain `dotnet build -f net48`)
    actually succeeds is untested; if it does, this dependency can be dropped entirely.

## Projects

| Project | TFM | Notes |
|---|---|---|
| `dgSpy.Protocol` | `netstandard2.0` | DTOs only. Referenced by both sides, so it must stay loadable from the retained net48 extension and the modern .NET host. It can move to `net10.0` once net48 is dropped. |
| `Extensions/dgSpy.Extension` | `net10.0-windows` (default), `net48` | The net10 target is the default everywhere and is what `pack-dgspy.ps1` ships; net48 is the retained fallback. Output is `dgSpy.Extension.x.dll` — dnSpy's scanner only loads `*.x.dll`. |
| `dgSpy.Cli` | `net10.0` | Console entrypoint, packaged with the Gateway in one shared self-contained runtime. |
| `dgSpy.Gateway` | `net10.0` | Standalone process, packaged with the CLI in one shared self-contained runtime. |
| `tests/TestTargets/NoPdbTarget` | `net48`, x64 | Built with `DebugType=none`. Shipped game assemblies almost never carry a PDB, so decompiled debug info is the normal case in the wild; this fixture keeps that path covered, including across a rebuild under one long-lived dnSpy. |
| `tests/TestTargets/Milestone1Target` | `net48`, x64 | Stays .NET Framework permanently: it is a *debuggee*, and CorDebug `CLR v4` is an in-scope engine that needs a Framework process to debug. CorDebug smoke target; the project pins `PlatformTarget=x64` and the smoke test verifies dnSpy reports `X64`. |

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
| Value cleanup tolerates sparse arrays and failing objects | `Extensions/dnSpy.Debugger/dnSpy.Debugger/Impl/DbgManagerImpl.cs` | Engine error paths return object arrays with null entries. Dereferencing one on the debugger thread stopped every later object from being closed and raised a modal `NullReferenceException` while the target was paused. `CloseObjects_DbgThread` now also isolates a `Close` that throws: one object's cleanup failing said nothing about the rest, but abandoned every object after it in the batch and faulted the dispatcher. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
| Raw metadata release-after-dispose | `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet/Metadata/Internal/DbgRawMetadataImpl.cs` | `Release` threw `ObjectDisposedException` on the runtime-teardown path, because `ForceDispose` had already marked the object disposed; that aborted the close batch above and faulted the dispatcher. Releasing an already-disposed object is now a no-op, and the free is deferred to the last reference. **The deferral does not fix the upstream use-after-free that shares this file** — that `AccessViolationException` is still open and has been measured to survive this change. See `docs/local/dnspy-raw-metadata-use-after-free.md`. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
| Dispatcher can report that it is gone | `dnSpy/dnSpy.Contracts.Debugger/DbgDispatcher.cs`; `Extensions/dnSpy.Debugger/dnSpy.Debugger/Impl/DbgDispatcherImpl.cs`; `Extensions/dnSpy.Debugger/dnSpy.Debugger/Shared/Dispatcher.cs` | `BeginInvoke` silently discards work once the dispatcher shuts down and cannot say so, so every RPC awaiting a result waited out its full deadline instead of failing. `TryBeginInvoke` enqueues and reports in one step, and `IsShutdown` distinguishes a dead dispatcher from one that merely recorded a contained fault. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
| Aggregate child paging | `dnSpy/Roslyn/dnSpy.Roslyn/Debugger/ValueNodes/AggregateValueNodeProvider.cs` | `index - childCount` is unsigned and wrapped to a negative provider index whenever a requested page started inside `providers[0]`, so any page spanning that provider's tail into the extra providers (`Static members`, `Raw View`) threw `IndexOutOfRangeException`. Upstream bug; fix is a corrected start index. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
| Diagnosable child-expansion failures | `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet/Evaluation/Engine/DbgEngineValueNodeImpl.cs`; `dnSpy/dnSpy.Contracts.Debugger/Evaluation/DbgValueNodeExpansionException.cs` (new); `Extensions/dnSpy.Debugger/dnSpy.Debugger/Evaluation/ViewModel/Impl/ExpansionErrorValueNode.cs` (new); `.../ViewModel/Impl/DbgValueNodeReader.cs` | Upstream answered every expansion failure with `count` copies of an error node carrying the localized string "Internal debugger error" against the literal expression `<expression>`, discarding the exception. Two fixes. **Content**: the message now carries the exception type/message and the parent expression, and the full exception goes to debugger output. **Shape**: `GetChildrenCore` throws `DbgValueNodeExpansionException` instead of fabricating a page. A page of placeholders is harmless in a treeview but is fabricated data over RPC — `get_members` handed back N rows that look like members, so nothing could tell "this object has N broken members" from "expansion failed once". `DbgValueNode.GetChildren` has exactly three consumers: the GUI's two `DbgValueNodeReader` call sites, which ask for one child and now build a single `ExpansionErrorValueNode` row (identical text, name and image to the old fabricated node, so GUI behaviour is unchanged), and `RpcHost.GetMembersAsync`, which raises one `evaluation_failed`. | GPLv3; see `dnSpy/dnSpy/LicenseInfo/GPLv3.txt`. |
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

### Default: the net10 host that ships

This is what `pack-dgspy.ps1` packages, what `install-dgspy.ps1` installs, and what
`launch_local_host` runs. `build-dgspy.ps1`, `tests\run-milestone1-smoke.ps1` and
`tests\run-modernization-gate.ps1` all target it by default, so the live smoke proves the build users
actually get. No Visual Studio installation is required.

```powershell
.\build.ps1 net-x64 -NoMsbuild
.\tests\run-modernization-gate.ps1 -Stage CorDebug
```

### Retained fallback: the net48 host

net48 stays fully supported behind `-TargetFramework net48` on all three scripts, and CI runs the
net48 CorDebug smoke on every commit, alongside the net10 one, so the fallback cannot rot unnoticed
and a break is attributed to the commit that caused it. It is still the only
host with Unity/UCH acceptance evidence, so the gate refuses `-Stage Unity` and `-Stage Full` unless
`-TargetFramework net48` is passed.

```powershell
.\build.ps1 netframework
.\tests\run-modernization-gate.ps1 -Stage CorDebug -TargetFramework net48
```

If MSBuild is installed but not on PATH, pass its resolved executable explicitly:

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\path\to\MSBuild.exe'
```

#### Verified net48 build

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
```

`build.ps1` supplies Visual Studio 18 with the installed SDK resolver path. If that installation path
changes, discover the full Visual Studio installations and pass the appropriate `MSBuild.exe`:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -format value -property installationPath
```

The net10 host publishes through the SDK-native driver because full-framework MSBuild 18 cannot load
the SDK 10 `Microsoft.Deployment.DotNet.Releases` task dependency during this publish; that is a
build-driver limitation, not a source failure. Neither driver can produce both target frameworks,
which is why `build.ps1` has two modes.

`pack-dgspy.ps1` starts with the self-contained dnSpy tree, then merges the CLI and Gateway publishes
into its `cli\bin` runtime directory. All three applications therefore ship one .NET 10 runtime.
Duplicate files must have identical SHA-256 hashes except for the explicit Windows Desktop framework
variants retained from dnSpy at the same .NET servicing version. Packaging fails on every unknown or
version-mismatched collision.

With no switches, `pack-dgspy.ps1` remains the release-engineering command: it writes the optimally
compressed portable archive `artifacts\dgspy\dgspy-win-x64.zip`. `-CompressionLevel Fastest` and
`-CompressionLevel NoCompression` are available for explicit ZIP experiments, but do not change the
default. `install-dgspy.ps1` from a repository checkout instead calls the packer with
`-DirectoryPackage` and publishes `artifacts\dgspy-local\dgspy-win-x64`; this avoids a local ZIP
round-trip while retaining a complete package that a failed install can reuse through the printed
`-PackagePath` recovery command. `-PackagePath` accepts either a release ZIP or a complete package
directory. Both formats carry the same `cli` payload and manifest verification fields.

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
| `Extensions\dnSpy.Debugger\...\DbgUI\DebuggerImpl.cs` | 1 changed condition in `AppWindow_MainWindowClosing`: no "stop debugging?" prompt under the switch. |

`--dgspy-no-window-activation` stops dnSpy pulling itself to the foreground on every debugger stop,
which otherwise steals focus from whatever the user is typing into on each breakpoint hit. It
suppresses the foreground grab and the modal "stop debugging?" prompt on window close; the window
still opens, still navigates to source, and keeps every command, so a user can take over an automated
session by clicking on it.

The close prompt belongs here for the same reason as the foreground grab: nobody is watching a
headless host, so the question is never answered, the close never completes, and the process stays up
holding its attachment. That also blocks the extension's `AppExit` handler, which is what detaches
targets before the process dies — so leaving the prompt in place strands the target rather than
protecting it.

Every dgSpy launcher passes the switch: the gateway's `LaunchLocalAsync`, the remote-host launcher,
`run-milestone1-smoke.ps1`, `run-launch-output-smoke.ps1`, `run-remote-registration-smoke.ps1` and
`Start-DgSpyHost.ps1`. The gateway
was the one that did not, so the host an agent actually uses was the only one still grabbing focus.

Nothing outside the process can substitute for this. `-WindowStyle Hidden` sets only the initial show
state, and an external hide-or-restyle loop is permanently racing code inside the app that owns the
window — it loses by however long its poll interval is, which is what the user sees as a flicker.

## Host lifecycle

**A host that exits takes its debuggee with it.** Verified directly, not inferred: attach to a
throwaway target, kill the dnSpy process, and the target dies in the same second. ICorDebug leaves
kill-on-exit at its default and nothing in the managed API lets the right side clear it, so an
attachment that is still live when the host process ends destroys the process being debugged. Nothing
logs it. From the agent's side a target simply vanishes, which is why this was suspected for a long
time before it was tested.

**The host's own crash-on-detach is closed.** An upstream use-after-free freed native module
metadata on runtime teardown while Roslyn was still reading it, killing the process with an
uncatchable `AccessViolationException` — and, per the paragraph above, its debuggee with it. Two
refcount-based mitigations were measured to fail; the carried fix quiesces instead: teardown only
marks the raw metadata disposed, and the free is posted to the engine's evaluation dispatcher — the
one thread every metadata reader runs on — so a read and a free can no longer overlap. Finalization
is suppressed before posting; a post dropped during dispatcher shutdown deliberately leaves its
small native allocation for process exit because finalizer-thread reclamation cannot prove that the
last engine-thread read has quiesced. Verified at
zero new `.NET Runtime` 1026 records across 12 consecutive CorDebug gate runs; full account and the
mechanism argument in `docs/local/dnspy-raw-metadata-use-after-free.md`. If a host nonetheless
vanishes shortly after a detach, check the Windows Application log for a 1026 record naming
`CompileGetLocals` before looking anywhere else.

Everything else in this section follows from that:

- The extension detaches every target on `ExtensionEvent.AppExit` (`RpcHost.DetachTargetsBeforeExit`),
  so an orderly close of dnSpy is safe. That path only exists because the "stop debugging?" prompt is
  suppressed under `--dgspy-no-window-activation` — see the section above.
- `launch_local_host` with `replace=true` detaches the running host's sessions over RPC before it
  closes the process, and refuses outright when a target cannot be detached without killing it.
- A hard kill of the host is still fatal to the target, and always will be. Nothing running inside the
  host survives its own termination. Do not kill a host to recover it; detach first.

**Exactly one host owns the RPC endpoint.** `launch_local_host` never starts a second dnSpy beside a
running one. It adopts a host already running the installed payload, and otherwise fails with
`host_already_running` naming both builds. Two hosts contending for the endpoint is the worst state
available: the second process composes, finds the port taken, and every answer keeps coming from the
superseded build while the call reports success. It is indistinguishable from a working host until
results start disagreeing with the tree. Two things enforce that beyond the process scan, because the
scan only sees dnSpy under the managed install root: `RunningManagedHosts` resolves image paths with
`QueryFullProcessImageName` rather than `Process.MainModule`, which is refused across an integrity
boundary and would hide an elevated host from the very check meant to find it; and the launch refuses
outright when something already accepts connections on `127.0.0.1:7351`, which catches a host installed
somewhere else entirely.

**An elevated host is opt-in, and its elevation is reported rather than assumed.** dnSpy's manifest is
`asInvoker`, so a host inherits the Gateway's integrity level and cannot see processes owned by other
users or by services. `launch_local_host` with `elevated=true` starts it through ShellExecute with the
`runas` verb; a dismissed User Account Control prompt fails with `elevation_declined` rather than
reporting a host that does not exist. ShellExecute cannot carry an environment block, so the elevated
child computes its own state root, identity, credential and port — the launch is therefore refused with
`elevated_launch_unsupported_environment` when the Gateway uses a non-default `DGSPY_STATE_ROOT` or has
`DGSPY_HOST_ID`, `DGSPY_RPC_TOKEN` or `DGSPY_RPC_PORT` set, because the two sides would otherwise
disagree about where to meet. Adoption is not a shortcut around this: a running host that matches the
payload but is not provably elevated fails with `host_not_elevated` or `host_elevation_unknown` instead
of being adopted, since the caller cannot check that claim afterwards and would find out only when a
target stayed invisible. The gateway↔host hop is a loopback socket with a shared token, so a
medium-integrity Gateway talks to an elevated host without any further arrangement.

Reading an elevated host's identity is permitted; ending its process is not. `replace=true` from an
unelevated Gateway therefore fails with `replace_requires_elevation` *before* it detaches anything —
the natural place to discover the refusal is `Process.Kill`, by which point the replacement has already
detached every session to make the close safe, so the caller would have lost the debugging state and
still be looking at the host they asked to replace. Close the elevated dnSpy by hand instead. Only a
proven elevated host blocks; an unreadable token falls through to the existing failure path rather than
refusing replacements on a machine where the query is unavailable.

**A dispatcher fault is not the same as a dead dispatcher.** `dispatcher_state` is `healthy`,
`faulted` (a debugger-thread callback failed and was contained; the host still works) or `unavailable`
(the debugger thread is gone). Only the last one ends the host, and it is reported as such:
`OnDebuggerAsync` fails immediately with `dispatcher_unavailable` rather than letting every operation
that needs the debugger thread wait out its full deadline. A silently discarded callback is how a host
stayed registered while being unusable — reads answered from cached state, control operations just
stopped responding, and nothing said which of the two you were looking at.

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
