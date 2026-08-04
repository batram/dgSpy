# dgSpy build baseline (Phase 0)

## Scope

| | Current scope |
|---|---|
| Architecture | x64 only |
| dnSpy build | `net48`, Release |
| Debug engines | .NET Framework CorDebug (`CLR v4.0.30319`, covering 4.0–4.8) and Mono/Unity (UCH) |
| Out of scope for now | x86, CoreCLR, remote hosts, multi-session |

## Toolchain

- Windows 10 or later, x64.
- .NET SDK 7.0 or later (builds the gateway; also drives `dotnet build` for the other projects).
- .NET Framework 4.8 developer pack (extension and test target).
- MSBuild from a **full Visual Studio 2019** installation for the dnSpy baseline build — `build.ps1` uses MSBuild rather than `dotnet build` because dnSpy has COM references. Newer MSBuilds on this machine do not work; see [Build and deploy](#build-and-deploy) for the exact path and why picking your own fails.

## Projects

| Project | TFM | Notes |
|---|---|---|
| `dgSpy.Protocol` | `netstandard2.0` | DTOs only. Referenced by both sides, so it must stay loadable from net48 and net7.0. |
| `Extensions/dgSpy.Extension` | `net48` | Pinned; does not inherit `net5.0-windows` from `DnSpyCommon.props`. Output is `dgSpy.Extension.x.dll` — dnSpy's scanner only loads `*.x.dll`. |
| `dgSpy.Gateway` | `net7.0` | Standalone process, not loaded into dnSpy. |
| `tests/TestTargets/Milestone1Target` | `net48`, x64 | CorDebug smoke target; the project pins `PlatformTarget=x64` and the smoke test verifies dnSpy reports `X64`. |

The dgSpy projects are intentionally **not** in `dnSpy.sln`, and dgSpy builds through `build-dgspy.ps1`.
The deliberate upstream edits are recorded below because each is a modernization/rebase obligation.

### Edits to upstream dnSpy sources

Keep this list small: every line is a merge conflict with upstream and a behavior difference that must
survive or be proven obsolete during modernization.

| File | Change | Why |
|---|---|---|
| `dnSpy/dnSpy.Contracts.Debugger/DbgMessageEventArgs.cs` | `DbgMessageThreadExitedEventArgs` now assigns `ExitCode` in its constructor | Upstream accepts `exitCode` and drops it, so the property was always `null`. dgSpy reports thread exit codes in the `thread_exited` event and cannot synthesize what the ctor discarded. A one-line fix and a clean upstream PR candidate. |
| `Extensions/dnSpy.Debugger/Mono.Debugger.Soft` gitlink | Local commit `888ded0f` bounds `ThreadMirror.GetFrames()` to three seconds | A Unity thread can disappear while frames are requested and some runtimes never reply, wedging dnSpy's Mono debugger thread. Neither checked Mono nor dnSpyEx contains an equivalent bound. The commit must be pushed to a durable fork or reapplied during modernization. |
| `Extensions/dnSpy.Debugger/.../DbgUI/DgSpyWindowActivation.cs` | Adds the `--dgspy-no-window-activation` switch | Automated stops must not steal foreground focus from the user. |
| `Extensions/dnSpy.Debugger/.../DbgUI/DebuggerImpl.cs` and `WpfCurrentStatementUpdater.cs` | Honor the no-activation switch | Suppresses only foreground activation; dnSpy remains visible and interactive. |

## Build and deploy

```powershell
.\build.ps1 netframework
```

If MSBuild is installed but not on PATH, pass its resolved executable explicitly:

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\path\to\MSBuild.exe'
```

### Use this MSBuild. Nothing else on this machine works.

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\amd64\MSBuild.exe'
```

**Copy that line. Do not go looking for an MSBuild yourself** — this has now cost several sessions,
each one rediscovering the same dead end. Four MSBuild installations are present and only that one
builds this solution:

| MSBuild | Result |
|---|---|
| VS 2019 Community, `Bin\amd64` | ✅ the only verified one |
| VS 2022 BuildTools | ❌ `MSB4236` |
| VS 18 Community | ❌ `MSB4236` |
| bare `msbuild` on PATH | ❌ usually absent — `CommandNotFoundException` from `build.ps1`'s `Get-Command` |

The failure is always the same and always *before* any source compiles: `MSB4236` / `MSB4276`,
"The SDK 'Microsoft.NET.Sdk' specified could not be found", repeated for every project in the
solution. Those installations start fine but lack the .NET SDK resolver payload. **`MSB4236` here is a
toolchain-installation mismatch, never a dgSpy source failure** — do not start reading project files.

`vswhere -latest` is actively misleading: it returns the newest VS, which is one of the broken ones.
If the path above ever stops existing, enumerate and try each, rather than trusting a "latest" pick:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -all -products * -format value -property installationPath
```

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

- **`Newtonsoft.Json.dll` is missing from the deploy directory.** dnSpy does not ship it. The
  `LoadFrom` context probes the extension's own directory for dependencies, so it must sit beside
  `dgSpy.Extension.x.dll`. Without it the extension is dropped silently.
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
