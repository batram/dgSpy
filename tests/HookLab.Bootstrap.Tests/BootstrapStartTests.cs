using System;
using System.Globalization;
using System.Linq;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	/// <summary>End-to-end, in-process, with no debugger involved - the shape T04 and T05 were tested in.
	/// Every case runs in its own AppDomain; see BootstrapRunner for why.</summary>
	public class BootstrapStartTests {
		static ParameterBuilder Parameters(BootstrapRunner runner) =>
			ParameterBuilder.ForCurrentProcess().With("appdomain_id", runner.AppDomainId);

		[Fact]
		public void Listener_startup_preserves_the_classified_exception_and_its_cause() {
			var cause = new DllNotFoundException("Unable to load DLL 'System.Native'");
			var classified = new PlatformNotSupportedException("The runtime cannot host the endpoint.", cause);
			var propagated = HookLab.Bootstrap.ProbeStartup.ListenerStartupFailure(classified);
			Assert.Same(classified, propagated);
			Assert.Same(cause, propagated.InnerException);
		}

		[Fact]
		public void A_guarded_install_resolves_the_graph_from_bytes_and_observes_an_event() {
			using (var runner = BootstrapRunner.Create("bootstrap-happy-path")) {
				var report = Report.Parse(runner.Start(Parameters(runner).WithHook().ToString()));
				Assert.True(report["status"] == "ok", "Bootstrap refused: " + report.Get("error_type") + " " + report.Get("error_message"));
				Assert.False(string.IsNullOrWhiteSpace(report["probe_instance_id"]));
				Assert.Equal("1", report["protocol_version"]);
				Assert.Equal("HookLab.Compat,HookLab.Contracts,HookLab.Probe.CorDebug,Microsoft.CodeAnalysis,Microsoft.CodeAnalysis.CSharp,System.Buffers,System.Collections.Immutable,System.Memory,System.Reflection.Metadata,System.Runtime.CompilerServices.Unsafe", report["payload_identities"]);
				// Five, not the two the probe alone needs: verifying what each payload identity binds to means
				// byte-loading each one before payload code runs. That is the price of checking a binding
				// while nothing has acted on it, and it is paid once per initialization.
				Assert.Equal("10", report["payload_load_count"]);
				Assert.False(string.IsNullOrWhiteSpace(report["pipe_name"]));
				Assert.DoesNotContain("secret_base64", report.Keys);
				Assert.DoesNotContain("endpoint_nonce_base64", report.Keys);
				Assert.Contains("fixture-hook", report["patch_id"], StringComparison.Ordinal);

				// The whole graph is resident and file-less. 0Harmony arrives through the probe's own pinned
				// backend loader, not through this bootstrap's manifest, and it is byte-loaded too.
				var resident = runner.ResidentPayloads();
				Assert.Equal(new[] { "0Harmony|byte-loaded", "HookLab.Contracts|byte-loaded", "HookLab.Probe.CorDebug|byte-loaded" }, resident);

				// The backend inventory ran before any Harmony type resolved. If the bootstrap had touched
				// HarmonyLib on the way in, 0Harmony would appear in the identities the inventory saw, and
				// the probe's resident-backend check would have inspected its own payload.
				Assert.Equal("", report["backend_inventory_at_initialize"]);
				Assert.Equal("false", report["backend_resident"]);
				Assert.Contains("0Harmony", report["backend_identity"], StringComparison.Ordinal);

				var drained = runner.InvokeFixtureAndDrain(3);
				Assert.Equal("results=8,9,10", drained[0]);
				Assert.Equal(3, drained.Length - 1);
				foreach (var line in drained.Skip(1)) Assert.Contains("fixture-hook", line, StringComparison.Ordinal);

				Assert.Equal("error", Report.Parse(runner.Start(Parameters(runner).ToString()))["status"]);
				Assert.Equal("already_started", Report.Parse(runner.Start(Parameters(runner).ToString()))["error_type"]);

				var shutdown = Report.Parse(runner.Shutdown());
				Assert.Equal("ok", shutdown["status"]);
				// Truthful about residency: unpatching removes methods, not assembly generations, and a
				// net48 AppDomain cannot unload what this bootstrap loaded.
				Assert.Equal("true", shutdown["payloads_resident"]);
			}
		}

		[Fact]
		public void A_byte_loaded_bootstrap_driven_only_by_reflection_installs_the_probe() {
			using (var runner = BootstrapRunner.Create("bootstrap-byte-loaded")) {
				var report = Report.Parse(runner.StartByteLoaded(Parameters(runner).WithHook("byte-loaded-hook").ToString()));
				Assert.True(report["status"] == "ok", "Bootstrap refused: " + report.Get("error_type") + " " + report.Get("error_message"));
				Assert.Equal("10", report["payload_load_count"]);
				Assert.Contains("byte-loaded-hook", report["patch_id"], StringComparison.Ordinal);
				Assert.Contains("HookLab.Probe.CorDebug|byte-loaded", runner.ResidentPayloads());
			}
		}

		[Fact]
		public void A_target_guard_mismatch_refuses_before_the_backend_loads() {
			using (var runner = BootstrapRunner.Create("bootstrap-wrong-target")) {
				var report = Report.Parse(runner.Start(Parameters(runner).With("process_id", "1").ToString()));
				Assert.Equal("error", report["status"]);
				Assert.Contains("GuardMismatchException", report["error_type"], StringComparison.Ordinal);
				Assert.Contains("process_id", report["error_message"], StringComparison.Ordinal);
				// The payload is resident because verification happens after load, which is the honest
				// outcome on .NET Framework; the patch backend never loaded at all.
				Assert.Equal("true", report["payloads_resident"]);
				// A rollback that cleaned up completely retains nothing, so the refusal report says there is
				// nothing left to retry. The field is on every refusal report, not only the ones with wreckage.
				Assert.Equal("false", report["cleanup_retry_possible"]);
				Assert.DoesNotContain("0Harmony|byte-loaded", runner.ResidentPayloads());
				Assert.Null(HookLabBootstrapRuntimeOf(runner));
			}
		}

		/// <summary>appdomain_name is consumed by the native bootstrap before this assembly exists, and this
		/// parser must simply tolerate it - both read the same initialize.params file.
		///
		/// It was missing from KnownKeys, so every domain-targeted initialization loaded the resident and
		/// then threw "Unknown initialization key: appdomain_name" inside the target. The MCP client saw a
		/// twenty-second timeout; the cause was visible only as a first-chance exception in the target.
		/// Proven live on w3wp 5208 (IIS, CRMAppPool) on 2026-08-20.</summary>
		[Fact]
		public void A_native_only_key_is_parsed_rather_than_refused() {
			using (var runner = BootstrapRunner.Create("bootstrap-native-only-key")) {
				var report = Report.Parse(runner.Start(Parameters(runner).With("appdomain_name", "/LM/W3SVC/1/ROOT-1-134316874119104946").ToString()));
				Assert.True(report["status"] == "ok", "Bootstrap refused a key it only has to tolerate: " + report.Get("error_type") + " " + report.Get("error_message"));
			}
		}

		[Theory]
		[InlineData("appdomain_id", "999999", "appdomain_id")]
		[InlineData("architecture", "x86", "architecture")]
		[InlineData("image_path", @"C:\nowhere\not-the-target.exe", "image_path")]
		[InlineData("process_creation_utc_ticks", "1", "process_creation_time_utc")]
		[InlineData("runtime_id", "v9.9.99999", "runtime_id")]
		public void Each_target_guard_field_refuses_independently(string key, string value, string guardName) {
			using (var runner = BootstrapRunner.Create("bootstrap-guard-" + key)) {
				var report = Report.Parse(runner.Start(Parameters(runner).With(key, value).ToString()));
				Assert.Equal("error", report["status"]);
				Assert.Contains("GuardMismatchException", report["error_type"], StringComparison.Ordinal);
				Assert.Contains(guardName, report["error_message"], StringComparison.Ordinal);
			}
		}

		/// <summary>Every method-guard field still refuses on its own, and the refusal names the field.
		///
		/// Where it refuses moved, and deliberately: the MVID and the metadata token now drive selection and
		/// lookup, so a wrong one is refused while choosing the module rather than after patching the wrong
		/// candidate's look-alike method, and the declaring type is checked against the token's own method.
		/// ProbeRuntime.Install re-validates all five regardless - the IL digest case proves that path is
		/// still reached and still decides.</summary>
		[Theory]
		[InlineData("hook_il_sha256", "GuardMismatchException", "il_sha256")]
		[InlineData("hook_module_mvid", "System.InvalidOperationException", "hook_module_mvid")]
		[InlineData("hook_metadata_token", "System.InvalidOperationException", "hook_metadata_token")]
		[InlineData("hook_declaring_type", "System.InvalidOperationException", "hook_declaring_type")]
		public void Each_method_guard_field_refuses_independently(string key, string errorType, string mentioned) {
			var wrong = key == "hook_module_mvid" ? Guid.Empty.ToString("D")
				: key == "hook_metadata_token" ? "1"
				: key == "hook_declaring_type" ? "Not.The.Declaring.Type"
				: new string('0', 64);
			using (var runner = BootstrapRunner.Create("bootstrap-method-guard-" + key)) {
				var report = Report.Parse(runner.Start(Parameters(runner).WithHook().With(key, wrong).ToString()));
				Assert.Equal("error", report["status"]);
				Assert.Contains(errorType, report["error_type"], StringComparison.Ordinal);
				Assert.Contains(mentioned, report["error_message"], StringComparison.Ordinal);
			}
		}

		[Fact]
		public void A_hook_specification_must_be_complete() {
			using (var runner = BootstrapRunner.Create("bootstrap-partial-hook")) {
				var report = Report.Parse(runner.Start(Parameters(runner).With("hook_id", "half").ToString()));
				Assert.Equal("error", report["status"]);
				Assert.Contains("ArgumentException", report["error_type"], StringComparison.Ordinal);
			}
		}

		[Fact]
		public void An_unknown_initialization_key_refuses_before_anything_loads() {
			using (var runner = BootstrapRunner.Create("bootstrap-unknown-key")) {
				var report = Report.Parse(runner.Start(Parameters(runner).With("host_id", "x").ToString() + "surprise=1\n"));
				Assert.Equal("error", report["status"]);
				Assert.Contains("ArgumentException", report["error_type"], StringComparison.Ordinal);
				Assert.Equal("false", report["payloads_resident"]);
				Assert.Empty(runner.ResidentPayloads());
			}
		}

		[Fact]
		public void Shutdown_before_start_reports_rather_than_throws() {
			using (var runner = BootstrapRunner.Create("bootstrap-shutdown-first")) {
				Assert.Equal("not_started", Report.Parse(runner.Shutdown())["error_type"]);
			}
		}

		static string? HookLabBootstrapRuntimeOf(BootstrapRunner runner) {
			try { return runner.RuntimeProperty("ProbeInstanceId"); }
			catch (Exception) { return null; }
		}
	}
}
