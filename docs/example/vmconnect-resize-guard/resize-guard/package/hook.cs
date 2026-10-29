using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// Paired Prefix/Postfix on InteractiveSessionForm.UpdateRdpClientDisplaySettings.
//
// The RDP display-control rejects a size under 200 (or over 8192) device pixels with E_FAIL. vmconnect
// has by then already added the drag delta to m_TrueSyncedEnhancedModeDesktopResolution and resized the
// viewer to it, and never rolls either back, so every later resize builds on a size the guest never got
// until the viewer collapses to 0x0 and the window goes blank.
//
// Prefix: clamp the requested size into that range, so the request succeeds.
// Postfix: if the request failed anyway, or the viewer is non-positive (eg. a 0x0 pre-fullscreen size
// restored on exit), resync the requested size and the viewer from the resolution the guest really has.
// Either correction re-fits the form around the viewer.
public static class VmconnectResizeGuard
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const int SyncedSession = 6; // RdpViewerConnectionState.ConnectedEnhancedVideoSyncedSession

    public static void Prefix(Form __instance, Control ___m_RdpViewer, ref Size ___m_TrueSyncedEnhancedModeDesktopResolution, out bool __state)
    {
        __state = false;
        try
        {
            if (!IsWindowedSyncedSession(___m_RdpViewer)) return;
            int dpi = __instance.DeviceDpi;
            Size requested = ___m_TrueSyncedEnhancedModeDesktopResolution;
            Size clamped = new Size(ClampLogical(requested.Width, dpi), ClampLogical(requested.Height, dpi));
            if (clamped == requested) return;
            ___m_TrueSyncedEnhancedModeDesktopResolution = clamped;
            __state = true;
            Log("clamp " + requested + " -> " + clamped);
        }
        catch (Exception e) { Log("clamp failed: " + e); }
    }

    public static void Postfix(Form __instance, Control ___m_RdpViewer, ref Size ___m_TrueSyncedEnhancedModeDesktopResolution, bool __result, bool __state)
    {
        try
        {
            Control viewer = ___m_RdpViewer;
            if (!IsWindowedSyncedSession(viewer)) return;
            Size viewerSize = viewer.Size;
            if (__result && viewerSize.Width > 0 && viewerSize.Height > 0)
            {
                // Succeeded: the original already sized the viewer to the (clamped) request.
                if (__state) SetFormSizes(__instance, viewerSize);
                return;
            }
            Size guest = (Size)Property(viewer, "DesktopResolution");
            if (guest.Width <= 0 || guest.Height <= 0) return;
            int dpi = __instance.DeviceDpi;
            Size logical = new Size(ToLogical(guest.Width, dpi), ToLogical(guest.Height, dpi));
            Log("rollback result=" + __result + " requested " + ___m_TrueSyncedEnhancedModeDesktopResolution + " viewer " + viewerSize + " -> guest " + guest);
            ___m_TrueSyncedEnhancedModeDesktopResolution = logical;
            viewer.Size = guest;
            SetFormSizes(__instance, guest);
        }
        catch (Exception e) { Log("rollback failed: " + e); }
    }

    static bool IsWindowedSyncedSession(Control viewer)
    {
        return viewer != null
            && Convert.ToInt32(Property(viewer, "ConnectionState")) == SyncedSession
            && !(bool)Property(viewer, "FullScreen");
    }

    // Smallest logical size that is at least 200 device pixels, largest that is at most 8192.
    static int ClampLogical(int logical, int dpi)
    {
        int minimum = (200 * 96 + dpi - 1) / dpi;
        int maximum = 8192 * 96 / dpi;
        return Math.Max(minimum, Math.Min(maximum, logical));
    }

    static int ToLogical(int device, int dpi) { return (device * 96 + dpi / 2) / dpi; }

    static object Property(object target, string name) { return target.GetType().GetProperty(name, Members).GetValue(target, null); }

    static void SetFormSizes(Form form, Size viewerSize)
    {
        MethodInfo method = form.GetType().GetMethod("SetFormSizes", Members, null, new[] { typeof(Size), typeof(bool) }, null);
        method.Invoke(form, new object[] { viewerSize, false });
    }

    internal static void Log(string line)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("LOCALAPPDATA") + "\\HookLab\\vmconnect-resize-guard.log";
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 256 * 1024) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\r\n");
        }
        catch { }
    }
}
