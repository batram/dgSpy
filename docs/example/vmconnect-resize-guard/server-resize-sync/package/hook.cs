using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

// Postfix on RdpViewerControl.OnVirtualMachineResolutionChanged.
//
// A server-initiated size updates the viewer's m_DesktopResolution and re-fits the form, but never the
// form's m_TrueSyncedEnhancedModeDesktopResolution, so the next drag-resize adds its delta to a stale
// base. While windowed in a synced Enhanced Session, resync it (and the viewer) from what the guest
// reported. An echo of our own request matches already and changes nothing.
public static class VmconnectServerResizeSync
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const int SyncedSession = 6; // RdpViewerConnectionState.ConnectedEnhancedVideoSyncedSession

    public static void Postfix(Control __instance)
    {
        try
        {
            Control viewer = __instance;
            if (Convert.ToInt32(Property(viewer, "ConnectionState")) != SyncedSession || (bool)Property(viewer, "FullScreen")) return;
            Form form = viewer.FindForm();
            if (form == null) return;
            FieldInfo trueSynced = form.GetType().GetField("m_TrueSyncedEnhancedModeDesktopResolution", Members);
            FieldInfo entering = form.GetType().GetField("_enteringFullScreen", Members);
            if (trueSynced == null || (entering != null && (bool)entering.GetValue(form))) return;
            Size guest = (Size)Property(viewer, "DesktopResolution");
            if (guest.Width <= 0 || guest.Height <= 0) return;
            int dpi = form.DeviceDpi;
            Size logical = new Size((guest.Width * 96 + dpi / 2) / dpi, (guest.Height * 96 + dpi / 2) / dpi);
            Size before = (Size)trueSynced.GetValue(form);
            if (before == logical && viewer.Size == guest) return;
            trueSynced.SetValue(form, logical);
            viewer.Size = guest;
            Log("server resize " + guest + ": requested " + before + " -> " + logical);
        }
        catch (Exception e) { Log("server resize sync failed: " + e); }
    }

    static object Property(object target, string name) { return target.GetType().GetProperty(name, Members).GetValue(target, null); }

    static void Log(string line)
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
