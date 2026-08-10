using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

		/// <summary>A refused bootstrap handed its endpoint teardown to the thread pool rather than
		/// blocking the caller. The report says so instead of implying the endpoint is already gone.</summary>
		internal static bool EndpointTeardownDeferred { get; private set; }

		/// <summary>Why the deferred teardown failed, if it did. A swallowed failure here would leave a
		/// listener that never tore down looking exactly like one that did - the same reason consumer
		/// dispatch failures are counted rather than discarded in ProbeRuntime.</summary>
		internal static string? EndpointTeardownError { get; private set; }

		/// <summary>Deliberately free of probe types: it disposes through IDisposable so that it can also run
		/// on the failure path, where the payload may never have loaded at all.</summary>
		internal static void Shutdown() {
			object? currentRuntime;
			IDisposable? currentServer;
			lock (Gate) { currentRuntime = runtime; currentServer = server; runtime = null; server = null; }
			(currentRuntime as IDisposable)?.Dispose();
			currentServer?.Dispose();
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
				lock (Gate) { runtime = null; server = null; }
				probe?.Dispose();
				// Do NOT tear the endpoint down on this thread. ProbePipeServer.Dispose waits up to two
				// seconds for its listener, and this runs inside a debugger func-eval whose evaluation
				// timeout is shorter than that: a refused bootstrap came back to the host as
				// "Evaluation timed out" with no report at all, which is strictly worse than a listener
				// that lingers for a moment. Measured against a live CorDebug target, not reasoned about.
				// Nothing is exposed by the delay - TakeInitialEndpoint was never reached, so the
				// credential never left this process.
				if (pipe != null) {
					EndpointTeardownDeferred = true;
					ThreadPool.QueueUserWorkItem(_ => {
						try { pipe.Dispose(); }
						catch (Exception disposeFailure) { EndpointTeardownError = disposeFailure.GetType().FullName + ": " + disposeFailure.Message; }
					});
				}
				throw;
			}
		}

		static PatchOperationResult InstallHook(ProbeRuntime probe, BootstrapParameters parameters) {
			var assemblyName = parameters.Hook("hook_assembly");
			var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(SafeName(a), assemblyName, StringComparison.Ordinal))
				?? throw new InvalidOperationException("Hook target assembly is not loaded in the target: " + assemblyName);
			var type = assembly.GetType(parameters.Hook("hook_type"), false)
				?? throw new InvalidOperationException("Hook target type was not found: " + parameters.Hook("hook_type"));
			var name = parameters.Hook("hook_method");
			var signature = parameters.Hook("hook_method_signature");
			var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
				.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal) && string.Equals(MethodGuards.Signature(m), signature, StringComparison.Ordinal))
				?? throw new InvalidOperationException("Hook target method was not found: " + signature);
			var guard = new MethodGuard(
				Guid.Parse(parameters.Hook("hook_module_mvid")),
				uint.Parse(parameters.Hook("hook_metadata_token"), CultureInfo.InvariantCulture),
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
