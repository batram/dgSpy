using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace HookLab.Bootstrap {
	/// <summary>The fixed entry point a host func-evals after byte-loading this assembly.
	///
	/// Every member of this type - fields included - is typed against the BCL and this assembly only.
	/// That is load-bearing, not style: invoking <see cref="Start"/> reflectively loads this type first,
	/// and the CLR would have to resolve the field types before the resolver is installed. A field typed
	/// <c>ProbeRuntime</c> here would fail the load it is supposed to make possible.</summary>
	public static class HookLabBootstrap {
		static readonly object Gate = new object();
		static EmbeddedAssemblyResolver? resolver;
		static string? startedResult;

		/// <summary>The ProbeRuntime this bootstrap created, or null. Typed as object so that reading the
		/// property never forces a probe type to load.</summary>
		public static object? Runtime => ProbeStartup.Runtime;

		public static bool IsStarted { get { lock (Gate) return startedResult != null; } }

		/// <summary>Non-null when the last endpoint teardown failed, which means a listener may still be
		/// accepting connections. Also reported in the Start and Shutdown reports.</summary>
		public static string? EndpointTeardownError => ProbeStartup.EndpointTeardownError;

		/// <summary>Non-null when the last runtime disposal failed, which means unpatching may be
		/// incomplete. Carried beside the endpoint outcome, never instead of it.</summary>
		public static string? RuntimeCleanupError => ProbeStartup.RuntimeCleanupError;

		/// <summary>True while a known endpoint may still be live because its teardown failed. No report
		/// may claim a clean stop while this is set.</summary>
		public static bool EndpointMayBeLive => ProbeStartup.EndpointMayBeLive;

		/// <summary>Installs the manifest-restricted resolver, byte-loads the probe and its contracts from
		/// verified embedded bytes, and calls ProbeInitializer.Initialize. Returns a bounded, line-oriented
		/// key/value report; it never throws, because a func-eval that faults tells the caller far less than
		/// a string that says exactly what refused.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Start(string parameters) {
			lock (Gate) {
				if (startedResult != null) return Error("already_started", "This bootstrap has already run in this AppDomain.", false);
				EmbeddedAssemblyResolver? installed = null;
				try {
					var parsed = BootstrapParameters.Parse(parameters);
					// One resolver per AppDomain, reused across retries. A refused attempt that already loaded a
					// payload leaves it resident, and a second handler over the same identities would either
					// answer with the first one's cache or hand out a duplicate - both worse than reusing it.
					installed = resolver ?? EmbeddedAssemblyResolver.FromEmbeddedManifest();
					installed.Install();
					resolver = installed;
					// Everything past this line lives in ProbeStartup, whose jitting is what first resolves a
					// probe type - which is why it must happen after Install and never be inlined into here.
					var outcome = ProbeStartup.Run(parsed);
					installed.VerifyNoDiskProvenance(AppDomain.CurrentDomain.GetAssemblies());
					startedResult = Describe(outcome, installed);
					return startedResult;
				}
				catch (Exception ex) {
					var retained = Rollback(installed);
					return Error(ex.GetType().FullName ?? "Exception", ex.Message, retained);
				}
			}
		}

		/// <summary>Releases the pipe endpoint and removes every hook this bootstrap installed. It cannot
		/// unload what it loaded: a .NET Framework AppDomain has no mechanism for that, so the payload stays
		/// resident and the report says so rather than implying a clean slate.
		///
		/// An incomplete cleanup keeps this bootstrap started. Clearing the started state would make the
		/// next Shutdown answer "not_started" while a listener was still up - a clean-looking report over an
		/// unreachable live endpoint. Started state plus retained handles is what makes the retry possible,
		/// and the report says which part is still outstanding.
		///
		/// What this report does *not* claim: that no probe work is still running. ProbePipeServer.Dispose
		/// closes the endpoint without cancelling a command already inside the handler, so a successful
		/// teardown means "not reachable from outside", not "quiesced". Distinguishing those needs a
		/// cancellation contract on the command handler, which is scheduled work rather than something to
		/// paper over here - and until it exists, no line in this report should be read as quiescence.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Shutdown() {
			lock (Gate) {
				if (startedResult == null) return Error("not_started", "This bootstrap has not run in this AppDomain.", false);
				var cleanup = ProbeStartup.Shutdown();
				// The endpoint state is the persistent one, not just this attempt's: an attempt that only
				// retried the runtime handle must not be allowed to report a clean stop over a listener an
				// earlier attempt failed to tear down.
				var clean = cleanup.Clean && !ProbeStartup.EndpointMayBeLive;
				if (clean) { resolver?.Uninstall(); startedResult = null; }
				var lines = new List<string> {
					"status=" + (clean ? "ok" : "partial"),
					"payloads_resident=true",
					"resolver_installed=" + (clean ? "false" : "true"),
					"endpoint_teardown=" + ProbeStartup.EndpointTeardownState,
					"endpoint_live=" + (ProbeStartup.EndpointMayBeLive ? "true" : "false"),
					"cleanup_retry_possible=" + (cleanup.RetryPossible ? "true" : "false"),
				};
				if (cleanup.RuntimeError != null) lines.Add("runtime_cleanup_error=" + Sanitize(cleanup.RuntimeError));
				if (ProbeStartup.EndpointTeardownError != null) lines.Add("endpoint_teardown_error=" + Sanitize(ProbeStartup.EndpointTeardownError));
				if (ProbeStartup.EndpointListenerFailure != null) lines.Add("endpoint_listener_failure=" + Sanitize(ProbeStartup.EndpointListenerFailure));
				if (!clean)
					lines.Add("error_message=" + Sanitize("Cleanup was incomplete. " +
						(cleanup.RuntimeError != null ? "Unpatching failed: " + cleanup.RuntimeError + " " : "") +
						(cleanup.EndpointError != null ? "Endpoint teardown failed: " + cleanup.EndpointError : "")));
				return Join(lines);
			}
		}

		/// <summary>On failure the resolver stays installed if it already handed out an assembly. Removing it
		/// then would strand a resident payload with no way to satisfy its next bind, which is a worse state
		/// than an idle handler.</summary>
		static bool Rollback(EmbeddedAssemblyResolver? installed) {
			// ProbeStartup.Run already cleaned up what it created and recorded the outcome; this call only
			// retries whatever that attempt had to retain, and reports nothing new when there is nothing
			// left. It cannot throw, so it cannot displace the startup failure this rollback runs under.
			ProbeStartup.Shutdown();
			if (installed == null) return false;
			if (installed.LoadCount != 0) return true;
			installed.Uninstall();
			resolver = null;
			return false;
		}

		static string Describe(BootstrapOutcome outcome, EmbeddedAssemblyResolver installed) {
			var lines = new List<string> {
				"status=ok",
				"probe_instance_id=" + outcome.ProbeInstanceId,
				"protocol_version=" + outcome.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
				"backend_identity=" + Sanitize(outcome.BackendIdentity),
				"backend_resident=" + (outcome.BackendResident ? "true" : "false"),
				"backend_inventory_at_initialize=" + Sanitize(outcome.InventoryIdentities),
				"hooks_version=" + outcome.HooksVersion.ToString(CultureInfo.InvariantCulture),
				"pipe_name=" + outcome.PipeName,
				"secret_base64=" + outcome.SecretBase64,
				"endpoint_nonce_base64=" + outcome.EndpointNonceBase64,
				"target_process_id=" + outcome.TargetProcessId.ToString(CultureInfo.InvariantCulture),
				"target_image_path=" + Sanitize(outcome.TargetImagePath),
				"payload_identities=" + string.Join(",", installed.ManifestIdentities),
				"payload_load_count=" + installed.LoadCount.ToString(CultureInfo.InvariantCulture),
			};
			if (outcome.PatchId != null) lines.Add("patch_id=" + Sanitize(outcome.PatchId));
			return Join(lines);
		}

		/// <summary>The refusal report. It carries the failure that caused the refusal *and* what the
		/// rollback managed to clean up - neither erases the other, and a rollback that left a listener up
		/// has to be visible here rather than only in a property nobody reads.</summary>
		static string Error(string type, string message, bool payloadsResident) {
			var lines = new List<string> {
				"status=error",
				"error_type=" + Sanitize(type),
				"error_message=" + Sanitize(message),
				"payloads_resident=" + (payloadsResident ? "true" : "false"),
				"resolver_installed=" + (payloadsResident ? "true" : "false"),
				"endpoint_teardown=" + ProbeStartup.EndpointTeardownState,
				"endpoint_live=" + (ProbeStartup.EndpointMayBeLive ? "true" : "false"),
			};
			if (ProbeStartup.RuntimeCleanupError != null) lines.Add("runtime_cleanup_error=" + Sanitize(ProbeStartup.RuntimeCleanupError));
			if (ProbeStartup.EndpointTeardownError != null) lines.Add("endpoint_teardown_error=" + Sanitize(ProbeStartup.EndpointTeardownError));
			if (ProbeStartup.EndpointListenerFailure != null) lines.Add("endpoint_listener_failure=" + Sanitize(ProbeStartup.EndpointListenerFailure));
			return Join(lines);
		}

		static string Join(List<string> lines) {
			var builder = new StringBuilder();
			foreach (var line in lines) { builder.Append(line); builder.Append('\n'); }
			return builder.ToString();
		}

		static string Sanitize(string value) {
			if (string.IsNullOrEmpty(value)) return "";
			var trimmed = value.Length > BootstrapParameters.MaximumValueLength ? value.Substring(0, BootstrapParameters.MaximumValueLength) : value;
			return trimmed.Replace('\r', ' ').Replace('\n', ' ');
		}
	}

	/// <summary>What ProbeStartup reports back. Every field is a primitive or a string, so this type can be
	/// named from HookLabBootstrap without dragging a probe type into that frame.</summary>
	sealed class BootstrapOutcome {
		internal string ProbeInstanceId = "";
		internal int ProtocolVersion;
		internal string BackendIdentity = "";
		internal bool BackendResident;
		internal string InventoryIdentities = "";
		internal long HooksVersion;
		internal string PipeName = "";
		internal string SecretBase64 = "";
		internal string EndpointNonceBase64 = "";
		internal int TargetProcessId;
		internal string TargetImagePath = "";
		internal string? PatchId;
	}
}
