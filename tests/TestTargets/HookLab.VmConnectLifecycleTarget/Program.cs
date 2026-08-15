using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HookLab.Bootstrap;

namespace HookLab.VmConnectLifecycleTarget {
	internal static class Program {
		[STAThread]
		static int Main(string[] arguments) {
			var reportPath = arguments.Length >= 1 ? Path.GetFullPath(arguments[0]) : Path.Combine(Path.GetTempPath(), "HookLab.VmConnectLifecycleTarget.report.txt");
			var mode = arguments.Length >= 2 ? arguments[1] : "delayed-ready";
			var readinessDelay = arguments.Length >= 3 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 750;
			if (mode != "delayed-ready" && mode != "never-ready") throw new ArgumentException("Mode must be delayed-ready or never-ready.");
			if (readinessDelay < 25 || readinessDelay > 2200) throw new ArgumentOutOfRangeException(nameof(readinessDelay));
			try {
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				var transcript = new Transcript();
				using (var viewer = new Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl(transcript, mode, readinessDelay)) {
					viewer.EnsureHandle();
					InstallExactHook(viewer, transcript);
					var timeout = new System.Windows.Forms.Timer { Interval = 10000 };
					timeout.Tick += delegate { timeout.Stop(); transcript.Fail("dispatcher_timeout"); Application.ExitThread(); };
					timeout.Start();
					var callback = new Thread(viewer.RaiseOnLoginComplete) { IsBackground = true, Name = "RDP login callback mirror" };
					callback.SetApartmentState(ApartmentState.STA);
					callback.Start();
					Application.Run();
					timeout.Dispose();
					File.WriteAllText(reportPath, transcript.Render(), new UTF8Encoding(false));
					return transcript.Passed ? 0 : 1;
				}
			}
			catch (Exception ex) {
				File.WriteAllText(reportPath, "status=error\nexception=" + ex + "\n", new UTF8Encoding(false));
				return 2;
			}
		}

		static void InstallExactHook(Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl viewer, Transcript transcript) {
			var method = typeof(Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl).GetMethod(nameof(Microsoft.Virtualization.Client.InteractiveSession.RdpViewerControl.SyncDisplaySettings))!;
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vmconnect-lifecycle-completion");
			try {
				var source = ExactHookSource();
				var parameters = Parameters(method, source, completion);
				var prepared = HookLabBootstrap.Prepare(parameters);
				if (!prepared.StartsWith("status=ok\n", StringComparison.Ordinal)) throw new InvalidOperationException("Prepare failed: " + prepared);
				var watch = Stopwatch.StartNew();
				var committed = HookLabBootstrap.Commit();
				if (!committed.StartsWith("status=ok\n", StringComparison.Ordinal)) throw new InvalidOperationException("Commit failed: " + committed);
				while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
				if (!File.Exists(completion)) throw new TimeoutException("The lifecycle target hook did not become ready within five seconds.");
				var report = File.ReadAllText(completion);
				if (!report.StartsWith("status=ok\n", StringComparison.Ordinal)) throw new InvalidOperationException("Hook install failed: " + report);
				transcript.Add("hook_ready", "elapsed_ms=" + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		static string ExactHookSource() {
			var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vmconnect-fullscreen.json"));
			var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object> ?? throw new InvalidDataException("Definition root is invalid.");
			var hook = root["hook"] as Dictionary<string, object> ?? throw new InvalidDataException("Definition hook is invalid.");
			return (string)hook["source"];
		}

		static string Parameters(MethodInfo method, string source, string completion) {
			using (var process = Process.GetCurrentProcess()) {
				var values = new Dictionary<string, string> {
					["host_id"] = "vmconnect-lifecycle-target", ["image_path"] = process.MainModule!.FileName,
					["process_id"] = process.Id.ToString(CultureInfo.InvariantCulture), ["process_creation_utc_ticks"] = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
					["architecture"] = "x64", ["runtime_id"] = System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion(), ["appdomain_id"] = AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture),
					["endpoint"] = "none", ["completion_path"] = completion, ["hook_id"] = "vmconnect-lifecycle-first-login", ["hook_kind"] = "Prefix",
					["hook_assembly"] = method.Module.Assembly.GetName().Name!, ["hook_type"] = method.DeclaringType!.FullName!, ["hook_method"] = method.Name,
					["hook_module_mvid"] = method.Module.ModuleVersionId.ToString("D"), ["hook_metadata_token"] = unchecked((uint)method.MetadataToken).ToString(CultureInfo.InvariantCulture),
					["hook_declaring_type"] = method.DeclaringType.FullName!, ["hook_method_signature"] = Signature(method), ["hook_il_sha256"] = IlSha256(method),
					["hook_source_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)), ["hook_revision"] = "1", ["maximum_events_per_second"] = "100", ["maximum_string_length"] = "1024"
				};
				return string.Join("\n", values.Select(pair => pair.Key + "=" + pair.Value)) + "\n";
			}
		}

		static string Signature(MethodInfo method) => (method.ReturnType.FullName ?? method.ReturnType.Name) + " " + method.Name + "(" + string.Join(",", method.GetParameters().Select(value => value.ParameterType.FullName ?? value.ParameterType.Name)) + ")";
		static string IlSha256(MethodInfo method) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(method.GetMethodBody()!.GetILAsByteArray()).Select(value => value.ToString("x2", CultureInfo.InvariantCulture))); }
	}

	public sealed class Transcript {
		readonly Stopwatch clock = Stopwatch.StartNew();
		readonly List<string> lines = new List<string>();
		public bool Passed { get; private set; }
		public void Add(string name, string detail = "") { lines.Add(clock.ElapsedTicks.ToString(CultureInfo.InvariantCulture) + "|" + name + "|" + detail); }
		public void Pass() { Passed = true; Add("pass"); }
		public void Fail(string reason) { Passed = false; Add("fail", reason); }
		public string Render() => "status=" + (Passed ? "ok" : "error") + "\n" + string.Join("\n", lines) + "\n";
	}
}

namespace Microsoft.Virtualization.Client.Interop {
	public sealed class IMsRdpClient9Mirror {
		readonly HookLab.VmConnectLifecycleTarget.Transcript transcript;
		readonly Func<bool> connectionSetterActive;
		public IMsRdpClient9Mirror(HookLab.VmConnectLifecycleTarget.Transcript transcript, Func<bool> connectionSetterActive) { this.transcript = transcript; this.connectionSetterActive = connectionSetterActive; Location = new System.Drawing.Point(7, 9); }
		public System.Drawing.Point Location { get; set; }
		public bool Ready { get; set; }
		public int Attempts { get; private set; }
		public int Successes { get; private set; }
		public void SyncSessionDisplaySettings() {
			Attempts++; transcript.Add("rdp_sync", "connection_setter_active=" + connectionSetterActive());
			if (!Ready) throw new NullReferenceException("Mirror COM session display object is not ready.");
			Successes++;
		}
	}
}

namespace Microsoft.Virtualization.Client.InteractiveSession {
	public sealed class RdpViewerControl : Control {
		readonly HookLab.VmConnectLifecycleTarget.Transcript transcript;
		readonly Microsoft.Virtualization.Client.Interop.IMsRdpClient9Mirror m_RdpClient;
		bool connectionSetterActive;
		bool firstResult;
		readonly string mode;
		readonly int readinessDelay;
		System.Threading.Timer? readiness;
		System.Threading.Timer? verification;
		public RdpViewerControl(HookLab.VmConnectLifecycleTarget.Transcript transcript, string mode, int readinessDelay) { this.transcript = transcript; this.mode = mode; this.readinessDelay = readinessDelay; m_RdpClient = new Microsoft.Virtualization.Client.Interop.IMsRdpClient9Mirror(transcript, () => connectionSetterActive); }
		public bool FullScreen { get; private set; }
		public int OriginalCalls { get; private set; }
		public void EnsureHandle() { CreateControl(); var ignored = Handle; }
		public void RaiseOnLoginComplete() {
			transcript.Add("login_complete_enter"); connectionSetterActive = true; FullScreen = true;
			if (mode == "delayed-ready") readiness = new System.Threading.Timer(delegate { m_RdpClient.Ready = true; transcript.Add("session_display_ready"); }, null, readinessDelay, Timeout.Infinite);
			try { firstResult = SyncDisplaySettings(); transcript.Add("first_sync_return", "result=" + firstResult); }
			finally { connectionSetterActive = false; transcript.Add("connection_setter_exit"); }
			verification = new System.Threading.Timer(delegate { BeginInvoke((Action)VerifyAfterDispatcher); }, null, mode == "delayed-ready" ? readinessDelay + 600 : 3200, Timeout.Infinite);
		}
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public bool SyncDisplaySettings() {
			OriginalCalls++; transcript.Add("original_sync_enter", "connection_setter_active=" + connectionSetterActive);
			try { m_RdpClient.SyncSessionDisplaySettings(); return true; }
			catch (NullReferenceException) { transcript.Add("original_sync_nre"); return false; }
		}
		void VerifyAfterDispatcher() {
			readiness?.Dispose(); verification?.Dispose();
			transcript.Add("verify", "first_result=" + firstResult + ",original_calls=" + OriginalCalls + ",attempts=" + m_RdpClient.Attempts + ",successes=" + m_RdpClient.Successes + ",location=" + m_RdpClient.Location.X + "," + m_RdpClient.Location.Y);
			if (mode == "delayed-ready" && firstResult && OriginalCalls == 0 && m_RdpClient.Successes == 1 && m_RdpClient.Location == System.Drawing.Point.Empty) transcript.Pass();
			else if (mode == "never-ready" && !firstResult && OriginalCalls == 0 && m_RdpClient.Attempts == 20 && m_RdpClient.Successes == 0 && m_RdpClient.Location == new System.Drawing.Point(7, 9)) transcript.Pass();
			else transcript.Fail("first-login repair contract was not met");
			Application.ExitThread();
		}
	}
}
