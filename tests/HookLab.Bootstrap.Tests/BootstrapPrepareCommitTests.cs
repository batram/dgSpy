using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	public sealed class BootstrapPrepareCommitTests {
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
