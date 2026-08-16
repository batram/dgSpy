# dnSpy CoreCLR debugger TODOs

## Scope

These are upstream dnSpy CoreCLR defects observed while attaching to Barnyard, not defects in the
documented dgSpy CLR v4 / Mono support boundary. CoreCLR remains outside the current dgSpy gate.

The two items below are independent changes. Fixing runtime-library resolution makes attach possible;
it does not fix a target left suspended by managed-callback processing.

## TODO 1: resolve single-file CoreCLR debugger libraries by identity

### Observed behavior

Attaching dnSpy to the x64, single-file Barnyard process failed while creating `ICorDebug` with
`0x80131C3C`. Installing the matching .NET 10.0.9 Desktop Runtime did not help. Placing the exact
10.0.9 `mscordbi.dll` and `mscordaccore.dll` beside `Barnyard.exe` allowed attach to proceed.

### Cause

dnSpyEx supports `CreateDebuggingInterfaceFromVersion3` and
`ICLRDebuggingLibraryProvider3`, but its provider derives a runtime version from the single-file
executable's `FileVersionInfo` and searches the corresponding installed-runtime directory. A modern
static apphost does not have to carry the embedded CoreCLR's product version, so the correct installed
runtime can exist without ever being examined.

The provider callback already supplies the authoritative Windows PE identity: requested filename,
index type, timestamp, and `SizeOfImage`. The executable/runtime path is context and a search hint, not
the identity of the requested debugger component.

### Upstream context

- [dnSpyEx issue #48](https://github.com/dnSpyEx/dnSpy/issues/48) records the same HRESULT and the
  earlier single-file attach failure.
- [dnSpyEx commit 4fe02eb](https://github.com/dnSpyEx/dnSpy/commit/4fe02ebc022fb789f90b4e00932e9b654b60a0f8)
  introduced the current single-file library provider.
- [dnSpyEx PR #260](https://github.com/dnSpyEx/dnSpy/pull/260) updated the host and dbgshim but did
  not replace the provider's apphost-version heuristic.
- Microsoft's
  [`ICLRDebuggingLibraryProvider3::ProvideWindowsLibrary`](https://learn.microsoft.com/dotnet/core/unmanaged-api/debugging/iclrdebugginglibraryprovider3-providewindowslibrary-method)
  contract permits the debugger to locate or acquire an exact component using the supplied identity.

### Intended solution

Introduce one identity-driven resolver behind `ICLRDebuggingLibraryProvider3`, in this order:

1. Inspect application/runtime-adjacent candidates.
2. Inspect a private, per-user debugger-component cache.
3. Enumerate installed x64 `Microsoft.NETCore.App` runtimes as candidate stores, without choosing one
   from the apphost file version.
4. Optionally acquire official components from a configured Microsoft symbol store, with an explicit
   local-only/download policy.
5. Before returning an absolute path, validate basename, architecture, PE timestamp, and
   `SizeOfImage`; never load a nearest-version match.

Use Microsoft symbol-store libraries, or a narrow modern helper built on them, rather than depending
on or reverse-engineering `vsdbg`. Keep acquisition outside the dnSpy dispatcher and publish cache
entries atomically. For Microsoft-hosted downloads, retain provenance and verify the executable before
loading it.

### Acceptance criteria

- Barnyard attaches when its exact debugger components exist only in the installed 10.0.9 runtime.
- The same fixture still attaches when exact components exist only beside the application.
- A deliberately misleading apphost file version does not affect selection.
- A same-name candidate with the wrong timestamp or image size is rejected with an actionable trace.
- Local-only mode performs no network request and reports every searched source.
- Concurrent resolution cannot expose a partial cache entry.
- Tests cover cache, installed-runtime, adjacent-file, mismatch, missing/offline, and architecture
  cases without requiring CoreCLR support to enter the current shipping gate.

## TODO 2: never strand a CoreCLR target in a managed callback

### Observed behavior

After the debugger attached to Barnyard, dnSpy/dgSpy reported the session as running and no breakpoint
was hit, but Barnyard's enabled and visible Avalonia window became genuinely hung. Detaching restored
normal responsiveness immediately.

### Likely failure class

dnSpy's managed-callback path increments its callback counter, dispatches callback handling and event
subscribers, and normally reaches `ContinueAndDecrementCounter`. An exception during callback handling
is caught, debugger state is reset, and the exception is rethrown before the continuation code. The
runtime can therefore remain suspended while the outer debugger/session model reports no ordinary
stop.

This lifecycle defect exactly explains the observed suspend-until-detach behavior. The specific
Barnyard callback and exception are not yet captured, so do not claim that Barnyard has the historical
.NET 6 dynamic-module runtime bug.

### Upstream context

- [dnSpyEx issue #96](https://github.com/dnSpyEx/dnSpy/issues/96) reports the same attach, apparent
  running state, frozen target, and recovery on detach.
- The maintainer's
  [root-cause analysis](https://github.com/dnSpyEx/dnSpy/issues/96#issuecomment-1198525994) shows an
  exception bypassing the code that resumes the target.
- [dnSpyEx commit 02623aa](https://github.com/dnSpyEx/dnSpy/commit/02623aa4e413c5faa88b2fdde66ce9268b646b99)
  worked around the particular .NET 6 dynamic-module metadata trigger. It did not establish a general
  callback terminal-disposition invariant.

### Investigation before implementation

1. Reproduce against Barnyard with exact debugger libraries available.
2. Capture exceptions from `HandleManagedCallback`, `CheckBreakpoints`, and every
   `DebugCallbackEvent` subscriber, including callback kind and counter/state transitions.
3. Identify the first callback that fails and whether the exception originates in upstream dnSpy or a
   dgSpy subscriber.
4. Read back the real CorDebug process/callback state; do not rely only on the high-level `running`
   label.
5. Verify whether a continuation was attempted and, if so, preserve its HRESULT.

### Intended invariant

Every accepted managed callback must reach exactly one observable terminal disposition:

- continued successfully;
- intentionally paused with a reported stop reason;
- detached; or
- terminated/faulted with a reported error.

An exception must never silently leave the target suspended while the session reports `running`.
Do not implement this as an unconditional `Continue` in a broad `finally`: callback counters, queued
callbacks, intentional pauses, detach, and reentrant callbacks must remain correct.

### Acceptance criteria

- The captured Barnyard failure produces a durable error naming the callback stage and exception.
- With no requested stop, successful attach leaves Barnyard responsive and foregroundable.
- A callback-handler exception cannot yield `running` while the target remains suspended.
- Breakpoints, attach-time queued callbacks, intentional pause, detach, termination, and reentrant
  callback behavior retain exactly-once continuation/counter semantics.
- dgSpy state and event responses distinguish running, intentionally stopped, and faulted attach.
- Regression coverage injects failures at each callback-processing stage and proves the target reaches
  one terminal disposition without timing-based assertions.
