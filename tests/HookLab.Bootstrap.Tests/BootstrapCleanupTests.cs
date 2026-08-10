using System;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	/// <summary>What a cleanup failure is allowed to leave behind, and what it must still say.
	///
	/// The defect these cover is not hypothetical bookkeeping: the previous Shutdown took both shared
	/// handles under the lock, nulled both, and then disposed runtime-then-server with nothing between
	/// them. A throwing unpatch skipped the endpoint teardown entirely and left an authenticated listener
	/// alive that no later Shutdown could reach - while the report said stopped.</summary>
	public class BootstrapCleanupTests {
		static ParameterBuilder Parameters(BootstrapRunner runner) =>
			ParameterBuilder.ForCurrentProcess().With("appdomain_id", runner.AppDomainId);

		static void StartCleanly(BootstrapRunner runner) {
			var report = Report.Parse(runner.Start(Parameters(runner).ToString()));
			Assert.True(report["status"] == "ok", "Bootstrap refused: " + report.Get("error_type") + " " + report.Get("error_message"));
		}

		[Fact]
		public void A_failing_unpatch_still_tears_the_endpoint_down_and_reports_the_failure() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-runtime-throws")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("runtime-fails-always");
				var shutdown = Report.Parse(runner.Shutdown());
				// The endpoint teardown is attempted regardless of what unpatching did.
				Assert.Equal("runtime_disposals=1;server_disposals=1", runner.FakeHandleState());
				Assert.Equal("completed", shutdown["endpoint_teardown"]);
				Assert.Equal("false", shutdown["endpoint_live"]);
				Assert.Equal("", shutdown.Get("endpoint_teardown_error"));
				// And the unpatch failure is reported rather than swallowed by the successful teardown.
				Assert.Equal("partial", shutdown["status"]);
				Assert.Contains("runtime-dispose-failed", shutdown["runtime_cleanup_error"], StringComparison.Ordinal);
				Assert.Equal("true", shutdown["cleanup_retry_possible"]);
			}
		}

		[Fact]
		public void A_retained_runtime_handle_is_retried_by_the_next_shutdown() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-runtime-retry")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("runtime-fails-once");
				var first = Report.Parse(runner.Shutdown());
				Assert.Equal("partial", first["status"]);
				Assert.Equal("true", first["cleanup_retry_possible"]);
				// A failed unpatch keeps the handle, so the retry has something to work with - and keeps this
				// bootstrap started, so the retry is not answered with "not_started".
				var second = Report.Parse(runner.Shutdown());
				Assert.Equal("ok", second["status"]);
				Assert.Equal("false", second["cleanup_retry_possible"]);
				Assert.Equal("", second.Get("runtime_cleanup_error"));
				// The runtime was retried; the endpoint, already torn down, was not disposed twice.
				Assert.Equal("runtime_disposals=2;server_disposals=1", runner.FakeHandleState());
			}
		}

		[Fact]
		public void A_failed_endpoint_teardown_is_never_reported_as_a_clean_stop_and_stays_retryable() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-server-throws")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("server-fails-once");
				var first = Report.Parse(runner.Shutdown());
				Assert.Equal("partial", first["status"]);
				Assert.Equal("true", first["endpoint_live"]);
				Assert.Equal("failed", first["endpoint_teardown"]);
				Assert.Contains("server-dispose-failed", first["endpoint_teardown_error"], StringComparison.Ordinal);
				// The resolver stays installed while the endpoint may be live: this is not a finished stop.
				Assert.Equal("true", first["resolver_installed"]);
				Assert.Equal("", first.Get("runtime_cleanup_error"));

				var second = Report.Parse(runner.Shutdown());
				Assert.Equal("ok", second["status"]);
				Assert.Equal("false", second["endpoint_live"]);
				Assert.Equal("completed", second["endpoint_teardown"]);
				Assert.Equal("runtime_disposals=1;server_disposals=2", runner.FakeHandleState());
			}
		}

		[Fact]
		public void Two_failing_disposals_are_both_reported_and_both_retried() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-both-throw")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("both-fail-always");
				var first = Report.Parse(runner.Shutdown());
				Assert.Equal("partial", first["status"]);
				Assert.Contains("runtime-dispose-failed", first["runtime_cleanup_error"], StringComparison.Ordinal);
				Assert.Contains("server-dispose-failed", first["endpoint_teardown_error"], StringComparison.Ordinal);
				Assert.Equal("true", first["endpoint_live"]);
				var second = Report.Parse(runner.Shutdown());
				Assert.Equal("partial", second["status"]);
				Assert.Equal("true", second["endpoint_live"]);
				Assert.Equal("runtime_disposals=2;server_disposals=2", runner.FakeHandleState());
			}
		}

		[Fact]
		public void A_startup_failure_survives_a_rollback_whose_unpatch_also_fails() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-startup-rollback")) {
				// The IL digest is the one method-guard field still decided by the probe's own guard, and it
				// refuses inside probe.Install - after the pipe server exists and after both shared handles
				// were published, which is precisely the window the rollback has to cope with.
				var parameters = Parameters(runner).WithHook().With("hook_il_sha256", new string('0', 64)).ToString();
				var report = Report.Parse(runner.StartWithFailingRollback(parameters));
				Assert.Equal("error", report["status"]);
				// The reason the bootstrap was refused is not replaced by the cleanup exception.
				Assert.Contains("GuardMismatchException", report["error_type"], StringComparison.Ordinal);
				Assert.Contains("il_sha256", report["error_message"], StringComparison.Ordinal);
				// And the cleanup failure is not lost either, nor does it stop the endpoint teardown.
				Assert.Contains("unpatch-failed-on-purpose", report["runtime_cleanup_error"], StringComparison.Ordinal);
				Assert.Equal("completed", report["endpoint_teardown"]);
				Assert.Equal("false", report["endpoint_live"]);
			}
		}
	}
}
