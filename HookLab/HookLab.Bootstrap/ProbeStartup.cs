using System;
using System.Collections.Generic;
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
		static string preparedPipeName = "";
		static string preparedEndpointNonceBase64 = "";
		// Set when the handle in the corresponding field is there because its own Dispose failed, rather
		// than because a start succeeded. The two states occupy the same fields and mean opposite things:
		// one is a running bootstrap, the other is wreckage a retry still has to clear.
		static bool runtimeRetained;
		static bool serverRetained;

		internal static object? Runtime { get { lock (Gate) return runtime; } }

		/// <summary>True while a handle whose disposal failed is still held, so a retry has something to work
		/// on - and so nothing may publish over it.
		///
		/// This has to be visible outside this type because retention can outlive a *failed* start, where
		/// there is no started state to infer it from. Without it, HookLabBootstrap.Shutdown answered
		/// "not_started" over a live listener and the next Start overwrote both handles, which orphaned a
		/// partially patched runtime and an authenticated endpoint for the life of the AppDomain.</summary>
		internal static bool HasRetainedCleanup { get { lock (Gate) return runtimeRetained || serverRetained; } }

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
			/// <summary>not_required, quiesced or in_flight. See <see cref="CommandQuiescenceState"/>.</summary>
			internal string CommandQuiescence = "not_required";
			/// <summary>not_required (this runtime tolerates an unwinding listener), stopped, or running.
			/// See <see cref="AwaitListenerOf"/>: on Mono, running means the target may not survive its own
			/// exit, which is not a clean stop however complete the rest of the teardown was.</summary>
			internal string ListenerTeardown = "not_required";
			/// <summary>A command still inside the handler is not a clean stop: the endpoint is unreachable
			/// but the probe may still be mutating the target, which is exactly the state a rollback used to
			/// report as completed.</summary>
			internal bool Clean => RuntimeError == null && EndpointError == null && !EndpointMayBeLive
				&& CommandQuiescence != "in_flight" && ListenerTeardown != "running";
		}

		/// <summary>How long one cleanup attempt waits for an in-flight command to leave the handler. Short
		/// on purpose: this can run on the target's own thread inside a func-eval whose whole budget is a
		/// second, and a caller that needs longer retries the Shutdown rather than being made to wait here.</summary>
		internal const int QuiescenceTimeoutMilliseconds = 250;

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

		/// <summary>Whether the last endpoint teardown left a command inside the handler: not_required (no
		/// endpoint, or a handle with no quiescence contract), quiesced (no command is running, so cleanup
		/// may be reported as completed) or in_flight (one is still running and may still be mutating the
		/// target - ambiguous cleanup, and a reconciliation).
		///
		/// This is the fact a rollback could not previously obtain. Dispose closes the transport and returns
		/// while a side-effecting handler keeps running, so a completed teardown meant "not reachable from
		/// outside", never "quiesced" - and D5's rollback-on-verification-failure has to tell those apart to
		/// answer cleanup_outcome honestly.</summary>
		internal static string CommandQuiescenceState { get; private set; } = "not_required";

		/// <summary>Whether the last teardown left the endpoint's listener thread still unwinding:
		/// not_required on runtimes that tolerate it, otherwise stopped or running. See
		/// <see cref="AwaitListenerOf"/>.</summary>
		internal static string ListenerTeardownState { get; private set; } = "not_required";

		/// <summary>The endpoint's listener thread died on an exception rather than on shutdown, if it did.
		/// Reported so that "the endpoint stopped" and "the endpoint was stopped" are distinguishable.</summary>
		internal static string? EndpointListenerFailure { get; private set; }

		/// <summary>Test seam: makes the runtime disposal on the startup rollback path fail, so that
		/// "a cleanup failure must not erase the startup failure" can be exercised without a probe that
		/// really breaks. Null in production; the only writer is the bootstrap test project, through
		/// InternalsVisibleTo.</summary>
		internal static Action? StartupRollbackFaultForTest;
		internal static int PipeConstructionCountForTest;

		/// <summary>Test seam: wraps the pipe server the startup rollback is about to tear down, so that a
		/// rollback whose endpoint teardown *also* fails can be exercised. The wrapper is what gets retained,
		/// so a wrapper that keeps failing keeps the retention alive across retries - which is the state the
		/// failed-start orphaning defect lived in. Null in production; the only writer is the bootstrap test
		/// project, through InternalsVisibleTo.</summary>
		internal static Func<IDisposable?, IDisposable?>? StartupRollbackServerFaultForTest;

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
			lock (Gate) {
				currentRuntime = runtime; currentServer = server;
				runtime = null; server = null; preparedPipeName = ""; preparedEndpointNonceBase64 = "";
				// Retention describes the handles now in hand. Cleanup sets it again for whichever fails
				// this time; leaving it set here would report wreckage that has just been cleared.
				runtimeRetained = false; serverRetained = false;
			}
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
				lock (Gate) { runtime = runtime ?? runtimeHandle; runtimeRetained = true; }
			}
			if (serverHandle != null) {
				report.EndpointAttempted = true;
				try { serverHandle.Dispose(); }
				catch (Exception ex) {
					report.EndpointError = Describe(ex);
					report.EndpointMayBeLive = true;
					report.RetryPossible = true;
					lock (Gate) { server = server ?? serverHandle; serverRetained = true; }
				}
				// After the dispose attempt, whether or not it threw: a teardown that failed leaves both an
				// endpoint that may be live and possibly a handler still running, and the second is not
				// answered by the first. Retained on in_flight for the same reason a failed dispose is -
				// the retry has something to do, and nothing may publish over a probe still being mutated.
				report.CommandQuiescence = QuiesceOf(serverHandle, QuiescenceTimeoutMilliseconds);
				if (report.CommandQuiescence == "in_flight") {
					report.RetryPossible = true;
					lock (Gate) { server = server ?? serverHandle; serverRetained = true; }
				}
				report.ListenerFailure = ListenerFailureOf(serverHandle);
				report.ListenerTeardown = AwaitListenerOf(serverHandle, ListenerTeardownTimeoutMilliseconds);
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
				CommandQuiescenceState = report.CommandQuiescence;
				ListenerTeardownState = report.ListenerTeardown;
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
		/// <summary>Asks the endpoint, without naming its type, whether any command is still inside the
		/// handler - cancelling first, which is what ProbePipeServer.TryQuiesce does. Reflection for the same
		/// reason ListenerFailureOf uses it: this runs on paths where the payload may never have loaded, and
		/// a probe type named here would be resolved when this method is jitted.
		///
		/// A handle without the contract - a test double, or any future endpoint that cannot be asked -
		/// answers not_required rather than quiesced. Absence of the question is not an answer to it, but it
		/// is also not evidence of work in flight, and treating it as in_flight would make every cleanup
		/// ambiguous forever.</summary>
		static string QuiesceOf(IDisposable serverHandle, int timeoutMilliseconds) {
			try {
				var method = serverHandle.GetType().GetMethod("TryQuiesce", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
				if (method == null) return "not_required";
				return true.Equals(method.Invoke(serverHandle, new object[] { timeoutMilliseconds })) ? "quiesced" : "in_flight";
			}
			// An endpoint that cannot answer must not be reported as quiesced: it was asked and did not say
			// no command was running.
			catch (Exception) { return "in_flight"; }
		}

		/// <summary>Waits, where the runtime requires it, for the listener thread to be gone before this
		/// returns - and reports whether it went.
		///
		/// <para>Mono crashes at process exit if a thread is still unwinding out of a disposed
		/// <c>NamedPipeServerStream</c>: measured 5 times in 5 on Unity's Mono 6.13 with a fifteen-line
		/// reproduction containing no HookLab in it, and 0 in 5 on CLR v4.
		/// <c>ProbePipeServer.Dispose</c> deliberately does not wait - the caller can be the target's own
		/// thread inside a func-eval, where a two-second wait outran the evaluation timeout and destroyed a
		/// correct guard report on its way out - so the wait belongs here, at retirement, and only where it
		/// buys something.</para>
		///
		/// <para>Restricted to the runtimes that need it rather than applied to all three, because CLR v4
		/// and CoreCLR tolerate the unwinding thread and this teardown path is shared with them. Reported
		/// rather than assumed: a listener that did not stop in time is exactly the state that would crash
		/// the target afterwards, and no report may claim a clean stop over it.</para></summary>
		static string AwaitListenerOf(IDisposable serverHandle, int timeoutMilliseconds) {
			if (!ListenerTeardownMustBeAwaited) return "not_required";
			try {
				var method = serverHandle.GetType().GetMethod("WaitForShutdown", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
				if (method == null) return "not_required";
				return true.Equals(method.Invoke(serverHandle, new object[] { timeoutMilliseconds })) ? "stopped" : "running";
			}
			// An endpoint that cannot be asked has not been shown to have stopped.
			catch (Exception) { return "running"; }
		}

		/// <summary>True on runtimes whose process teardown does not survive a listener thread that is still
		/// unwinding. Mono is the one, and it is asked by runtime type rather than by corlib name: Mono's
		/// corlib is also <c>mscorlib</c>, so a corlib test would answer this for CLR v4 as well and make
		/// every desktop retirement pay a wait it does not need.</summary>
		static bool ListenerTeardownMustBeAwaited => Type.GetType("Mono.Runtime") != null;

		static int endpointExitHookInstalled;

		/// <summary>Makes sure a target that has hosted a resident can still exit, on the runtimes where
		/// that is not free.
		///
		/// <para>A resident is meant to outlive the debugger: it stays loaded, its endpoint stays up, and
		/// retirement is an explicit operation. CLR v4 and CoreCLR abandon a background listener parked in
		/// a blocking read when the process goes down, so this costs them nothing and they do not take it.
		/// Mono does not abandon it. Measured on mono-project 6.12: a fixture that had hosted a resident
		/// stopped its own loop on request and then never exited at all, while the same fixture without a
		/// resident exited immediately. For a game that is a window the operator can no longer close, which
		/// is a worse outcome than anything the resident was installed to observe.</para>
		///
		/// <para>The endpoint only, deliberately. Unpatching here would run the patch engine over a runtime
		/// that is already tearing down, to restore methods nobody will call again - the retirement path
		/// exists for callers who want that, and it is the one that reports what it managed to do. This
		/// hook reports nothing and cannot: there is no longer anyone to report to.</para></summary>
		static void ReleaseEndpointWhenTheProcessExits() {
			if (!ListenerTeardownMustBeAwaited) return;
			if (Interlocked.Exchange(ref endpointExitHookInstalled, 1) != 0) return;
			AppDomain.CurrentDomain.ProcessExit += (sender, arguments) => {
				IDisposable? handle;
				lock (Gate) handle = server as IDisposable;
				if (handle == null) return;
				// Nothing here may throw. A handler that faults during process exit turns "the target could
				// not close" into "the target crashed on the way out", which is the same defect wearing a
				// worse report.
				try { handle.Dispose(); } catch (Exception) { }
				try { AwaitListenerOf(handle, ListenerTeardownTimeoutMilliseconds); } catch (Exception) { }
			};
		}

		/// <summary>Long enough for a listener released by its own endpoint's disposal, short enough that a
		/// wedged one is reported rather than waited on. Deliberately not the func-eval budget
		/// <see cref="QuiescenceTimeoutMilliseconds"/> answers to: the runtimes that take this wait do not
		/// reach retirement through a CorDebug evaluation.</summary>
		internal const int ListenerTeardownTimeoutMilliseconds = 2000;

		static string? ListenerFailureOf(IDisposable serverHandle) {
			try {
				var property = serverHandle.GetType().GetProperty("ListenerFailure", BindingFlags.Public | BindingFlags.Instance);
				return property == null ? null : property.GetValue(serverHandle) as string;
			}
			catch (Exception) { return null; }
		}

		/// <summary>Proves this process is the target the caller named, before anything is loaded into it.
		///
		/// <para>The guard used to run first simply by being the first line of
		/// <c>ProbeInitializer.Initialize</c>. It has to be lifted out because the patch engine now loads
		/// ahead of that call - see <c>HookLabBootstrap.LoadPatchEngine</c> - and loading a patch engine
		/// into a process nobody has confirmed is the target would be a side effect on an unauthorised
		/// process. Initialize still validates; this only moves the first check earlier, and re-measuring
		/// the live identity is cheap beside the thing it is gating.</para>
		///
		/// <para>Its own non-inlined method, and it names no probe type that reaches the patch engine, so
		/// that preparing it does not demand the engine it is supposed to run before.</para></summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		internal static void ValidateTarget(BootstrapParameters parameters) {
			var expected = new TargetIdentity(parameters.HostId, parameters.ImagePath, parameters.ProcessId,
				new DateTime(parameters.ProcessCreationUtcTicks, DateTimeKind.Utc), parameters.Architecture,
				parameters.RuntimeId, parameters.AppDomainId);
			MethodGuards.ValidateTarget(expected, new LiveTargetIdentityProvider(parameters.HostId).GetCurrentIdentity());
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
				if (parameters.Endpoint == "pipe") { pipe = new ProbePipeServer(HandleCommand); PipeConstructionCountForTest++; ReleaseEndpointWhenTheProcessExits(); }
				var initialization = new ProbeInitialization(expected, provider, pipe, parameters.EventCapacity, parameters.ByteCapacity);
				// T04 owns the ordering inside here: the target guard is validated before any Harmony type
				// resolves, then the backend inventory runs. Nothing above may touch HarmonyLib.
				probe = ProbeInitializer.Initialize(initialization);
				lock (Gate) { runtime = probe; server = pipe; }
				var endpoint = pipe?.TakeInitialEndpoint();
				var actual = provider.GetCurrentIdentity();
				var outcome = new BootstrapOutcome {
					ProbeInstanceId = probe.ProbeInstanceId,
					ProtocolVersion = ProbeWireProtocol.ProtocolVersion,
					BackendIdentity = probe.Inventory.SelectedIdentity,
					BackendResident = probe.Inventory.UseResident,
					InventoryIdentities = string.Join(";", probe.Inventory.LoadedIdentities.ToArray()),
					PipeName = endpoint?.PipeName ?? "",
					SecretBase64 = endpoint == null ? "" : Convert.ToBase64String(endpoint.Secret),
					EndpointNonceBase64 = endpoint == null ? "" : Convert.ToBase64String(endpoint.EndpointNonce),
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
				lock (Gate) { runtime = null; server = null; runtimeRetained = false; serverRetained = false; }
				var faulted = StartupRollbackFaultForTest;
				var wrapServer = StartupRollbackServerFaultForTest;
				Record(Cleanup(faulted == null ? (object?)probe : new FaultingHandle(faulted),
					wrapServer == null ? pipe : wrapServer(pipe)));
				// The startup failure is what the caller asked about, so it is what leaves this frame. The
				// cleanup outcome rides in the reporting fields instead of replacing it.
				throw;
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		internal static BootstrapOutcome Prepare(BootstrapParameters parameters) {
			if (parameters.Endpoint != "none" && parameters.Endpoint != "pipe") throw new InvalidOperationException("Prepare requires endpoint=none or endpoint=pipe.");
			var expected = new TargetIdentity(parameters.HostId, parameters.ImagePath, parameters.ProcessId,
				new DateTime(parameters.ProcessCreationUtcTicks, DateTimeKind.Utc), parameters.Architecture,
				parameters.RuntimeId, parameters.AppDomainId);
			var provider = new LiveTargetIdentityProvider(parameters.HostId);
			ProbePipeServer? pipe = null;
			ProbeRuntime? probe = null;
			try {
				if (parameters.Endpoint == "pipe") {
					var endpointSecret = parameters.EndpointSecret;
					pipe = new ProbePipeServer(HandleCommand, injectedSecret: endpointSecret, authenticationEnabled: endpointSecret != null, controllerSid: parameters.ControllerSid);
					PipeConstructionCountForTest++;
					ReleaseEndpointWhenTheProcessExits();
					if (!pipe.WaitUntilListening(2000)) throw new InvalidOperationException("HookLab probe listener did not become ready: " + (pipe.ListenerFailure ?? "timeout"));
				}
				// The public HookLab service uses explicit bounded drain commands. Do not also register the pipe
				// as a push consumer here: that would remove events from the authoritative buffer before the
				// caller's cursor read. The older one-shot Start path retains its push behavior above.
				probe = ProbeInitializer.Initialize(new ProbeInitialization(expected, provider, null, parameters.EventCapacity, parameters.ByteCapacity));
				var endpoint = pipe == null || pipe.SecretWasInjected ? null : pipe.TakeInitialEndpoint();
				lock (Gate) {
					runtime = probe; server = pipe;
					preparedPipeName = pipe?.PipeName ?? "";
					preparedEndpointNonceBase64 = pipe == null ? "" : Convert.ToBase64String(pipe.EndpointNonce);
				}
				var outcome = Outcome(probe, provider.GetCurrentIdentity());
				outcome.PipeName = pipe?.PipeName ?? "";
				outcome.SecretBase64 = endpoint == null ? "" : Convert.ToBase64String(endpoint.Secret);
				outcome.EndpointNonceBase64 = pipe == null ? "" : Convert.ToBase64String(pipe.EndpointNonce);
				return outcome;
			}
			catch {
				lock (Gate) { runtime = null; server = null; preparedPipeName = ""; preparedEndpointNonceBase64 = ""; }
				Cleanup(probe, pipe);
				throw;
			}
		}

		internal static BootstrapOutcome CommitPrepared(BootstrapParameters parameters) {
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not prepared.");
			var outcome = Outcome(probe, probe.GetState().Target);
			lock (Gate) { outcome.PipeName = preparedPipeName; outcome.EndpointNonceBase64 = preparedEndpointNonceBase64; }
			if (parameters.HasHook) {
				ResidentLauncher.NotePatchInstall();
				if (parameters.OptionalHook("hook_source_base64") != null) {
					var compiled = InstallCompiledHook(probe, parameters);
					outcome.PatchId = compiled.PatchId;
					outcome.HooksVersion = compiled.HooksVersion;
				}
				else {
					var observed = InstallHook(probe, parameters);
					outcome.PatchId = observed.PatchId;
					outcome.HooksVersion = observed.HooksVersion;
				}
			}
			return outcome;
		}

		internal static string DrainEvents(int maximumCount) {
			if (maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			var events = probe.Events.Drain(maximumCount);
			var lines = new List<string> {
				"status=ok", "count=" + events.Count.ToString(CultureInfo.InvariantCulture),
				"dropped=" + probe.Events.DroppedCount.ToString(CultureInfo.InvariantCulture),
				"residency_commit=completed", "behavior_commit=" + (probe.HooksVersion == 0 ? "not_started" : "completed"),
				"prototype_compromises=endpoint_none,identity_partly_self_asserted,no_residency_rollback"
			};
			for (var index = 0; index < events.Count; index++) {
				var item = events[index];
				lines.Add("event_" + index.ToString(CultureInfo.InvariantCulture) + "=" +
					item.Sequence.ToString(CultureInfo.InvariantCulture) + "|" + item.PatchId + "|" + item.PayloadJson.Replace("\r", " ").Replace("\n", " "));
			}
			return string.Join("\n", lines.ToArray()) + "\n";
		}

		internal static string InstallPrepared(BootstrapParameters parameters) {
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			var result = InstallHook(probe, parameters);
			return OperationReport(result);
		}

		internal static string InstallCompiledPrepared(BootstrapParameters parameters) {
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			var result = InstallCompiledHook(probe, parameters);
			return "status=ok\npatch_id=" + result.PatchId + "\nhooks_version=" + result.HooksVersion.ToString(CultureInfo.InvariantCulture) +
				"\nrevision=" + result.Revision.ToString(CultureInfo.InvariantCulture) + "\nchanged=" + (result.Changed ? "true" : "false") +
				"\nresidency_commit=completed\nbehavior_commit=completed\nprototype_compromises=unauthenticated_pipe,identity_partly_self_asserted,no_residency_rollback\n";
		}

		static CompiledPatchOperationResult InstallCompiledHook(ProbeRuntime probe, BootstrapParameters parameters) {
			var target = ResolveHook(parameters);
			if (target.Document.Kind != HookKind.Prefix && target.Document.Kind != HookKind.Postfix && target.Document.Kind != HookKind.Finalizer && target.Document.Kind != HookKind.Transpiler) throw new InvalidOperationException("Compiled hooks currently support Prefix, Postfix, Finalizer, and Transpiler only.");
			var sourceText = parameters.Hook("hook_source_base64");
			string source;
			try { source = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sourceText)); }
			catch (FormatException ex) { throw new InvalidOperationException("hook_source_base64 is not valid base64.", ex); }
			var revision = Positive(parameters, "hook_revision", 1);
			return probe.InstallCompiledHook(target.Method, target.Document, source, revision, probe.HooksVersion);
		}

		internal static string UninstallPrepared(string patchId) {
			if (string.IsNullOrWhiteSpace(patchId)) throw new ArgumentException("A patch id is required.", nameof(patchId));
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			return OperationReport(probe.Uninstall(patchId, probe.HooksVersion));
		}

		internal static string SetEnabledPrepared(string patchId, bool enabled) {
			if (string.IsNullOrWhiteSpace(patchId)) throw new ArgumentException("A patch id is required.", nameof(patchId));
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			return OperationReport(probe.SetEnabled(patchId, enabled, probe.HooksVersion));
		}

		static string OperationReport(PatchOperationResult result) =>
			"status=ok\npatch_id=" + result.PatchId + "\nhooks_version=" + result.HooksVersion.ToString(CultureInfo.InvariantCulture) +
			"\nchanged=" + (result.Changed ? "true" : "false") + "\nresidency_commit=completed\nbehavior_commit=completed\n" +
			"prototype_compromises=identity_partly_self_asserted,no_residency_rollback\n";

		static BootstrapOutcome Outcome(ProbeRuntime probe, TargetIdentity actual) => new BootstrapOutcome {
			ProbeInstanceId = probe.ProbeInstanceId,
			ProtocolVersion = ProbeWireProtocol.ProtocolVersion,
			BackendIdentity = probe.Inventory.SelectedIdentity,
			BackendResident = probe.Inventory.UseResident,
			InventoryIdentities = string.Join(";", probe.Inventory.LoadedIdentities.ToArray()),
			HooksVersion = probe.HooksVersion,
			TargetProcessId = actual.ProcessId,
			TargetImagePath = actual.ImagePath,
		};

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
			var target = ResolveHook(parameters);
			return probe.Install(target.Method, target.Document, probe.HooksVersion);
		}

		static ResolvedHook ResolveHook(BootstrapParameters parameters) {
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
			var limits = new HookLimits(Positive(parameters, "maximum_events_per_second", 100), 64 * 1024, 8, 64, Positive(parameters, "maximum_string_length", 1024), 5);
			var document = new HookDocument(1, parameters.Hook("hook_id"), kind, guard, "{}", limits, true);
			// ProbeRuntime.Install re-validates the target identity and every method guard field. The values
			// come from the caller, so a wrong module, token, signature or IL digest refuses here.
			return new ResolvedHook(method, document);
		}

		sealed class ResolvedHook {
			internal ResolvedHook(MethodBase method, HookDocument document) { Method = method; Document = document; }
			internal MethodBase Method { get; }
			internal HookDocument Document { get; }
		}

		static int Positive(BootstrapParameters parameters, string name, int fallback) {
			var text = parameters.OptionalHook(name);
			if (string.IsNullOrWhiteSpace(text)) return fallback;
			if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
				throw new InvalidOperationException(name + " must be a positive whole number.");
			return value;
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

		/// <summary>The bootstrap endpoint's only command. It is a read, so the cancellation token has nothing
		/// to abandon part-way - but it is honoured before the read rather than ignored, because a probe that
		/// is being torn down should not answer as though it were not, and because the next handler added
		/// here will be side-effecting and must inherit the habit rather than the exception.</summary>
		static ProbeCommandResult HandleCommand(string operation, string payloadJson, long? expectedHooksVersion, CancellationToken cancellationToken) {
			cancellationToken.ThrowIfCancellationRequested();
			ProbeRuntime probe;
			lock (Gate) probe = runtime as ProbeRuntime ?? throw new InvalidOperationException("The probe is not initialized yet.");
			if (expectedHooksVersion.HasValue && expectedHooksVersion.Value != probe.HooksVersion)
				throw new InvalidOperationException("The expected hooks version does not match the resident probe.");
			if (string.Equals(operation, "status", StringComparison.Ordinal)) {
				var state = probe.GetState();
				return new ProbeCommandResult(StatusJson(state), state.HooksVersion);
			}
			if (string.Equals(operation, "drain", StringComparison.Ordinal)) {
				if (!int.TryParse(payloadJson, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximum) || maximum < 1 || maximum > 256)
					throw new ArgumentException("Drain payload must be an integer from 1 through 256.");
				return new ProbeCommandResult(DrainEvents(maximum), probe.HooksVersion);
			}
			if (string.Equals(operation, "uninstall", StringComparison.Ordinal))
				return new ProbeCommandResult(UninstallPrepared(payloadJson), probe.HooksVersion);
			if (string.Equals(operation, "enable", StringComparison.Ordinal))
				return new ProbeCommandResult(SetEnabledPrepared(payloadJson, true), probe.HooksVersion);
			if (string.Equals(operation, "disable", StringComparison.Ordinal))
				return new ProbeCommandResult(SetEnabledPrepared(payloadJson, false), probe.HooksVersion);
			if (string.Equals(operation, "install", StringComparison.Ordinal))
				return new ProbeCommandResult(InstallPrepared(BootstrapParameters.Parse(payloadJson)), probe.HooksVersion);
			if (string.Equals(operation, "install_compiled_prefix", StringComparison.Ordinal))
				return new ProbeCommandResult(InstallCompiledPrepared(BootstrapParameters.Parse(payloadJson)), probe.HooksVersion);
			throw new NotSupportedException("Unsupported probe operation: " + operation);
		}

		static string StatusJson(ProbeState state) =>
			"{\"protocol_version\":" + state.ProtocolVersion.ToString(CultureInfo.InvariantCulture) +
			",\"probe_instance_id\":\"" + Escape(state.ProbeInstanceId) +
			"\",\"backend_identity\":\"" + Escape(state.BackendIdentity) +
			"\",\"hooks_version\":" + state.HooksVersion.ToString(CultureInfo.InvariantCulture) +
			",\"process_id\":" + state.Target.ProcessId.ToString(CultureInfo.InvariantCulture) +
			",\"image_path\":\"" + Escape(state.Target.ImagePath) +
			"\",\"appdomain_id\":\"" + Escape(state.Target.AppDomainId) +
			"\",\"patch_ids\":[" + string.Join(",", state.PatchIds.Select(id => "\"" + Escape(id) + "\"").ToArray()) + "]" +
			",\"compiled_hooks\":[" + string.Join(",", state.CompiledHooks.Select(CompiledHookJson).ToArray()) + "]" +
			",\"shadowed_hooks\":[" + string.Join(",", state.ShadowedHooks.Select(ShadowedHookJson).ToArray()) + "]}";

		static string ShadowedHookJson(ShadowedHookState hook) =>
			"{\"patch_id\":\"" + Escape(hook.PatchId) + "\",\"declaring_type\":\"" + Escape(hook.DeclaringType) +
			"\",\"shadowing_assembly\":\"" + Escape(hook.ShadowingAssembly) + "\"}";

		static string CompiledHookJson(CompiledHookState hook) => "{\"patch_id\":\"" + Escape(hook.PatchId) + "\",\"assembly_simple_name\":\"" + Escape(hook.AssemblySimpleName) + "\",\"kind\":\"" + hook.Kind.ToString() + "\",\"module_mvid\":\"" + hook.Target.ModuleMvid.ToString("D") + "\",\"metadata_token\":" + hook.Target.MetadataToken.ToString(CultureInfo.InvariantCulture) + ",\"declaring_type\":\"" + Escape(hook.Target.DeclaringType) + "\",\"signature\":\"" + Escape(hook.Target.MethodSignature) + "\",\"il_sha256\":\"" + Escape(hook.Target.IlSha256) + "\",\"source_sha256\":\"" + Escape(hook.SourceSha256) + "\",\"revision\":" + hook.Revision.ToString(CultureInfo.InvariantCulture) + ",\"enabled\":" + (hook.Enabled ? "true" : "false") + "}";

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
