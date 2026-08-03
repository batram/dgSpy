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
- MSBuild from Visual Studio 2019 or later for the dnSpy baseline build — `build.ps1` uses MSBuild rather than `dotnet build` because dnSpy has COM references.

## Projects

| Project | TFM | Notes |
|---|---|---|
| `dgSpy.Protocol` | `netstandard2.0` | DTOs only. Referenced by both sides, so it must stay loadable from net48 and net7.0. |
| `Extensions/dgSpy.Extension` | `net48` | Pinned; does not inherit `net5.0-windows` from `DnSpyCommon.props`. Output is `dgSpy.Extension.x.dll` — dnSpy's scanner only loads `*.x.dll`. |
| `dgSpy.Gateway` | `net7.0` | Standalone process, not loaded into dnSpy. |
| `tests/TestTargets/Milestone1Target` | `net48`, x64 | CorDebug smoke target; the project pins `PlatformTarget=x64` and the smoke test verifies dnSpy reports `X64`. |

The dgSpy projects are intentionally **not** in `dnSpy.sln`. The fork's own build stays exactly as upstream; dgSpy builds through `build-dgspy.ps1`.

## Build and deploy

```powershell
.\build.ps1 netframework
```

If MSBuild is installed but not on PATH, pass its resolved executable explicitly:

```powershell
.\build.ps1 netframework -MSBuildPath 'C:\path\to\MSBuild.exe'
```

The framework build clears its validated `dnSpy\dnSpy\bin\Release\net48` output before compiling and
then packages dependencies under `net48\bin`. This makes repeated builds idempotent; a second run must
not produce `net48\bin\bin`.

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
