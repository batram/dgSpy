using System;
using System.Collections.Generic;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	/// <summary>Hook target selection with more than one same-named assembly resident.
	///
	/// Selecting by simple name and letting the MVID guard refuse afterwards is an availability defect, not
	/// a guard bypass: the guard still refuses the wrong method. What it cannot do is put the right one
	/// back on the table, so a fully valid guarded request failed or succeeded depending on enumeration
	/// order.</summary>
	public class HookTargetSelectionTests {
		static ParameterBuilder Parameters(BootstrapRunner runner) =>
			ParameterBuilder.ForCurrentProcess().With("appdomain_id", runner.AppDomainId);

		static IDictionary<string, string> Evidence(string value) => Report.Parse(value.Replace(';', '\n'));

		[Fact]
		public void The_module_mvid_selects_the_target_rather_than_enumeration_order() {
			using (var runner = BootstrapRunner.Create("bootstrap-hook-target-mvid")) {
				var loaded = Evidence(runner.LoadDuplicateHookTargets("distinct"));
				Assert.Equal("true", loaded["distinct_assemblies"]);
				var order = loaded["enumeration_order"].Split(',');
				// Three same-named assemblies: the disk-loaded original and the two byte-loaded copies. The
				// fixture is only meaningful if the requested one is not the one a first-match would pick.
				Assert.Equal(3, order.Length);
				Assert.NotEqual(loaded["target_mvid"], order[0]);

				var report = Report.Parse(runner.StartWithDuplicateHookTarget(Parameters(runner).ToString(), "duplicate-hook", ""));
				Assert.True(report["status"] == "ok", "Bootstrap refused: " + report.Get("error_type") + " " + report.Get("error_message"));
				Assert.Contains("duplicate-hook", report["patch_id"], StringComparison.Ordinal);

				// Which copy got patched, measured rather than assumed: both copies really run (their own
				// static counters say so), and only the selected one produces hook events.
				var observed = Evidence(runner.InvokeCopiesAndDrain(2, 3));
				Assert.Equal("2", observed["decoy_observed"]);
				Assert.Equal("3", observed["target_observed"]);
				Assert.Equal("3", observed["events"]);
			}
		}

		[Fact]
		public void A_module_mvid_nobody_has_is_refused_as_not_found() {
			using (var runner = BootstrapRunner.Create("bootstrap-hook-target-zero-match")) {
				runner.LoadDuplicateHookTargets("distinct");
				var report = Report.Parse(runner.StartWithDuplicateHookTarget(
					Parameters(runner).ToString(), "zero-match-hook", Guid.NewGuid().ToString("D")));
				Assert.Equal("error", report["status"]);
				Assert.Contains("System.InvalidOperationException", report["error_type"], StringComparison.Ordinal);
				Assert.Contains("No loaded assembly matches", report["error_message"], StringComparison.Ordinal);
				Assert.Contains("hook_module_mvid", report["error_message"], StringComparison.Ordinal);
			}
		}

		[Fact]
		public void Two_modules_with_one_mvid_are_refused_as_ambiguous() {
			using (var runner = BootstrapRunner.Create("bootstrap-hook-target-ambiguous")) {
				var loaded = Evidence(runner.LoadDuplicateHookTargets("same-mvid"));
				Assert.Equal("true", loaded["distinct_assemblies"]);
				Assert.Equal(loaded["decoy_mvid"], loaded["target_mvid"]);
				var report = Report.Parse(runner.StartWithDuplicateHookTarget(Parameters(runner).ToString(), "ambiguous-hook", ""));
				Assert.Equal("error", report["status"]);
				Assert.Contains("System.InvalidOperationException", report["error_type"], StringComparison.Ordinal);
				Assert.Contains("Ambiguous hook target", report["error_message"], StringComparison.Ordinal);
				Assert.Contains("2 loaded assemblies", report["error_message"], StringComparison.Ordinal);
			}
		}
	}
}
