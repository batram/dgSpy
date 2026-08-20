# AGENTS.md

Working notes for coding agents in the dgSpy repo. Read this before changing anything;
it is short on purpose and points at the documents that carry the detail.

dgSpy is a fork of dnSpy that adds an MCP-driven debugging extension. The dnSpy tree is
mostly upstream code; `dgSpy.*` projects are ours.

## Read first

| Document | What it settles |
| --- | --- |
| [docs/reference/DGSPY_BASELINE.md](docs/reference/DGSPY_BASELINE.md) | Build commands, host targets, and the failure modes that waste the most time. Read before improvising a build. |
| [docs/product/ARCHITECTURE.md](docs/product/ARCHITECTURE.md) | How the extension, gateway and CLI fit together. |
| [docs/reference/DGSPY_REFERENCE.md](docs/reference/DGSPY_REFERENCE.md) | Tool surface and protocol reference. |
| [docs/guides/GETTING_STARTED.md](docs/guides/GETTING_STARTED.md) | Installing and running a session. |

## Scope

x64, CLR v4, CoreCLR, and Unity/UCH. x86 is out of scope; do not add support or tests for it.
CoreCLR HookLab residents are supported through a separate, explicit backend from ordinary CoreCLR debugging.
Prefer the smallest change that works and verify it by running it.

## Build

```bash
dotnet run --project Build\DgSpyTool -- pipeline
```

This is the only supported dgSpy build/package path. It publishes immutable compiler inputs,
composes a fresh layout, verifies every file, and publishes a package without mutating build output.

Run dgSpy restore, build, test, and pipeline commands under the normal Windows identity from the
first attempt. The Codex sandbox blocks NuGet HTTPS requests and can also be unable to overwrite
`bin`/`obj` intermediates created by the normal identity. Treat socket-denied `NU1301` and those
cross-identity access failures as execution-environment failures, not repository defects. Do not
weaken NuGet checks, maintain an offline package feed, or redesign build outputs to work around them.

Submodules must be present, or the build fails with `MSB3202` on seven missing projects:

```bash
git submodule update --init --recursive
```

## Test

```bash
.\tests\run-modernization-gate.ps1 -Stage CorDebug
```

Two live legs run the debugger and the target as **different Windows accounts**, because every other
gate runs them as the same user in the default application domain - the arrangement that hid five
defects until a live IIS worker found them. They need a one-time, elevated setup:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tests\TestSupport\Register-CrossIdentitySmokeTasks.ps1
```

That creates the non-admin `dgspy-fixture` account and registers two scheduled tasks. Afterwards the
gate runs both legs with no elevation and no prompt: attaching across accounts needs
`SeDebugPrivilege`, and the tasks supply it rather than the gate demanding an elevated shell every run.
Without the setup the gate skips those legs and names why. The smokes refuse to fall back to a
same-user run, since a cross-identity gate that quietly runs as one identity is the gap they close.

Other stages and smokes live in `tests\`. Treat harness code as carefully as product code:
a harness bug looks exactly like a product bug and costs a full verify cycle. When a bug in
your own tooling turned out to be the cause, say so explicitly in your final message ---
mid-run status text is often never read.

## Silent failures

Two classes of defect produce no error, no log line, and no test failure. Both look like a
bad build and are not.

**MEF composition.** A part whose imports cannot be satisfied does not exist. Nothing is
logged, and every part that imported it disappears too. The symptom is a quietly *reduced*
UI: menu entries gone, a dropdown missing options, a tool window empty. Before adding any
constructor import, confirm the type is actually exported --- `[Export(typeof(T))]`, or an
existing `[ImportingConstructor]` that already takes it. Public and obviously available is
not evidence. `IAppCommandLineArgs` in particular is *not* a MEF export. The full account is
in the "MEF composition fails silently" section of `docs/reference/DGSPY_BASELINE.md`.

dnSpy does check this, but only under `Debug.Assert(config.ThrowOnErrors() == config)`, which
the Release build we ship compiles away. `tests\dgSpy.Composition.Tests` performs that check
in a test run instead, against the published assemblies:

```bash
dotnet test tests\dgSpy.Composition.Tests\dgSpy.Composition.Tests.csproj -c Release
```

It must run *after* a publish, and the gate already sequences it that way. Note that no
behavioural test can cover this: when a part fails to compose there is nothing to call and
nothing to assert against, which is why these tests read the composition itself.

**Half-applied upstream commits.** When taking or reverting upstream work, take the whole
commit. A feature typically spans an interface in `dnSpy.Contracts.*`, its `[Export]`ed
implementation in an extension, resource strings in `Properties\*.resx` *and* the generated
`*.Designer.cs`, and `ContentTypeDefinition` exports in a `ContentTypeDefinitions.cs`.
Dropping any one of them compiles cleanly and fails at runtime, or composes away silently.
`git show --diff-filter=D --name-only --format="" <commit>` lists what a commit deleted;
check whether anything still imports or references it.

## Staying in sync with dnSpyEx

This repo is a genuine fork of [dnSpyEx](https://github.com/dnSpyEx/dnSpy). Its `master` is a
direct ancestor of ours, so most of the tree is upstream code that we should not be touching.

The rule is simple: **every file that differs from dnSpyEx must have a written reason.**

```bash
powershell -NoProfile -File tools\check-upstream-drift.ps1
```

`tools/upstream-drift-allowlist.txt` records each intentional divergence and why. Anything
differing without an entry fails the check, and so does an entry that no longer matches
anything -- a stale line must be deleted rather than left as a standing licence to overwrite
something. `tools/upstream-baseline.txt` pins the exact dnSpyEx commit we compare against, so a
new upstream release cannot turn a green run red by itself. Use `-Report` to see the drift
grouped by reason. CI runs this as its own job.

If a file drifts and the change was not deliberate, **restore it rather than listing it**:

```bash
git checkout 3f4caa4f8 -- <path>
```

When deliberately merging a newer dnSpyEx, update the baseline and re-check the allowlist in the
same change: a merge can legitimately resolve drift, and can equally hide new drift behind an
entry that already exists.

## Seeing the GUI

No automated suite catches the failures above, because they are only visible in the running
application. [tools/gui-inspect.ps1](tools/gui-inspect.ps1) drives the dnSpy WPF GUI over UI
Automation so an agent can check without a human at the keyboard.

```bash
powershell -NoProfile -STA -File tools\gui-inspect.ps1 -List
```

Launch the published build, open Debug > Start Debugging, and list the debug engines:

```bash
powershell -NoProfile -STA -File tools\gui-inspect.ps1 -Launch -Keys "{F5}" -WaitWindow "Debug Program" -ExpandCombo 0
```

Dump a window's control tree, click a button, or photograph anything UI Automation cannot
express:

```bash
powershell -NoProfile -STA -File tools\gui-inspect.ps1 -Window "Debug Program" -ClickNear envTextBox -WaitWindow "Edit Environment Variables" -Dump -Screenshot env.png
```

Run `Get-Help tools\gui-inspect.ps1 -Full` for every switch. Four things about it are worth
knowing up front, because each one cost a debugging cycle to find:

- **`-STA` is mandatory.** UI Automation refuses to marshal from an MTA thread.
- **A WPF item's `Name` is often its view model's `ToString()`.** The text a user sees lives
  on a descendant `Text` element. The script resolves this for you; hand-rolled UIA code
  will report `dnSpy.Debugger.Dialogs.DebugProgram.OptionsPageVM` four times instead of the
  four engine names.
- **Combo popups only render when the owning window is active**, and Windows refuses
  `SetForegroundWindow` from a background process. The script calls
  `AutomationElement.SetFocus()`, which does activate. Without it, item enumeration silently
  returns zero and looks like the options are missing.
- **Dialogs are not reliably children of the desktop root.** Search `TreeScope.Descendants`
  by name. A `TreeScope.Children` sweep can miss a dialog that is plainly on screen and
  report that a keystroke did not land when it did.

## Conventions

- Keep `.ps1` files ASCII-only. An em dash parses as a stray quote under CP1252.
- The Bash and PowerShell tools take different multi-line-string syntax. Heredocs are Bash
  only; here-strings are PowerShell only.
- Do not switch branches. This repo commits on whatever branch is checked out.
- New tooling goes in `tools\`, not the repository root.
- **Never apply a cross-baseline overlay, tree copy, or bulk file sync.** Commit 399ed7297 did
  exactly that -- it set this tree to the content of an older dgSpy tree, which silently reverted
  57 files to a pre-dnSpyEx state. Making one tree's hash equal another's is not integration; it
  discards everything the target had that the source lacked. Changes to shared files land as
  individual, reviewable commits, and `tools\check-upstream-drift.ps1` now fails if they do not.
