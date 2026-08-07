# AGENTS.md

Working notes for coding agents in the dgSpy repo. Read this before changing anything;
it is short on purpose and points at the documents that carry the detail.

dgSpy is a fork of dnSpy that adds an MCP-driven debugging extension. The dnSpy tree is
mostly upstream code; `dgSpy.*` projects are ours.

## Read first

| Document | What it settles |
| --- | --- |
| [docs/DGSPY_BASELINE.md](docs/DGSPY_BASELINE.md) | Build commands, host targets, and the failure modes that waste the most time. Read before improvising a build. |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the extension, gateway and CLI fit together. |
| [docs/DGSPY_REFERENCE.md](docs/DGSPY_REFERENCE.md) | Tool surface and protocol reference. |
| [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) | Installing and running a session. |

## Scope

x64, CLR v4, Unity/UCH. x86 and CoreCLR are out of scope; do not add support or tests for
them. Prefer the smallest change that works and verify it by running it.

## Build

```bash
.\build.ps1 net-x64 -NoMsbuild
```

```bash
.\build-dgspy.ps1
```

`build.ps1` wipes the deployed extension, so `build-dgspy.ps1` has to run after it, in that
order, every time. The build is deterministic and does not need a second run --- if
something looks missing after a rebuild, see "Silent failures" below before touching the
build system. `docs/DGSPY_BASELINE.md` covers the retained net48 host and the one MSBuild
that works for it.

Submodules must be present, or the build fails with `MSB3202` on seven missing projects:

```bash
git submodule update --init --recursive
```

## Test

```bash
.\tests\run-modernization-gate.ps1 -Stage CorDebug
```

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
in the "MEF composition fails silently" section of `docs/DGSPY_BASELINE.md`.

**Half-applied upstream commits.** When taking or reverting upstream work, take the whole
commit. A feature typically spans an interface in `dnSpy.Contracts.*`, its `[Export]`ed
implementation in an extension, resource strings in `Properties\*.resx` *and* the generated
`*.Designer.cs`, and `ContentTypeDefinition` exports in a `ContentTypeDefinitions.cs`.
Dropping any one of them compiles cleanly and fails at runtime, or composes away silently.
`git show --diff-filter=D --name-only --format="" <commit>` lists what a commit deleted;
check whether anything still imports or references it.

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
