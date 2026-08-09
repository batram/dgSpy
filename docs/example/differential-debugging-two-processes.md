# Example: why one VM window resizes and another does not

A worked example of using dgSpy on two live processes at once.

**The question.** Hyper-V's `VmConnect.exe` shows one window per VM. Connected to a Windows guest, the
window has a draggable border and the guest resolution follows it. Connected to a Debian guest, the
border is fixed. Same binary, same host, same user — so the difference has to be state, and state is
what a debugger is for.

---

## 1. See the targets at all

`VmConnect.exe` runs elevated. A debugger host at normal integrity cannot even enumerate it:

```jsonc
// unelevated host
list_programs { "process_ids": [30488, 31784] }
-> { "result": [] }
```

The processes are running, and the host simply cannot see them. Start an elevated host instead:

```jsonc
launch_local_host { "elevated": true }
-> { "started": true, "elevated": true, "connected": true, "process_id": 34424 }
```

```jsonc
list_programs { "process_ids": [30488, 31784] }
-> [ { "pid": 30488, "title": "deb13 on RYZ ...",    "runtime_name": "CLR v4.0.30319" },
     { "pid": 31784, "title": "WinAgain on RYZ ...", "runtime_name": "CLR v4.0.30319" } ]
```

The `elevated` field in the result reports the host's _actual_ token, not what was requested, so a
caller can tell the difference between "I asked for elevation" and "I have it".

## 2. Attach to both

```jsonc
attach { "program_id": "31784:...:CLR v4.0.30319" }   // Windows guest
attach { "program_id": "30488:...:CLR v4.0.30319" }   // Linux guest
-> { "session_id": "66a6...", "process_ids": [31784, 30488] }
```

Both targets now live in one session.

> **Practical note.** Module-scoped tools (`get_csharp`, `search_text`, `list_types`, `set_breakpoint`)
> resolve by module name, and here both processes load the _same_ `VmConnect.exe` from the same path —
> so they answer `ambiguous_target`. Detach one while doing static work, or do the static work before
> attaching the second. Evaluation is frame-scoped and unaffected.

## 3. Find the decision point

```jsonc
search_symbols { "pattern": "Enhanced", "module": "VmConnect" }
-> ... RdpViewerControl.get_RdpEnhancedModeAvailable ...
```

```jsonc
get_csharp { "module": "VmConnect.exe", "method_token": 100663615 }
```

```csharp
public bool get_RdpEnhancedModeAvailable()
{
    return this.m_OldRdpEnhancedModeAvailable && !RdpOptions.IsRestrictedAdminModePolicyEnabled();
}
```

Where does that field come from?

```jsonc
search_text { "pattern": "m_OldRdpEnhancedModeAvailable", "type": "RdpViewerControl" }
-> SetupVirtualMachine():
   this.m_OldRdpEnhancedModeAvailable =
       this.VirtualMachine.EnhancedSessionModeState == EnhancedSessionModeStateType.Available;
```

A clean hypothesis: enhanced session mode must be unavailable for the Linux VM.

## 4. Kill the hypothesis with data

Read the field in the _Linux_ process:

```jsonc
evaluate { "expression": "this.m_OldRdpEnhancedModeAvailable" }
-> { "type": "Boolean", "value": true }
```

`true`. Enhanced mode **is** available for the Linux guest, and the hypothesis is dead after one read.
This is the point of attaching to the real thing rather than reasoning from source.

## 5. Compare the two processes

Expand the control's state in each target and look for what actually differs:

```jsonc
get_members { "expression": "this", "offset": 200 }   // the m_* fields
```

| field                           | Linux guest                      | Windows guest                                 |
| ------------------------------- | -------------------------------- | --------------------------------------------- |
| `m_OldRdpEnhancedModeAvailable` | `true`                           | `true`                                        |
| `m_ConnectInEnhancedMode`       | `true`                           | —                                             |
| **`m_State`**                   | **`ConnectedEnhancedVideo` (5)** | **`ConnectedEnhancedVideoSyncedSession` (6)** |

One field differs. Decompiling the enum shows why it matters:

```csharp
internal enum RdpViewerConnectionState {
    NoVirtualMachine, NotConnected, Connecting,
    ConnectedNoVideo, ConnectedBasicVideo,
    ConnectedEnhancedVideo,               // 5 — Linux guest
    ConnectedEnhancedVideoSyncedSession   // 6 — Windows guest
}
```

and `InteractiveSessionForm.SetFormSizes()` enumerates state 5 as a _fixed-size_ case:

```csharp
if (ConnectionState == RdpViewerConnectionState.ConnectedEnhancedVideo)
    SetFormSizes(DesktopResolution, true);    // sizeFixed
else
    SetFormSizes(DesktopResolution, false);   // Sizable
...
base.FormBorderStyle = (sizeFixed ? FormBorderStyle.Fixed3D : FormBorderStyle.Sizable);
```

So the fixed border is a direct, documented consequence of being at state 5.

## 6. Find what promotes 5 to 6

```jsonc
search_text { "pattern": "ConnectedEnhancedVideoSyncedSession" }
```

Exactly one assignment, in `RdpViewerControl.SetupRdpEventHandlers`:

```csharp
m_RdpClient.OnLoginComplete += delegate {
    if (this.ConnectionState == RdpViewerConnectionState.ConnectedEnhancedVideo)
        this.ConnectionState = ConnectedEnhancedVideoSyncedSession;
};
```

The gate is an RDP logon notification. So the question becomes: does the Linux guest ever send one?

## 7. Prove a negative with a breakpoint

Arm the handler itself and drive a full reconnect and login:

```jsonc
set_breakpoint { "type": "...RdpViewerControl", "method": "<SetupRdpEventHandlers>b__190_0" }
-> { "breakpoint_id": 1, "bound": true }
```

After a complete disconnect/reconnect/login cycle:

```jsonc
list_breakpoints
-> { "breakpoint_id": 1, "bound": true, "enabled": true, "engine_hit_count": 1 }
```

`engine_hit_count` is counted **before** any condition runs, which is what makes it usable as evidence:
it separates "never reached" from "condition never true". The single hit came from a _basic-mode_
connection, and its call stack proves the event is real when it does happen:

```
RdpViewerControl.<SetupRdpEventHandlers>b__190_0
AxMsRdpClient9NotSafeForScripting.RaiseOnOnLoginComplete   <- RdpClientAxHost.dll
[Native to Managed Transition]                             <- COM event from the ActiveX control
... FPushMessageLoop -> Program.Main
```

On the enhanced connection to the Linux guest it never fired. A breakpoint that stays bound and is never
reached is a measurement, not an absence of evidence.

## 8. Prove causality by changing it

Reading state shows correlation. Writing it shows cause. Invoke the real property setter — which also
raises `ConnectionStateChanged`, so the form reacts exactly as it would normally:

```jsonc
evaluate {
  "expression": "this.m_RdpViewer.ConnectionState = ...RdpViewerConnectionState.ConnectedEnhancedVideoSyncedSession",
  "allow_func_eval": true,
  "allow_side_effects": true
}
-> { "display": "ConnectedEnhancedVideoSyncedSession", "value": 6 }
```

```jsonc
evaluate { "expression": "this.FormBorderStyle", "allow_func_eval": true }
-> { "display": "Sizable" }     // was Fixed3D
```

The border became draggable, and dragging it **actually resized the Linux guest's desktop**. That single
mutation converts a plausible story into a demonstrated one: the guest was always capable, and the only
thing standing in the way was a client-side state check.

> Two gates guard this deliberately. `allow_func_eval` permits running target code at all;
> `allow_side_effects` permits an expression dnSpy classifies as side-effecting. An assignment needs
> both, and the refusal names which gate blocked it.

## Outcome

The root cause is that **xrdp never sends a Save Session Info PDU** ([MS-RDPBCGR] 2.2.10.1), so mstscax
never raises `OnLoginComplete`, so VMConnect never leaves state 5. Confirmed independently in the xrdp
source: the only caller of `libxrdp_send_session_info` is the RDP-proxy backend relaying an upstream
server's PDU. A three-file patch to xrdp that emits the notification when the session is established
makes Linux guests resize with no debugger involved.

## Techniques worth reusing

- **Elevated host for elevated targets.** Otherwise they are invisible, not merely unattachable.
- **Two processes, one session.** Comparing live state across two instances of one binary is the fastest
  way to find which of a hundred fields actually differs.
- **Let data kill hypotheses early.** One `evaluate` retired the "enhanced mode unavailable" theory that
  the source code had made look obvious.
- **`engine_hit_count` proves a negative.** A bound breakpoint that never fires is real evidence.
- **Field reads work when func-eval does not.** At a stop where property getters are refused, raw fields
  still read — often enough to finish the job.
- **Idle GUI targets have no evaluable frame.** Every thread parks in native code, so `run_to_method`
  never arrives. Set a breakpoint on a message handler, deliver an external stimulus (posting `WM_NULL`
  to the target's windows works and is invisible), then `wait_for_stop`.
- **Mutation tests causality.** When a read tells you _what_ differs, a guarded write tells you whether
  it is the cause.
