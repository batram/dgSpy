using System.Text.Json;
using HookLab.Watcher;

namespace HookLab.Watcher.Companion;

internal static class Program {
	[STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new CompanionContext(CompanionPaths.Default)); }
}

internal sealed record CompanionPaths(string StateRoot,string AuditPath) {
	public static CompanionPaths Default { get; }=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-audit.jsonl"));
}
public enum CompanionState { Running,Paused,Error,Stale,Stopped }
internal sealed record CompanionSnapshot(CompanionState State,string Detail,IReadOnlyList<string> Profiles,IReadOnlySet<string> DisabledProfiles) {
	public static CompanionSnapshot Read(CompanionPaths paths,Func<int,long,bool>? identityIsAlive=null) {
		try {
			var status=WatcherStatusStore.Read(Path.Combine(paths.StateRoot,"watcher-status.json"),identityIsAlive);
			if(status is null) return New(CompanionState.Stopped,"No watcher status has been published.");
			var root=status.Value; var lifecycle=root.GetProperty("lifecycle").GetString();
			var disabled=root.TryGetProperty("disabledProfiles",out var disabledJson)?disabledJson.EnumerateArray().Select(value=>value.GetString()!).Where(value=>value is not null).ToHashSet(StringComparer.Ordinal):new HashSet<string>(StringComparer.Ordinal);
			var profiles=root.TryGetProperty("definitions",out var definitions)?definitions.EnumerateArray().Select(value=>value.TryGetProperty("profileId",out var id)?id.GetString():null).Where(value=>!String.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.Ordinal).OrderBy(value=>value,StringComparer.Ordinal).ToArray():Array.Empty<string>();
			CompanionSnapshot Result(CompanionState state,string detail)=>new(state,detail,profiles,disabled);
			if(lifecycle=="stale") return Result(CompanionState.Stale,"Recorded watcher process is no longer alive.");
			if(root.TryGetProperty("catalogError",out var error)&&error.ValueKind==JsonValueKind.String&&!String.IsNullOrWhiteSpace(error.GetString())) return Result(CompanionState.Error,error.GetString()!);
			if(lifecycle is "stopped" or null) return Result(CompanionState.Stopped,"Watcher is stopped.");
			if(root.TryGetProperty("paused",out var paused)&&paused.GetBoolean()) return Result(CompanionState.Paused,"Watcher is paused; resident hooks are unchanged.");
			if(lifecycle=="running"&&root.GetProperty("processAlive").GetBoolean()) return Result(CompanionState.Running,"Watcher is running.");
			return Result(CompanionState.Error,"Watcher state is "+lifecycle+".");
		} catch(Exception ex) { return New(CompanionState.Error,"Status could not be read: "+Sanitize(ex.Message)); }
	}
	static CompanionSnapshot New(CompanionState state,string detail)=>new(state,detail,Array.Empty<string>(),new HashSet<string>(StringComparer.Ordinal));
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ');
}

internal sealed class CompanionOperations {
	readonly CompanionPaths paths; readonly Func<string,string> taskAction; public CompanionOperations(CompanionPaths paths,Func<string,string>? taskAction=null) { this.paths=paths; this.taskAction=taskAction??RunTask; }
	public void Pause(bool paused)=>new WatchControlStore(Path.Combine(paths.StateRoot,"watcher-control.json")).Update(paused:paused);
	public void SetProfile(string profile,bool enabled)=>new WatchControlStore(Path.Combine(paths.StateRoot,"watcher-control.json")).Update(enableProfile:enabled?profile:null,disableProfile:enabled?null:profile);
	public string StartTask()=>taskAction("/Run"); public string RestartTask() { taskAction("/End"); return taskAction("/Run"); }
	static string RunTask(string action) { var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true}; info.ArgumentList.Add(action); info.ArgumentList.Add("/TN"); info.ArgumentList.Add("HookLab Watcher"); using var process=System.Diagnostics.Process.Start(info)??throw new InvalidOperationException("Could not start Task Scheduler command."); var output=process.StandardOutput.ReadToEnd(); var error=process.StandardError.ReadToEnd(); process.WaitForExit(); if(process.ExitCode!=0) throw new InvalidOperationException("Task Scheduler refused the operation: "+Sanitize(error)); return Sanitize(output); }
	public IReadOnlyList<string> ReadAudit(int maximum=200) { if(!File.Exists(paths.AuditPath)) return Array.Empty<string>(); using var stream=new FileStream(paths.AuditPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); using var reader=new StreamReader(stream); var rows=new Queue<string>(maximum); while(reader.ReadLine() is { } line) { if(rows.Count==maximum) rows.Dequeue(); rows.Enqueue(line); } return rows.ToArray(); }
	static string Sanitize(string value)=>value.Replace('\r',' ').Replace('\n',' ').Trim();
}

internal sealed class CompanionContext:ApplicationContext {
	readonly CompanionPaths paths; readonly CompanionOperations operations; readonly NotifyIcon icon; readonly System.Windows.Forms.Timer timer; readonly ToolStripMenuItem stateItem=new(); readonly ToolStripMenuItem pauseItem=new("Pause"); readonly ToolStripMenuItem resumeItem=new("Resume"); readonly ToolStripMenuItem profilesItem=new("Profiles");
	public CompanionContext(CompanionPaths paths) { this.paths=paths; operations=new(paths); icon=new NotifyIcon { Icon=HookIcon.Create(),Text="HookLab Watcher",Visible=true,ContextMenuStrip=new ContextMenuStrip() }; var menu=icon.ContextMenuStrip.Items; stateItem.Enabled=false; menu.Add(stateItem); menu.Add(new ToolStripSeparator()); pauseItem.Click+=(s,e)=>Act(()=>operations.Pause(true)); resumeItem.Click+=(s,e)=>Act(()=>operations.Pause(false)); menu.Add(pauseItem); menu.Add(resumeItem); menu.Add(profilesItem); menu.Add(new ToolStripSeparator()); menu.Add("Start installed task",null,(s,e)=>Act(()=>operations.StartTask())); menu.Add("Restart installed task",null,(s,e)=>Act(()=>operations.RestartTask())); menu.Add("Audit log",null,(s,e)=>ShowAudit()); menu.Add(new ToolStripSeparator()); menu.Add("Exit companion",null,(s,e)=>ExitThread()); icon.DoubleClick+=(s,e)=>ShowAudit(); timer=new System.Windows.Forms.Timer { Interval=1000 }; timer.Tick+=(s,e)=>Refresh(); timer.Start(); Refresh(); }
	void Refresh() { var snapshot=CompanionSnapshot.Read(paths); stateItem.Text="State: "+snapshot.State.ToString().ToLowerInvariant(); icon.Text="HookLab Watcher - "+snapshot.State.ToString().ToLowerInvariant(); pauseItem.Enabled=snapshot.State==CompanionState.Running; resumeItem.Enabled=snapshot.State==CompanionState.Paused; profilesItem.DropDownItems.Clear(); foreach(var profile in snapshot.Profiles) { var item=new ToolStripMenuItem(profile){Checked=!snapshot.DisabledProfiles.Contains(profile)}; item.Click+=(s,e)=>Act(()=>operations.SetProfile(profile,!item.Checked)); profilesItem.DropDownItems.Add(item); } profilesItem.Enabled=profilesItem.DropDownItems.Count>0; }
	void Act(Action action) { try { action(); Refresh(); } catch(Exception ex) { MessageBox.Show(ex.Message,"HookLab Watcher operation failed",MessageBoxButtons.OK,MessageBoxIcon.Error); } }
	void ShowAudit() { try { var form=new Form { Text="HookLab Watcher audit log",Icon=HookIcon.Create(),Width=1000,Height=600,StartPosition=FormStartPosition.CenterScreen }; form.Controls.Add(new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,WordWrap=false,Text=String.Join(Environment.NewLine,operations.ReadAudit()) }); form.Show(); } catch(Exception ex) { MessageBox.Show(ex.Message,"HookLab Watcher audit log failed",MessageBoxButtons.OK,MessageBoxIcon.Error); } }
	protected override void ExitThreadCore() { timer.Dispose(); icon.Visible=false; icon.Dispose(); base.ExitThreadCore(); }
}

internal static class HookIcon {
	public static Icon Create() { using var bitmap=new Bitmap(16,16,System.Drawing.Imaging.PixelFormat.Format32bppArgb); using(var graphics=Graphics.FromImage(bitmap)) using(var pen=new Pen(Color.FromArgb(0,150,136),2f) { StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round }) { graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias; graphics.DrawEllipse(pen,6,1,6,6); graphics.DrawLine(pen,9,4,9,10); graphics.DrawBezier(pen,9,10,9,13,7,14,5,14); graphics.DrawBezier(pen,5,14,3,14,2,12.5f,2,11); graphics.DrawLine(pen,5,8,9,4); graphics.DrawLine(pen,5,8,4,7); } var handle=bitmap.GetHicon(); try { using var borrowed=Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { DestroyIcon(handle); } }
	[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
