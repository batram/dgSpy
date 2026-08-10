using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HookLab.Contracts;
using HookLab.Probe.CorDebug;
using HookLab.Probe.CorDebug.Patching;
using HookLab.Probe.CorDebug.Transport;

namespace HookLab.Bootstrap {
	/// <summary>Everything that names a probe type. Jitting <see cref="Run"/> is the first moment the CLR
	/// asks for HookLab.Probe.CorDebug, so it must only ever be reached after the resolver is installed.
	///
	/// The static fields are typed <c>object</c>/<c>IDisposable</c> on purpose. Loading this type happens as
	/// soon as HookLabBootstrap.Start is jitted, which is before the resolver exists.</summary>
	static class ProbeStartup {
		static readonly object Gate = new object();
		static object? runtime;
		static IDisposable? server;

		internal static object? Runtime { get { lock (Gate) return runtime; } }

		/// <summary>What one cleanup attempt did. Every field is a primitive or a string, so HookLabBootstrap
		/// can name this type without dragging a probe type into that frame.
		///
		/// The two failures are carried side by side on purpose: unpatching and endpoint teardown fail for
		/// unrelated reasons, and letting either win erases the other.</summary>
		internal sealed class CleanupReport {
			/// <summary>Why disposing the runtime failed - which means unpatching may be incomplete.</summary>
			internal string? RuntimeError;
			/// <summary>Why tearing the endpoint down failed.</summary>
			internal string? EndpointError;
			/// <summary>True when a teardown was attempted and failed, so a listener may still accept
			/// connections. No caller may report a clean stop while this is set.</summary>
			internal bool EndpointMayBeLive;
			/// <summary>True when there was nothing to dispose, so this attempt says nothing new. A caller
			/// must not use it to overwrite what an earlier attempt recorded.</summary>
			internal bool NothingToDo;
			/// <summary>The handles that failed are retained, so a later Shutdown retries exactly them.</summary>
			internal bool RetryPossible;
			/// <summary>True when there was an endpoint to tear down at all.</summary>
			internal bool EndpointAttempted;
			/// <summary>True when there was a runtime to dispose at all.</summary>
			internal bool RuntimeAttempted;
			/// <summary>The endpoint's own account of why its listener thread ended, if it ended on an
			/// exception rather than on shutdown. Not a cleanup failure - it means the endpoint had already
			/// stopped accepting connections before anyone asked it to - but it is the difference between a
			/// stop that happened and a stop that was performed, so it is reported rather than inferred.</summary>
			internal string? ListenerFailure;
			internal bool Clean => RuntimeError == null && EndpointError == null && !EndpointMayBeLive;
		}

		/// <summary>Endpoint teardown state of the last attempt: not_required, completed or failed.</summary>
		internal static string EndpointTeardownState { get; private set; } = "not_required";

		/// <summary>Why the endpoint teardown failed, if it did. A swallowed failure here would leave a
		/// listener that never tore down looking exactly like one that did - the same reason consumer
		/// dispatch failures are counted rather than discarded in ProbeRuntime.</summary>
		internal static string? EndpointTeardownError { get; private set; }

		/// <summary>Why disposing the runtime failed, if it did. Reported beside the endpoint outcome rather
		/// than instead of it.</summary>
		internal static string? RuntimeCleanupError { get; private set; }

		/// <summary>Set while a known endpoint may still be accepting connections.</summary>
		internal static bool EndpointMayBeLive { get; private set; }

		/// <summary>The endpoint's listener thread died on an exception rather than on shutdown, if it did.
		/// Reported so that "the endpoint stopped" and "the endpoint was stopped" are distinguishable.</summary>
		internal static string? EndpointListenerFailure { get; private set; }

		/// <summary>Test seam: makes the runtime disposal on the startup rollback path fail, so that
		/// "a cleanup failure must not erase the startup failure" can be exercised without a probe that
		/// really breaks. Null in production; the only writer is the bootstrap test project, through
		/// InternalsVisibleTo.</summary>
		internal static Action? StartupRollbackFaultForTest;

		/// <summary>Test seam: swaps the two shared handles and hands the previous pair back, so the cleanup
		/// contract can be exercised with disposables that fail on demand and record their disposals.</summary>
		internal static void ExchangeHandlesForTest(object? newRuntime, IDisposable? newServer, out object? oldRuntime, out IDisposable? oldServer) {
			lock (Gate) {
				oldRuntime = runtime; oldServer = server;
				runtime = newRuntime; server = newServer;
			}
		}

		/// <summary>Deliberately free of probe types: it disposes through IDisposable so that it can also run
		/// on the failure path, where the payload may never have loaded at all.
		///
		/// It never throws. A caller that has to decide whether an endpoint is still live cannot be handed
		/// that decision as an exception, and the previous version's throw skipped the endpoint teardown
		/// entirely - behind two already-nulled handles, so no later Shutdown could reach the listener.</summary>
		internal static CleanupReport Shutdown() {
			object? currentRuntime;
			IDisposable? currentServer;
			lock (Gate) { currentRuntime = runtime; currentServer = server; runtime = null; server = null; }
			// Nothing to dispose says nothing about what an earlier attempt left behind, so the reporting
			// fields are left exactly as that attempt set them.
			if (currentRuntime == null && currentServer == null) return new CleanupReport { NothingToDo = true };
			var report = Cleanup(currentRuntime, currentServer);
			Record(report);
			return report;
		}

		/// <summary>Disposes both handles in independent guarded blocks and retains whichever failed.
		///
		/// Separate blocks are the whole point: the endpoint teardown must be attempted even when unpatching
		/// throws. Retention is the other half - a handle nulled after a failed dispose is unreachable
		/// forever, so a truthful "still live, here is what failed" is kept retryable instead.</summary>
		static CleanupReport Cleanup(object? runtimeHandle, IDisposable? serverHandle) {
			var report = new CleanupReport();
			report.RuntimeAttempted = runtimeHandle != null;
			try { (runtimeHandle as IDisposable)?.Dispose(); }
			catch (Exception ex) {
				report.RuntimeError = Describe(ex);
				report.RetryPossible = true;
				lock (Gate) runtime = runtime ?? runtimeHandle;
			}
			if (serverHandle != null) {
				report.EndpointAttempted = true;
				try { serverHandle.Dispose(); }
				catch (Exception ex) {
					report.EndpointError = Describe(ex);
					report.EndpointMayBeLive = true;
					report.RetryPossible = true;
					lock (Gate) server = server ?? serverHandle;
				}
				report.ListenerFailure = ListenerFailureOf(serverHandle);
			}
			return report;
		}

		/// <summary>Each component reports the last attempt made on *that component*. An attempt that had no
		/// endpoint to tear down says nothing about the endpoint, so it must not overwrite what the attempt
		/// that did have one recorded - that is how a retry of a retained runtime handle would otherwise
		/// downgrade a real "failed" teardown to "not_required".</summary>
		static void Record(CleanupReport report) {
			if (report.RuntimeAttempted) RuntimeCleanupError = report.RuntimeError;
			if (report.EndpointAttempted) {
				EndpointListenerFailure = report.ListenerFailure;
				EndpointTeardownError = report.EndpointError;
				EndpointMayBeLive = report.EndpointMayBeLive;
				EndpointTeardownState = report.EndpointError != null ? "failed" : "completed";
			}
		}

		static string Describe(Exception ex) => ex.GetType().FullName + ": " + ex.Message;

		/// <summary>Reads ProbePipeServer.ListenerFailure without naming the type. Cleanup runs on paths where
		/// the payload may never have loaded - a startup refused before the resolver handed anything out
		/// still ends here - and a probe type named in this method would be resolved when it is jitted.
		/// Reflection keeps that hazard out of a method whose whole job is to run when things went wrong;
		/// a handle without the property (a test double) simply reports nothing.</summary>
		static string? ListenerFailureOf(IDisposable serverHandle) {
			try {
				var property = serverHandle.GetType().GetProperty("ListenerFailure", BindingFlags.Public | BindingFlags.Instance);
				return property == null ? null : property.GetValue(serverHandle) as string;
			}
			catch (Exception) { return null; }
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		internal static BootstrapOutcome Run(BootstrapParameters parameters) {
			var expected = new TargetIdentity(parameters.HostId, parameters.ImagePath, parameters.ProcessId,
				new DateTime(parameters.ProcessCreationUtcTicks, DateTimeKind.Utc), parameters.Architecture,
				parameters.RuntimeId, parameters.AppDomainId);
			// The provider measures the live process. Echoing the caller's expectation back would make
			// MethodGuards.ValidateTarget compare a value with itself, which is a guard that can never fail.
			var provider = new LiveTargetIdentityProvider(parameters.HostId);
			ProbePipeServer? pipe = null;
			ProbeRuntime? probe = null;
			try {
				pipe = new ProbePipeServer(HandleCommand);
				var initialization = new ProbeInitialization(expected, provider, pipe, parameters.EventCapacity, parameters.ByteCapacity);
				// T04 owns the ordering inside here: the target guard is validated before any Harmony type
				// resolves, then the backend inventory runs. Nothing above may touch HarmonyLib.
				probe = ProbeInitializer.Initialize(initialization);
				lock (Gate) { runtime = probe; server = pipe; }
				var endpoint = pipe.TakeInitialEndpoint();
				var actual = provider.GetCurrentIdentity();
				var outcome = new BootstrapOutcome {
					ProbeInstanceId = probe.ProbeInstanceId,
					ProtocolVersion = ProbeWireProtocol.ProtocolVersion,
					BackendIdentity = probe.Inventory.SelectedIdentity,
					BackendResident = probe.Inventory.UseResident,
					InventoryIdentities = string.Join(";", probe.Inventory.LoadedIdentities.ToArray()),
					PipeName = endpoint.PipeName,
					SecretBase64 = Convert.ToBase64String(endpoint.Secret),
					EndpointNonceBase64 = Convert.ToBase64String(endpoint.EndpointNonce),
					TargetProcessId = actual.ProcessId,
					TargetImagePath = actual.ImagePath,
				};
				if (parameters.HasHook) {
					var result = InstallHook(probe, parameters);
					outcome.PatchId = result.PatchId;
					outcome.HooksVersion = result.HooksVersion;
				}
				else
					outcome.HooksVersion = probe.HooksVersion;
				return outcome;
			}
			catch (Exception) {
				// Same cleanup as Shutdown, for the same reasons, and inline. The teardown used to be handed
				// to the thread pool because ProbePipeServer.Dispose waited up to two seconds for its
				// listener, which outran the func-eval timeout; that Dispose is bounded and non-blocking
				// now (it closes the endpoint and returns without waiting for the listener thread), so the
				// deferral has nothing left to buy and cost the caller a report it could act on.
				lock (Gate) { runtime = null; server = null; }
				var faulted = StartupRollbackFaultForTest;
				Record(Cleanup(faulted == null ? (object?)probe : new FaultingHandle(faulted), pipe));
				// The startup failure is what the caller asked about, so it is what leaves this frame. The
				// cleanup outcome rides in the reporting fields instead of replacing it.
				throw;
			}
		}

		/// <summary>Selects the hook target by the strongest identity the caller supplied - module MVID -
		/// and resolves the method from the metadata token of that same module.
		///
		/// Selecting by simple name and letting the MVID guard refuse afterwards made enumeration order
		/// decide: with two same-named assemblies resident (side-by-side versions, distinct load contexts,
		/// or a byte-loaded copy beside a disk one), the wrong one is examined, resolution fails or finds a
		/// look-alike method, the guard correctly refuses - and the right assembly is never considered. A
		/// fully valid request then fails nondeterministically. The guard still decides; it just no longer
		/// has to stand in for a lookup.</summary>
		static PatchOperationResult InstallHook(ProbeRuntime probe, BootstrapParameters parameters) {
			var assemblyName = parameters.Hook("hook_assembly");
			var mvidText = parameters.Hook("hook_module_mvid");
			Guid mvid;
			try { mvid = Guid.Parse(mvidText); }
			catch (FormatException ex) { throw new InvalidOperationException("hook_module_mvid is not a GUID: " + mvidText, ex); }
			// An empty MVID is never a real module identity, and it is what a failed read of an assembly's
			// MVID degrades to below - so accepting it would turn selection into "whichever one we could
			// not identify".
			if (mvid == Guid.Empty) throw new InvalidOperationException("hook_module_mvid must name a module; the empty GUID does not.");
			var tokenText = parameters.Hook("hook_metadata_token");
			var token = uint.Parse(tokenText, CultureInfo.InvariantCulture);

			var named = AppDomain.CurrentDomain.GetAssemblies()
				.Where(a => string.Equals(SafeName(a), assemblyName, StringComparison.Ordinal)).ToArray();
			var candidates = named.Where(a => SafeMvid(a) == mvid).ToArray();
			if (candidates.Length == 0)
				throw new InvalidOperationException("No loaded assembly matches hook_assembly '" + assemblyName + "' and hook_module_mvid " +
					mvid.ToString("D") + ". Same-named assemblies loaded: " +
					(named.Length == 0 ? "(none)" : string.Join(",", named.Select(a => SafeMvid(a).ToString("D")).ToArray())) + ".");
			if (candidates.Length > 1)
				throw new InvalidOperationException("Ambiguous hook target: " + candidates.Length.ToString(CultureInfo.InvariantCulture) +
					" loaded assemblies share hook_assembly '" + assemblyName + "' and hook_module_mvid " + mvid.ToString("D") +
					". Refusing rather than patching whichever one enumerated first.");
			var assembly = candidates[0];
			var module = assembly.ManifestModule;
			var name = parameters.Hook("hook_method");
			var signature = parameters.Hook("hook_method_signature");
			MethodBase method;
			try { method = module.ResolveMethod(unchecked((int)token)) ?? throw new InvalidOperationException("resolved to null"); }
			catch (Exception ex) {
				throw new InvalidOperationException("hook_metadata_token " + tokenText + " does not resolve to a method in the selected module (" +
					module.Name + ", mvid " + mvid.ToString("D") + "): " + ex.Message, ex);
			}
			// The token came out of the same metadata as the MVID, so the lookup is now aligned with the
			// identity it is guarded by. The remaining caller-supplied facts are checked here for a refusal
			// that names the field, and again - authoritatively - by ProbeRuntime.Install's guard.
			var declaringType = method.DeclaringType?.FullName ?? "";
			Verify("hook_type", parameters.Hook("hook_type"), declaringType);
			Verify("hook_declaring_type", parameters.Hook("hook_declaring_type"), declaringType);
			Verify("hook_method", name, method.Name);
			Verify("hook_method_signature", signature, MethodGuards.Signature(method));
			var guard = new MethodGuard(
				mvid,
				token,
				parameters.Hook("hook_declaring_type"),
				signature,
				parameters.Hook("hook_il_sha256"));
			var kind = (HookKind)Enum.Parse(typeof(HookKind), parameters.Hook("hook_kind"), false);
			var limits = new HookLimits(100, 64 * 1024, 8, 64, 1024, 5);
			var document = new HookDocument(1, parameters.Hook("hook_id"), kind, guard, "{}", limits, true);
			// ProbeRuntime.Install re-validates the target identity and every method guard field. The values
			// come from the caller, so a wrong module, token, signature or IL digest refuses here.
			return probe.Install(method, document, probe.HooksVersion);
		}

		static string SafeName(Assembly assembly) {
			try { return assembly.GetName().Name ?? ""; }
			catch (Exception) { return ""; }
		}

		/// <summary>Guid.Empty for anything whose module identity cannot be read - a dynamic assembly has no
		/// MVID at all. Callers reject Guid.Empty as a request, so an unreadable assembly can never match.</summary>
		static Guid SafeMvid(Assembly assembly) {
			try { return assembly.IsDynamic ? Guid.Empty : assembly.ManifestModule.ModuleVersionId; }
			catch (Exception) { return Guid.Empty; }
		}

		static void Verify(string field, string expected, string actual) {
			if (!string.Equals(expected, actual, StringComparison.Ordinal))
				throw new InvalidOperationException("The method at hook_metadata_token disagrees with " + field + ": expected '" +
					expected + "', found '" + actual + "'.");
		}

		/// <summary>Test-seam handle: its Dispose does what a failing unpatch does - throws, and leaves the
		/// real runtime undisposed.</summary>
		sealed class FaultingHandle : IDisposable {
			readonly Action fault;
			internal FaultingHandle(Action fault) { this.fault = fault; }
			public void Dispose() => fault();
		}

		static ProbeCommandResult HandleCommand(string operation, string payloadJson, long? expectedHooksVersion) {
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			if (!string.Equals(operation, "status", StringComparison.Ordinal))
				throw new NotSupportedException("The bootstrap probe endpoint serves 'status' only; hook operations belong to the extension surface.");
			var state = probe.GetState();
			return new ProbeCommandResult(StatusJson(state), state.HooksVersion);
		}

		static string StatusJson(ProbeState state) =>
			"{\"protocol_version\":" + state.ProtocolVersion.ToString(CultureInfo.InvariantCulture) +
			",\"probe_instance_id\":\"" + Escape(state.ProbeInstanceId) +
			"\",\"backend_identity\":\"" + Escape(state.BackendIdentity) +
			"\",\"hooks_version\":" + state.HooksVersion.ToString(CultureInfo.InvariantCulture) +
			",\"process_id\":" + state.Target.ProcessId.ToString(CultureInfo.InvariantCulture) +
			",\"image_path\":\"" + Escape(state.Target.ImagePath) +
			"\",\"appdomain_id\":\"" + Escape(state.Target.AppDomainId) +
			"\",\"patch_ids\":[" + string.Join(",", state.PatchIds.Select(id => "\"" + Escape(id) + "\"").ToArray()) + "]}";

		static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

		sealed class LiveTargetIdentityProvider : ITargetIdentityProvider {
			readonly string hostId;
			internal LiveTargetIdentityProvider(string hostId) { this.hostId = hostId; }
			public TargetIdentity GetCurrentIdentity() {
				using (var process = Process.GetCurrentProcess()) {
					var module = process.MainModule ?? throw new InvalidOperationException("The target process has no main module.");
					return new TargetIdentity(hostId, module.FileName, process.Id, process.StartTime.ToUniversalTime(),
						IntPtr.Size == 8 ? "x64" : "x86", RuntimeEnvironment.GetSystemVersion(),
						AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture));
				}
			}
		}
	}
}
