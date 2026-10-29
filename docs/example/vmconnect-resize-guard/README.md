# Example: keeping a VMConnect Enhanced Session window from going blank

Two HookLab watcher deployments that fix a client-side resize bug in Hyper-V's `VmConnect.exe`
(`10.0.19041.7802`, module MVID `ef16ec22-02e9-4006-a311-609f3885235e`). They are a worked example of
diagnosing a closed-source WinForms app with dgSpy and shipping the fix as an automatically applied hook.

## The symptom

In an Enhanced Session (the window border drags the guest resolution with it), shrink the window until
the guest area would be narrower or shorter than 200 pixels. From then on every resize makes it worse:
the guest area shifts, then the window shows nothing at all in windowed mode. Fullscreen still works,
and toggling Enhanced Session or reconnecting recovers it.

## The mechanism

All in `Microsoft.Virtualization.Client.InteractiveSession`, traced live with breakpoints:

- `InteractiveSessionForm` tracks the size it asked the guest for in
  `m_TrueSyncedEnhancedModeDesktopResolution` (logical units) while the viewer is in
  `ConnectedEnhancedVideoSyncedSession` (6).
- `OnResizeEnd` adds the drag delta to that field, re-fits the form, sets the viewer to it, then calls
  `UpdateRdpClientDisplaySettings`.
- `UpdateRdpClientDisplaySettings` saves the viewer size for a rollback - but it was already
  overwritten, so the rollback restores the new value. It calls `RdpViewerControl.UpdateDisplaySettings`.
- `UpdateDisplaySettings` calls the ActiveX `UpdateSessionDisplaySettings`. The RDP display-control
  refuses anything under 200 (or over 8192) device pixels with `COMException E_FAIL`, client-side; no
  request reaches the guest. The exception is swallowed and the method returns false.
- Nothing resets `m_TrueSyncedEnhancedModeDesktopResolution`. The guest keeps its old size, the field
  keeps the refused one, and each later drag adds its delta to the wrong base - live, the field reached
  `120x-76`. The viewer is clamped to 0x0 and the window goes blank. Only
  `HandleViewerConnectedStateChanged` (reconnect, Enhanced Session toggle) re-initializes the field.

A second, smaller drift: `RdpViewerControl.OnVirtualMachineResolutionChanged` (a size reported by the
guest) updates the viewer's resolution and the form, but never the form's field. When the guest answers
with a different size than requested, the next drag starts from a stale base. This is not hypothetical:
the guest measured here answers an odd width one pixel narrower (`1461` requested, `1460` reported).

## The fix

| deployment | profile | target | kind |
| --- | --- | --- | --- |
| [resize-guard](resize-guard) | `vmconnect-resize-guard-user` | `InteractiveSessionForm.UpdateRdpClientDisplaySettings` | paired Prefix/Postfix |
| [server-resize-sync](server-resize-sync) | `vmconnect-server-resize-sync-user` | `RdpViewerControl.OnVirtualMachineResolutionChanged` | Postfix |

- **Prefix** clamps the requested size into 200-8192 device pixels, converting with the form's
  `DeviceDpi`, so the request succeeds. The **Postfix** then re-fits the form around the viewer. If a
  request fails anyway, or the viewer is non-positive (a 0x0 pre-fullscreen size restored on exit
  passes through the same method), it resyncs the field and the viewer from the resolution the guest
  really has.
- The **server Postfix** resyncs the field and the viewer from a guest-reported size, outside fullscreen
  and outside `HandleEnterFullScreen`. An echo of vmconnect's own request already matches and changes
  nothing.

Both only act in a windowed, synced Enhanced Session, catch their own exceptions, and use reflection
for vmconnect's non-public members. Every correction appends one line to
`%LOCALAPPDATA%\HookLab\vmconnect-resize-guard.log`, which truncates itself at 256 KiB:

```
2026-10-29 21:39:26.273 clamp {Width=135, Height=22} -> {Width=200, Height=200}
2026-10-29 21:39:29.352 clamp {Width=120, Height=-76} -> {Width=200, Height=200}
2026-10-29 21:39:39.277 server resize {Width=1460, Height=712}: requested {Width=1461, Height=712} -> {Width=1460, Height=712}
```

The paired source is about 5 KB. Hook source was bounded to roughly 1.5 KiB until the bootstrap value
limit was raised; see [HookLab](../../product/HOOKLAB.md) for the current bound.

## Deploying

Each directory is a canonical `export_hook_package` deployment. Enroll and enable with the installed,
elevated watcher (see [HookLab watcher](../../guides/HOOKLAB_WATCHER.md)):

```powershell
$watcher = "$env:LOCALAPPDATA\Programs\HookLab.Watcher\HookLab.Watcher.exe"
& $watcher enroll --deployment docs\example\vmconnect-resize-guard\resize-guard
& $watcher enroll --deployment docs\example\vmconnect-resize-guard\server-resize-sync
& $watcher enable-profile vmconnect-resize-guard-user
& $watcher enable-profile vmconnect-server-resize-sync-user
```

They are examples rather than built-in watcher deployments on purpose: a built-in and an enrolled
profile may not share a profile or hook ID, and the packages are pinned to one vmconnect build.

## When Windows updates vmconnect

A cumulative update that rebuilds `vmconnect.exe` changes its MVID, and the watcher then refuses both
packages before injecting anything. Re-pin by attaching dgSpy to the new vmconnect, recreating each
hook from its `hook.cs`, re-exporting it, and enrolling with `--replace`. Check the decompiled methods
against the description above first; the fix depends on their exact control flow.
