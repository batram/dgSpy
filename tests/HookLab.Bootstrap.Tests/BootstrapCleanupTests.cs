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

		/// <summary>The ordinary path, against the real pipe server rather than a double. ProbeStartup asks
		/// the endpoint for its quiescence reflectively - it must not name a probe type on a path that runs
		/// when the payload never loaded - so a rename or a signature change on the probe side would silently
		/// degrade every report to not_required and no other test would notice.</summary>
		[Fact]
		public void A_clean_shutdown_reports_the_real_endpoint_as_quiesced() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-real-endpoint-quiesced")) {
				StartCleanly(runner);
				var shutdown = Report.Parse(runner.Shutdown());
				Assert.Equal("ok", shutdown["status"]);
				Assert.Equal("completed", shutdown["endpoint_teardown"]);
				Assert.Equal("quiesced", shutdown["command_quiescence"]);
			}
		}

		/// <summary>The state the teardown result cannot express on its own: the endpoint is down and
		/// unreachable, and a command is still inside the handler, possibly still mutating the target.
		/// Reporting that as a clean stop is what would let T09d's rollback answer
		/// cleanup_outcome=completed over a probe that is still patching.</summary>
		[Fact]
		public void A_command_still_inside_the_handler_is_not_a_clean_stop() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-command-in-flight")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("server-in-flight-once");
				var first = Report.Parse(runner.Shutdown());
				// The endpoint really is down - this is not a failed teardown wearing a different name.
				Assert.Equal("completed", first["endpoint_teardown"]);
				Assert.Equal("false", first["endpoint_live"]);
				Assert.Equal("", first.Get("endpoint_teardown_error"));
				// And it is still not a clean stop, because the probe has not stopped working.
				Assert.Equal("in_flight", first["command_quiescence"]);
				Assert.Equal("partial", first["status"]);
				Assert.Equal("true", first["cleanup_retry_possible"]);
				Assert.Equal("true", first["resolver_installed"]);

				// The retry re-asks the endpoint rather than trusting what the last attempt recorded.
				var second = Report.Parse(runner.Shutdown());
				Assert.Equal("quiesced", second["command_quiescence"]);
				Assert.Equal("ok", second["status"]);
				Assert.Equal("false", second["cleanup_retry_possible"]);
				Assert.Equal("runtime_disposals=1;server_disposals=2", runner.FakeHandleState());
			}
		}

		[Fact]
		public void A_start_over_a_probe_that_is_still_running_a_command_is_refused() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-command-in-flight-refuses-restart")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("server-in-flight-always");
				Assert.Equal("partial", Report.Parse(runner.Shutdown())["status"]);
				// Initializing over it would publish a second runtime and a second endpoint while the first
				// probe is still mutating the target, and orphan whatever the running command was doing.
				var restart = Report.Parse(runner.Start(Parameters(runner).ToString()));
				Assert.Equal("error", restart["status"]);
				// already_started rather than cleanup_pending: an incomplete stop leaves this bootstrap
				// started, and that check comes first. Either refusal is correct here - what matters is that
				// a caller reading only this report can see why, and that the refusal is not conditional on
				// the started state, which a failed start does not have (see the failed-start cases below).
				Assert.Equal("already_started", restart["error_type"]);
				Assert.Equal("in_flight", restart["command_quiescence"]);
				Assert.Equal("true", restart["cleanup_retry_possible"]);
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
				// The runtime handle was kept, and the refusal report says so - a caller who only ever sees
				// this report has to be able to learn that a Shutdown still has work to do.
				Assert.Equal("true", report["cleanup_retry_possible"]);
			}
		}

		[Fact]
		public void A_retry_that_succeeds_retains_nothing_so_the_bootstrap_can_be_started_again() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-restart-after-successful-retry")) {
				StartCleanly(runner);
				runner.InstallFakeHandles("both-fail-once");
				var first = Report.Parse(runner.Shutdown());
				Assert.Equal("partial", first["status"]);
				Assert.Equal("true", first["cleanup_retry_possible"]);
				var shutdown = Report.Parse(runner.Shutdown());
				Assert.Equal("ok", shutdown["status"]);
				Assert.Equal("false", shutdown["cleanup_retry_possible"]);
				// Retention is what refuses a restart, so a retention flag that outlived the handle it
				// described would make cleanup_pending permanent for this AppDomain.
				var restart = Report.Parse(runner.Start(Parameters(runner).ToString()));
				Assert.NotEqual("cleanup_pending", restart.Get("error_type"));
				Assert.True(restart["status"] == "ok", "Restart refused: " + restart.Get("error_type") + " " + restart.Get("error_message"));
			}
		}

		/// <summary>The failed-start hole. Every retry case above starts *successfully* first, so the started
		/// state that Shutdown was gated on is populated and the gate is invisible to them.
		///
		/// Here the start publishes both handles and then fails, and neither disposal can be made to work. The
		/// bootstrap therefore holds a runtime that may still be patched and an endpoint that may still be
		/// accepting connections, with no started state to advertise them. A Shutdown gated on started state
		/// answered "not_started" and retried nothing.</summary>
		static string StartFailingAfterBothHandlesPublished(BootstrapRunner runner) =>
			// hook_il_sha256 is refused by ProbeRuntime.Install's own guard, which runs after the pipe server
			// exists and after both shared handles were published - the window the rollback has to cope with.
			runner.StartWithBothRollbackDisposalsFailing(
				Parameters(runner).WithHook().With("hook_il_sha256", new string('0', 64)).ToString());

		[Fact]
		public void A_shutdown_after_a_failed_start_retries_the_retained_cleanup_instead_of_reporting_not_started() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-failed-start-retry")) {
				var start = Report.Parse(StartFailingAfterBothHandlesPublished(runner));
				Assert.Equal("error", start["status"]);
				Assert.Contains("GuardMismatchException", start["error_type"], StringComparison.Ordinal);
				// Both cleanup failures are reported beside the startup failure, and none of the three erases
				// the others.
				Assert.Contains("unpatch-failed-on-purpose", start["runtime_cleanup_error"], StringComparison.Ordinal);
				Assert.Contains("endpoint-teardown-failed-on-purpose", start["endpoint_teardown_error"], StringComparison.Ordinal);
				Assert.Equal("failed", start["endpoint_teardown"]);
				Assert.Equal("true", start["endpoint_live"]);
				Assert.Equal("true", start["cleanup_retry_possible"]);
				// This rig's endpoint has no quiescence contract at all, and an absent question is not an
				// answer to it: not_required rather than quiesced, and the endpoint_live line above is what
				// keeps the report honest here.
				Assert.Equal("not_required", start["command_quiescence"]);
				// Rollback already retried once: one attempt inside ProbeStartup.Run, one from the rollback.
				Assert.Equal("runtime_disposals=2;server_disposals=2", runner.RollbackFaultState());

				var shutdown = Report.Parse(runner.Shutdown());
				// The point of the case: this is not a bootstrap that never ran.
				Assert.NotEqual("not_started", shutdown.Get("error_type"));
				Assert.Equal("partial", shutdown["status"]);
				Assert.Equal("failed", shutdown["endpoint_teardown"]);
				Assert.Equal("true", shutdown["endpoint_live"]);
				Assert.Equal("true", shutdown["cleanup_retry_possible"]);
				Assert.Contains("unpatch-failed-on-purpose", shutdown["runtime_cleanup_error"], StringComparison.Ordinal);
				Assert.Contains("endpoint-teardown-failed-on-purpose", shutdown["endpoint_teardown_error"], StringComparison.Ordinal);
				// It really retried the retained pair rather than merely describing it.
				Assert.Equal("runtime_disposals=3;server_disposals=3", runner.RollbackFaultState());
			}
		}

		[Fact]
		public void A_start_over_an_unresolved_previous_attempt_is_refused_rather_than_orphaning_it() {
			using (var runner = BootstrapRunner.Create("bootstrap-cleanup-failed-start-refuses-restart")) {
				Assert.Equal("error", Report.Parse(StartFailingAfterBothHandlesPublished(runner))["status"]);
				Assert.Equal("runtime_disposals=2;server_disposals=2", runner.RollbackFaultState());

				// The rig is disarmed, so an unrefused start would create a real second runtime and a real
				// second listener and overwrite both shared handles - which is exactly the orphaning.
				var second = Report.Parse(runner.Start(Parameters(runner).WithHook().ToString()));
				Assert.Equal("error", second["status"]);
				// Distinguishable from already_started on purpose: the two need opposite responses - one says
				// a bootstrap is running, the other says an old one has not finished dying.
				Assert.Equal("cleanup_pending", second["error_type"]);
				Assert.NotEqual("already_started", second["error_type"]);
				Assert.Equal("true", second["endpoint_live"]);
				Assert.Equal("true", second["cleanup_retry_possible"]);
				// The refusal only stands because the retry it made first got nowhere; the retained pair is
				// still the original one.
				Assert.Equal("runtime_disposals=3;server_disposals=3", runner.RollbackFaultState());
			}
		}
	}
}
