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

		/// <summary>True while a previous attempt's cleanup failed and its handles are still held. Reported as
		/// <c>cleanup_retry_possible</c> on every report, refusal reports included: the retention can outlive
		/// a *failed* start, where <see cref="IsStarted"/> is false and says nothing about it.</summary>
		public static bool CleanupRetryPossible => ProbeStartup.HasRetainedCleanup;

		/// <summary>Irreversibly makes the stage-1 generation and payload graph resident, validates the target,
		/// loads the backend, and pre-JITs <see cref="ResidentLauncher.Commit"/>. It installs no hook.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Prepare(string parameters) {
			lock (Gate) {
				EmbeddedAssemblyResolver? installed = null;
				try {
					var parsed = BootstrapParameters.Parse(parameters);
					if (parsed.Endpoint != "none" && parsed.Endpoint != "pipe") throw new ArgumentException("Prepare requires endpoint=none or endpoint=pipe.", nameof(parameters));
					installed = resolver ?? EmbeddedAssemblyResolver.FromEmbeddedManifest();
					installed.Install(); resolver = installed;
					var outcome = ProbeStartup.Prepare(parsed);
					ResidentLauncher.Prepare(parsed);
					installed.VerifyNoDiskProvenance(AppDomain.CurrentDomain.GetAssemblies());
					return Describe(outcome, installed) + "generation_identity=" + ResidentLauncher.GenerationIdentity + "\npayloads_resident=true\n";
				}
				catch (Exception ex) { return Error(ex.GetType().FullName ?? "Exception", ex.Message, installed != null && installed.LoadCount != 0); }
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Commit() => ResidentLauncher.Commit();

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string DrainEvents(int max) => ProbeStartup.DrainEvents(max);

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string InstallHook(string parameters) => ProbeStartup.InstallPrepared(BootstrapParameters.Parse(parameters));

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string UninstallHook(string patchId) => ProbeStartup.UninstallPrepared(patchId);

		internal static string RunPrepared(BootstrapParameters parameters) {
			var outcome = ProbeStartup.CommitPrepared(parameters);
			startedResult = Describe(outcome, resolver!);
			return startedResult;
		}

		internal static string WorkerError(Exception ex) => Error(ex.GetType().FullName ?? "Exception", ex.Message, true);

		/// <summary>Installs the manifest-restricted resolver, byte-loads the probe and its contracts from
		/// verified embedded bytes, and calls ProbeInitializer.Initialize. Returns a bounded, line-oriented
		/// key/value report; it never throws, because a func-eval that faults tells the caller far less than
		/// a string that says exactly what refused.
		///
		/// A start is refused with <c>cleanup_pending</c> - distinct from <c>already_started</c>, because the
		/// two need opposite responses - while an earlier attempt's runtime or endpoint is still unresolved.
		/// Initializing over it would replace both shared handles and orphan whatever they pointed at: a
		/// partially unpatched runtime, or an authenticated listener still accepting connections that no
		/// later Shutdown could reach. The retained cleanup is retried first, so the refusal only stands when
		/// the retry got nowhere.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Start(string parameters) {
			lock (Gate) {
				if (startedResult != null) return Error("already_started", "This bootstrap has already run in this AppDomain.", false);
				if (ProbeStartup.HasRetainedCleanup) {
					ProbeStartup.Shutdown();
					if (ProbeStartup.HasRetainedCleanup)
						return Error("cleanup_pending",
							"A previous attempt's cleanup is still outstanding and retrying it did not clear it. Refusing to initialize " +
							"over a runtime that may still be patched or an endpoint that may still be accepting connections.",
							resolver != null && resolver.LoadCount != 0);
				}
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
		/// Whether any probe work is still running is now reported rather than left unanswerable.
		/// <c>command_quiescence</c> is <c>quiesced</c> when no command is inside the handler, <c>in_flight</c>
		/// when one is and may still be mutating the target, and <c>not_required</c> when there was no
		/// endpoint to ask. A completed teardown still only means "not reachable from outside"; it is the
		/// quiescence line that says whether the probe stopped working, and a status of <c>ok</c> now
		/// requires both. An <c>in_flight</c> answer keeps the endpoint handle retained, so the cleanup stays
		/// retryable and no Start may publish over a probe that is still being mutated.
		///
		/// It also runs when the bootstrap never started but a failed start left handles retained. Gating on
		/// started state alone was the defect: a start that published the runtime and the endpoint and then
		/// failed left both retained with no started state to go with them, so this method answered
		/// "not_started" and retried nothing while a listener was still up.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Shutdown() {
			lock (Gate) {
				var wasStarted = startedResult != null;
				var wasPrepared = ProbeStartup.Runtime != null;
				if (!wasStarted && !wasPrepared && !ProbeStartup.HasRetainedCleanup)
					return Error("not_started", "This bootstrap has not run in this AppDomain.", false);
				var cleanup = ProbeStartup.Shutdown();
				// The endpoint state is the persistent one, not just this attempt's: an attempt that only
				// retried the runtime handle must not be allowed to report a clean stop over a listener an
				// earlier attempt failed to tear down.
				// The persistent quiescence state, not only this attempt's: a retry that had nothing left to
				// dispose reports Clean, and must not be allowed to turn an earlier attempt's in-flight
				// command into a clean stop. The retry that does have a handle re-asks and clears it.
				var clean = cleanup.Clean && !ProbeStartup.EndpointMayBeLive && ProbeStartup.CommandQuiescenceState != "in_flight";
				// The resolver is removed only when a bootstrap that actually started has stopped cleanly.
				// Finishing a failed start's cleanup does not make the payload it already loaded go away, and
				// removing the handler that satisfies that payload's next bind is what Rollback exists to
				// avoid - so the state is reported rather than forced.
				var resolverInstalled = resolver != null;
				if (clean && (wasStarted || wasPrepared)) { resolver?.Uninstall(); startedResult = null; resolverInstalled = false; }
				var lines = new List<string> {
					"status=" + (clean ? "ok" : "partial"),
					"payloads_resident=true",
					"resolver_installed=" + (resolverInstalled ? "true" : "false"),
					"endpoint_teardown=" + ProbeStartup.EndpointTeardownState,
					// Beside the teardown line, never instead of it: "unreachable" and "no longer working"
					// are different facts and a rollback needs both to answer cleanup_outcome.
					"command_quiescence=" + ProbeStartup.CommandQuiescenceState,
					"endpoint_live=" + (ProbeStartup.EndpointMayBeLive ? "true" : "false"),
					// Read from the retention state rather than from this attempt's report, so that this line
					// and the identical one on every refusal report can never disagree.
					"cleanup_retry_possible=" + (ProbeStartup.HasRetainedCleanup ? "true" : "false"),
					"residency_commit=completed",
					"behavior_commit=" + (clean ? "stopped" : "cleanup_incomplete"),
					"prototype_compromises=identity_partly_self_asserted,no_residency_rollback",
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
				"target_process_id=" + outcome.TargetProcessId.ToString(CultureInfo.InvariantCulture),
				"target_image_path=" + Sanitize(outcome.TargetImagePath),
				"payload_identities=" + string.Join(",", installed.ManifestIdentities),
				"payload_load_count=" + installed.LoadCount.ToString(CultureInfo.InvariantCulture),
				"residency_commit=completed",
				"behavior_commit=" + (outcome.PatchId == null ? "not_started" : "completed"),
				"prototype_compromises=" + (outcome.PipeName.Length == 0 ? "endpoint_none," : "") + "identity_partly_self_asserted,no_residency_rollback",
			};
			if (outcome.PipeName.Length != 0) { lines.Add("pipe_name=" + outcome.PipeName); lines.Add("pipe_nonce_base64=" + outcome.EndpointNonceBase64); }
			if (outcome.PatchId != null) lines.Add("patch_id=" + Sanitize(outcome.PatchId));
			return Join(lines);
		}

		/// <summary>The refusal report. It carries the failure that caused the refusal *and* what the
		/// rollback managed to clean up - neither erases the other, and a rollback that left a listener up
		/// has to be visible here rather than only in a property nobody reads.
		///
		/// cleanup_retry_possible belongs here and not only in the shutdown report: a failed start is exactly
		/// where retained handles have no started state to advertise them, so a caller reading only this
		/// report had no way to learn that a Shutdown still had work to do.</summary>
		static string Error(string type, string message, bool payloadsResident) {
			var lines = new List<string> {
				"status=error",
				"error_type=" + Sanitize(type),
				"error_message=" + Sanitize(message),
				"payloads_resident=" + (payloadsResident ? "true" : "false"),
				"resolver_installed=" + (payloadsResident ? "true" : "false"),
				"endpoint_teardown=" + ProbeStartup.EndpointTeardownState,
				// A refused start whose rollback left a command running is the same hazard as a shutdown
				// that did, and a caller who only ever sees this report has to be able to learn it.
				"command_quiescence=" + ProbeStartup.CommandQuiescenceState,
				"endpoint_live=" + (ProbeStartup.EndpointMayBeLive ? "true" : "false"),
				"cleanup_retry_possible=" + (ProbeStartup.HasRetainedCleanup ? "true" : "false"),
				"residency_commit=" + (payloadsResident ? "completed" : "not_started"),
				"behavior_commit=not_started",
				"prototype_compromises=identity_partly_self_asserted,no_residency_rollback",
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
