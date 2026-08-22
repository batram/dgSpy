using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Text;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	public sealed class BootstrapPrepareCommitTests {
		readonly Xunit.Abstractions.ITestOutputHelper output;
		public BootstrapPrepareCommitTests(Xunit.Abstractions.ITestOutputHelper output) { this.output = output; }
		static ParameterBuilder Parameters(BootstrapRunner runner, string completion) => ParameterBuilder.ForCurrentProcess()
			.With("appdomain_id", runner.AppDomainId).With("endpoint", "none").With("completion_path", completion);

		[Fact]
		public void Endpoint_is_required_and_none_prepares_without_a_pipe() {
			using (var runner = BootstrapRunner.Create("bootstrap-endpoint-required")) {
				var missing = Parameters(runner, Path.GetTempFileName()).With("endpoint", "none").ToString().Replace("endpoint=none\n", "");
				var refused = Report.Parse(runner.Prepare(missing));
				Assert.Equal("error", refused["status"]);
				Assert.Contains("endpoint", refused["error_message"], StringComparison.Ordinal);
			}
		}

		[Fact]
		public void Native_entry_publishes_a_prepare_refusal_before_any_worker_exists() {
			var directory=Path.Combine(Path.GetTempPath(),"hooklab-native-refusal-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
			try {
				var completion=Path.Combine(directory,"completion.txt"); var parameters=Path.Combine(directory,"initialize.params");
				using(var runner=BootstrapRunner.Create("bootstrap-native-refusal")) {
					File.WriteAllText(parameters,Parameters(runner,completion).ToString().Replace("endpoint=none\n",""),new UTF8Encoding(false));
					Assert.Equal(2,runner.NativeInitialize(parameters));
				}
				var report=Report.Parse(File.ReadAllText(completion)); Assert.Equal("error",report["status"]); Assert.Contains("endpoint",report["error_message"],StringComparison.Ordinal);
			}
			finally { try { Directory.Delete(directory,true); } catch { } }
		}

		[Fact]
		public void A_second_byte_loaded_bootstrap_generation_is_refused_in_the_same_appdomain() {
			using(var runner=BootstrapRunner.Create("bootstrap-generation-rendezvous")) {
				var first=Report.Parse(runner.Prepare(Parameters(runner,Path.GetTempFileName()).With("endpoint","none").ToString())); Assert.Equal("ok",first["status"]);
				var second=Report.Parse(runner.StartByteLoaded(Parameters(runner,Path.GetTempFileName()).With("endpoint","none").ToString())); Assert.Equal("error",second["status"]); Assert.Contains("resident_generation_present",second["error_message"],StringComparison.Ordinal);
			}
		}

		[Fact]
		public void Pipe_endpoint_identity_survives_prepare_to_async_completion() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-pipe-prepare-commit")) {
					var prepared = Report.Parse(runner.Prepare(Parameters(runner, completion).With("endpoint", "pipe").ToString()));
					Assert.False(string.IsNullOrWhiteSpace(prepared["pipe_name"]));
					runner.Commit();
					var watch = Stopwatch.StartNew(); while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
					var completed = Report.Parse(File.ReadAllText(completion));
					Assert.Equal(prepared["pipe_name"], completed["pipe_name"]);
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		[Fact]
		public void Host_supplied_pipe_secret_is_accepted_but_never_reported_outward() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-injected-pipe-secret")) {
					var secret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
					var prepared = Report.Parse(runner.Prepare(Parameters(runner, completion).With("endpoint", "pipe").With("endpoint_secret_base64", Convert.ToBase64String(secret)).ToString()));
					Assert.Equal("ok", prepared["status"]); Assert.False(string.IsNullOrWhiteSpace(prepared["pipe_name"])); Assert.False(string.IsNullOrWhiteSpace(prepared["pipe_nonce_base64"]));
					Assert.DoesNotContain(prepared.Keys, key => key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0);
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		/// <summary>A target running under a different account than its debugger builds a control pipe whose
		/// protected DACL names only the target's own SID, so the debugger is denied on a pipe that was created
		/// for it. Naming the controller grants it. The same-user case, which is what this test can run, must
		/// keep working unchanged - the probe drops a controller that is already the pipe's owner.</summary>
		[Fact]
		public void A_named_controller_is_accepted_and_changes_nothing_when_it_is_the_current_account() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-controller-sid")) {
					var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
					var prepared = Report.Parse(runner.Prepare(Parameters(runner, completion).With("endpoint", "pipe").With("controller_sid", sid).ToString()));
					Assert.Equal("ok", prepared["status"]);
					Assert.False(string.IsNullOrWhiteSpace(prepared["pipe_name"]));
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		[Theory]
		[InlineData("none", "S-1-5-18")]
		[InlineData("pipe", "not-a-sid")]
		[InlineData("pipe", "S-1-5-")]
		public void Controller_sid_requires_pipe_and_a_well_formed_sid(string endpoint, string sid) {
			using (var runner = BootstrapRunner.Create("bootstrap-invalid-controller-" + endpoint + "-" + sid.Length)) {
				var report = Report.Parse(runner.Prepare(Parameters(runner, Path.GetTempFileName()).With("endpoint", endpoint).With("controller_sid", sid).ToString()));
				Assert.Equal("error", report["status"]);
			}
		}

		[Theory]
		[InlineData("none", "AQID")]
		[InlineData("pipe", "not-base64")]
		[InlineData("pipe", "AQID")]
		public void Endpoint_secret_requires_pipe_and_exact_base64_secret(string endpoint, string secret) {
			using (var runner = BootstrapRunner.Create("bootstrap-invalid-pipe-secret-" + endpoint + "-" + secret.Length)) {
				var report = Report.Parse(runner.Prepare(Parameters(runner, Path.GetTempFileName()).With("endpoint", endpoint).With("endpoint_secret_base64", secret).ToString()));
				Assert.Equal("error", report["status"]);
			}
		}

		[Fact]
		public void Prepare_and_idempotent_commit_publish_completion_out_of_band() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-prepare-commit")) {
					var prepared = Report.Parse(runner.Prepare(Parameters(runner, completion).WithHook("async-hook").ToString()));
					Assert.Equal("ok", prepared["status"]);
					Assert.Equal("completed", prepared["residency_commit"]);
					Assert.Equal("not_started", prepared["behavior_commit"]);
					Assert.Equal(ResidentLauncher.GenerationIdentity, prepared["generation_identity"]);
					Assert.Contains("endpoint_none", prepared["prototype_compromises"], StringComparison.Ordinal);
					Assert.DoesNotContain("pipe_name", prepared.Keys);
					Assert.Equal(0, runner.PipeConstructions);

					var watch = Stopwatch.StartNew();
					Assert.Equal("true", Report.Parse(runner.Commit())["worker_started"]);
					Assert.Equal("false", Report.Parse(runner.Commit())["worker_started"]);
					Assert.Equal(1, runner.WorkerStarts);
					Assert.Equal(new[] { 0, 0, 0 }, runner.CommitInstrumentation);
					while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
					Assert.True(File.Exists(completion), "Worker did not publish completion.");
					var report = Report.Parse(File.ReadAllText(completion));
					Assert.Equal("ok", report["status"]);
					Assert.Equal("completed", report["behavior_commit"]);
					Assert.Contains("async-hook", report["patch_id"], StringComparison.Ordinal);
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		[Fact]
		public void One_shot_commit_installs_compiled_source_instead_of_observer() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-one-shot-compiled")) {
					const string source = "public static class H{public static bool Prefix(ref int __result){__result=123;return false;}}";
					var parameters = Parameters(runner, completion).WithHook("compiled-one-shot")
						.With("hook_source_base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(source))).With("hook_revision", "1").ToString();
					runner.Prepare(parameters);
					runner.Commit();
					var watch = Stopwatch.StartNew(); while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
					var report = Report.Parse(File.ReadAllText(completion));
					Assert.Equal("ok", report["status"]);
					Assert.Contains("compiled-one-shot", report["patch_id"], StringComparison.Ordinal);
					Assert.Equal(new[] { 123, 123 }, runner.InvokeFixture(2));
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		[Fact]
		public void Cold_one_shot_timing_reports_install_and_patches_the_first_call() {
			for (var sample = 1; sample <= 5; sample++) {
				var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
				try {
					using (var runner = BootstrapRunner.Create("bootstrap-one-shot-timing-" + sample)) {
						const string source = "public static class H{public static bool Prefix(ref int __result){__result=123;return false;}}";
						var parameters = Parameters(runner, completion).WithHook("compiled-timing-" + sample)
							.With("hook_source_base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(source))).With("hook_revision", "1").ToString();
						runner.Prepare(parameters);
						var endToEnd = Stopwatch.StartNew();
						runner.Commit();
						while (!File.Exists(completion) && endToEnd.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
						Assert.True(File.Exists(completion), "Cold worker did not complete within ten seconds.");
						var report = Report.Parse(File.ReadAllText(completion));
						var calls = runner.MeasureFixtureCalls();
						output.WriteLine("sample={0} end_to_end_ms={1} queue_ms={2} behavior_ms={3} first_result={4} first_ticks={5} second_result={6} second_ticks={7}", sample, endToEnd.ElapsedMilliseconds, report["worker_queue_ms"], report["behavior_elapsed_ms"], calls[0], calls[1], calls[2], calls[3]);
						Assert.Equal(123, calls[0]);
						Assert.Equal(123, calls[2]);
					}
				}
				finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
			}
		}

		[Fact]
		public void Calls_during_async_install_are_not_retroactively_hooked_but_completion_is_a_ready_barrier() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-one-shot-race")) {
					const string source = "public static class H{public static bool Prefix(ref int __result){__result=123;return false;}}";
					var parameters = Parameters(runner, completion).WithHook("compiled-race")
						.With("hook_source_base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(source))).With("hook_revision", "1").ToString();
					runner.Prepare(parameters);
					var watch = Stopwatch.StartNew();
					runner.Commit();
					var duringInstall = runner.InvokeFixture(1)[0];
					while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(5);
					Assert.True(File.Exists(completion), "Worker did not publish its ready barrier.");
					var afterCompletion = runner.InvokeFixture(1)[0];
					output.WriteLine("during_install_result={0} after_completion_result={1} ready_ms={2}", duringInstall, afterCompletion, watch.ElapsedMilliseconds);
					Assert.Equal(8, duringInstall);
					Assert.Equal(123, afterCompletion);
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}

		[Fact]
		public void Reports_contain_neither_secret_fields_nor_secret_shaped_hex() {
			using (var runner = BootstrapRunner.Create("bootstrap-no-secret")) {
				var text = runner.Start(ParameterBuilder.ForCurrentProcess().With("appdomain_id", runner.AppDomainId).ToString());
				Assert.DoesNotContain("secret_base64", text, StringComparison.Ordinal);
				Assert.DoesNotContain("secret_hex", text, StringComparison.Ordinal);
				Assert.DoesNotMatch(new Regex("(?im)^[^=]*secret[^=]*=.*$"), text);
			}
		}

		[Fact]
		public void DrainEvents_is_bounded_and_reports_truthful_drops() {
			var completion = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hooklab-completion");
			try {
				using (var runner = BootstrapRunner.Create("bootstrap-drain")) {
					runner.Prepare(Parameters(runner, completion).With("event_capacity", "2").WithHook("drain-hook").ToString());
					runner.Commit();
					var watch = Stopwatch.StartNew(); while (!File.Exists(completion) && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
					Assert.Equal(new[] { 8, 9, 10, 11 }, runner.InvokeFixture(4));
					var drained = Report.Parse(runner.DrainEvents(1));
					Assert.Equal("1", drained["count"]);
					Assert.Equal("2", drained["dropped"]);
					Assert.Contains("drain-hook", drained["event_0"], StringComparison.Ordinal);
				}
			}
			finally { try { File.Delete(completion); File.Delete(completion + ".tmp"); } catch { } }
		}
	}
}
