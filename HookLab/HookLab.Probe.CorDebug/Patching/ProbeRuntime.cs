using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class PatchOperationResult {
		internal PatchOperationResult(string patchId, long hooksVersion, bool changed) { PatchId = patchId; HooksVersion = hooksVersion; Changed = changed; }
		public string PatchId { get; }
		public long HooksVersion { get; }
		public bool Changed { get; }
	}

	public sealed class CompiledPatchOperationResult {
		internal CompiledPatchOperationResult(string patchId, long hooksVersion, int revision, bool changed) { PatchId = patchId; HooksVersion = hooksVersion; Revision = revision; Changed = changed; }
		public string PatchId { get; }
		public long HooksVersion { get; }
		public int Revision { get; }
		public bool Changed { get; }
	}

	public sealed class StaleHooksVersionException : InvalidOperationException {
		public StaleHooksVersionException(long expected, long actual) : base($"Stale hooks_version: expected {expected}, actual {actual}.") { }
	}

	public sealed class ProbeRuntime : IDisposable {
		readonly object gate = new object();
		readonly Harmony harmony;
		readonly Dictionary<string, HookContext> hooks = new Dictionary<string, HookContext>(StringComparer.Ordinal);
		readonly Dictionary<string, CompiledHookContext> compiledHooks = new Dictionary<string, CompiledHookContext>(StringComparer.Ordinal);
		readonly ProbeInitialization initialization;
		readonly BoundedEventBuffer buffer;
		long hooksVersion;
		int notificationPending;
		bool disposed;

		ProbeRuntime(ProbeInitialization initialization, BackendInventoryResult inventory) {
			this.initialization = initialization; Inventory = inventory;
			ProbeInstanceId = Guid.NewGuid().ToString("D");
			buffer = new BoundedEventBuffer(initialization.EventCapacity, initialization.ByteCapacity);
			harmony = new Harmony("dgspy.hooklab.probe." + ProbeInstanceId);
			// Watch what else arrives in this domain. A hook patches one method in one assembly, and
			// nothing stops a later assembly from defining the same type and taking over the work.
			assemblyLoad = (sender, args) => NoteAssemblyLoaded(args.LoadedAssembly);
			AppDomain.CurrentDomain.AssemblyLoad += assemblyLoad;
		}

		readonly AssemblyLoadEventHandler assemblyLoad;
		/// <summary>patch id to the assembly that shadowed it. Ordinal, and first writer wins: the
		/// interesting fact is that the hook stopped being reachable, not how many times since.</summary>
		readonly Dictionary<string, ShadowedHookState> shadowed = new Dictionary<string, ShadowedHookState>(StringComparer.Ordinal);

		/// <summary>Records any installed hook whose declaring type is also defined by an assembly that
		/// has just loaded.
		///
		/// <para>The check is one <c>GetType</c> per installed hook rather than an enumeration of the new
		/// assembly's types: enumeration is expensive on every load in a busy process and throws
		/// <see cref="ReflectionTypeLoadException"/> on half-resolvable assemblies, which is a poor reason
		/// to disturb a target. With no hooks installed this does nothing at all.</para>
		///
		/// <para>It reports rather than reacts. Re-patching the new assembly would install a hook the
		/// caller never asked for, on code whose IL nobody guarded; unpatching would destroy a hook that
		/// is still correct for anything holding the old type. Saying so is the useful part - the failure
		/// this exists for is silence, not the shadowing itself.</para></summary>
		void NoteAssemblyLoaded(Assembly loaded) {
			if (loaded == null) return;
			try {
				lock (gate) {
					if (disposed || (hooks.Count == 0 && compiledHooks.Count == 0)) return;
					foreach (var entry in Targets()) NoteShadowing(entry, loaded);
				}
			}
			// A load notification must never be the thing that breaks a target. Losing this observation
			// degrades a diagnostic; throwing here would run inside the CLR's loader callback.
			catch (Exception) { }
		}

		/// <summary>Records one hook as shadowed if the given assembly defines its declaring type from a
		/// different module. Callers hold <see cref="gate"/>.</summary>
		void NoteShadowing((string PatchId, string DeclaringType, Module Module) entry, Assembly candidateAssembly) {
			if (shadowed.ContainsKey(entry.PatchId) || string.IsNullOrEmpty(entry.DeclaringType)) return;
			Type? candidate;
			// A half-resolvable assembly throws from GetType, and a dynamic one can be mid-definition.
			// Neither is a reason to disturb the target.
			try { candidate = candidateAssembly.GetType(entry.DeclaringType, false); } catch (Exception) { return; }
			// Same declaring type from a different module: the new one is what fresh calls resolve to, so
			// the patch is on code that is no longer being entered.
			//
			// Module identity, not the module version id. Two loads of byte-identical assemblies share an
			// MVID and are still two modules, and Harmony patched exactly one of them - so an MVID
			// comparison calls a genuinely shadowed hook healthy whenever the newer generation happens to
			// be an identical copy. What is being asked is "is the type I would resolve now in the module
			// I patched", which only reference identity answers.
			if (candidate == null || ReferenceEquals(candidate.Module, entry.Module)) return;
			shadowed[entry.PatchId] = new ShadowedHookState(entry.PatchId, entry.DeclaringType,
				SafeName(candidateAssembly));
		}

		static string SafeName(Assembly assembly) {
			try { return assembly.FullName ?? assembly.GetName().Name ?? "an unnamed assembly"; }
			catch (Exception) { return "an unnamed assembly"; }
		}

		/// <summary>Checks one freshly installed hook against everything already loaded.
		///
		/// <para>Waiting for a load notification would miss the commonest case entirely. Measured on
		/// 2026-08-20: the IIS worker had <b>both</b> generations of the recompiled page assembly resident
		/// before the hook was installed, so no assembly loaded afterwards and an event-only check would
		/// have stayed silent about a hook that could never fire. Shadowing is a property of the domain,
		/// not of an event.</para>
		///
		/// <para>Callers hold <see cref="gate"/>. One dictionary lookup per loaded assembly, once per
		/// install.</para></summary>
		void NoteShadowingAtInstall(string patchId, MethodBase method) {
			try {
				var entry = (PatchId: patchId, DeclaringType: method.DeclaringType?.FullName ?? "", Module: method.Module);
				if (string.IsNullOrEmpty(entry.DeclaringType)) return;
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
					NoteShadowing(entry, assembly);
					if (shadowed.ContainsKey(patchId)) return;
				}
			}
			catch (Exception) { }
		}

		/// <summary>Every installed hook as (patch id, declaring type, the module it was patched in),
		/// observation and compiled alike. Callers hold <see cref="gate"/>.</summary>
		IEnumerable<(string PatchId, string DeclaringType, Module Module)> Targets() {
			foreach (var hook in hooks.Values)
				yield return (hook.PatchId, hook.Method.DeclaringType?.FullName ?? "", hook.Method.Module);
			foreach (var hook in compiledHooks.Values)
				yield return (hook.PatchId, hook.Method.DeclaringType?.FullName ?? "", hook.Method.Module);
		}

		internal static ProbeRuntime CreateAfterInventory(ProbeInitialization initialization, BackendInventoryResult inventory) => new ProbeRuntime(initialization, inventory);
		public string ProbeInstanceId { get; }
		public BackendInventoryResult Inventory { get; }
		public IHookEventSource Events => buffer;
		public long HooksVersion { get { lock (gate) return hooksVersion; } }
		public bool IsAutoDisabled(string patchId) { lock (gate) return hooks.TryGetValue(patchId, out var context) && context.Disabled; }
		public int? CompiledRevision(string patchId) { lock (gate) return compiledHooks.TryGetValue(patchId, out var context) ? context.Revision : (int?)null; }
		public bool? IsEnabled(string patchId) { lock (gate) { if (hooks.TryGetValue(patchId, out var observed)) return observed.Enabled; if (compiledHooks.TryGetValue(patchId, out var compiled)) return compiled.Enabled; return null; } }

		public CompiledPatchOperationResult InstallCompiledHook(MethodBase method, HookDocument document, string source, int revision, long expectedHooksVersion) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (document == null) throw new ArgumentNullException(nameof(document));
			if (document.Kind != HookKind.Prefix && document.Kind != HookKind.Postfix && document.Kind != HookKind.Finalizer && document.Kind != HookKind.Transpiler) throw new NotSupportedException("Compiled hooks currently support Prefix, Postfix, Finalizer, and Transpiler only.");
			if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
			RejectGenericCarrier(method);
			// Compilation deliberately happens before taking the mutation lock. A failed candidate cannot
			// alter the resident hook set or advance hooks_version.
			var compiled = CompiledHookCompiler.Compile(source, method, document.Kind);
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				MethodGuards.ValidateTarget(initialization.ExpectedTarget, initialization.IdentityProvider.GetCurrentIdentity());
				MethodGuards.ValidateMethod(method, document.Target);
				var patchId = StablePatchId(document.HookId);
				if (hooks.ContainsKey(patchId)) throw new InvalidOperationException("The hook id is already used by an observational hook.");
				compiledHooks.TryGetValue(patchId, out var old);
				if (old != null && revision <= old.Revision) throw new InvalidOperationException("Compiled hook revision must increase.");
				var candidate = new CompiledHookContext(method, patchId, document, source, revision, compiled.Methods) { Enabled=old == null || old.Enabled };
				try {
					if(candidate.Enabled) harmony.Patch(method,
						compiled.Prefix == null ? null : new HarmonyMethod(compiled.Prefix),
						compiled.Postfix == null ? null : new HarmonyMethod(compiled.Postfix),
						compiled.Transpiler == null ? null : new HarmonyMethod(compiled.Transpiler),
						compiled.Finalizer == null ? null : new HarmonyMethod(compiled.Finalizer));
				}
				catch { throw; }
				try {
					if (old != null) foreach (var patchMethod in old.PatchMethods) harmony.Unpatch(old.Method, patchMethod);
					compiledHooks[patchId] = candidate;
					NoteShadowingAtInstall(patchId, method);
					hooksVersion++;
					return new CompiledPatchOperationResult(patchId, hooksVersion, revision, true);
				}
				catch {
					if(candidate.Enabled) foreach (var patchMethod in compiled.Methods) harmony.Unpatch(method, patchMethod);
					throw;
				}
			}
		}

		/// <summary>Refuses a carrier that still has unbound generic parameters, by name and before anything
		/// is compiled or patched.
		///
		/// <para>A generic method definition, or any method on an open generic type, is not one runtime
		/// method: the runtime compiles one per set of type arguments, and the metadata token, signature
		/// and IL digest the guards check all describe the definition rather than any of them. There is
		/// therefore nothing here that patching could intercept.</para>
		///
		/// <para>It was already impossible, and that is the point of stating it. Measured 2026-08-21 on
		/// Mono 6.12: offering a generic method to <c>create_hook</c> reached Harmony and came back
		/// <c>NotSupportedException: Specified method is not supported.</c> - a refusal, but one that names
		/// neither the method nor the reason, and reads like a defect in HookLab rather than a property of
		/// the target. <c>get_hook_template</c> already refused the same shape by name, but a template is
		/// an authoring convenience: an exported hook package or a hand-written request reaches this path
		/// without ever asking for one.</para></summary>
		static void RejectGenericCarrier(MethodBase method) {
			if (!method.ContainsGenericParameters) return;
			throw new NotSupportedException("HookLab cannot patch a generic carrier: '" +
				(method.DeclaringType?.FullName ?? "") + "." + method.Name + "' still has unbound generic parameters, so the runtime " +
				"compiles one method per set of type arguments and there is no single method to intercept. " +
				"Choose a non-generic carrier.");
		}

		public PatchOperationResult Install(MethodBase method, HookDocument document, long expectedHooksVersion) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (document == null) throw new ArgumentNullException(nameof(document));
			RejectGenericCarrier(method);
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				MethodGuards.ValidateTarget(initialization.ExpectedTarget, initialization.IdentityProvider.GetCurrentIdentity());
				MethodGuards.ValidateMethod(method, document.Target);
				var patchId = StablePatchId(document.HookId);
				if (hooks.TryGetValue(patchId, out var old)) RemoveCore(old);
				var phaseAlreadyPatched = hooks.Values.Any(value => value.Method == method && value.Document.Kind == document.Kind);
				var context = new HookContext(this, method, patchId, document, hooksVersion + 1);
				HookDispatch.Register(context);
				try { if (!phaseAlreadyPatched) ApplyPatch(method, document.Kind); } catch { HookDispatch.Unregister(context); throw; }
				hooks[patchId] = context; NoteShadowingAtInstall(patchId, method); hooksVersion++;
				return new PatchOperationResult(patchId, hooksVersion, true);
			}
		}

		public PatchOperationResult Uninstall(string patchId, long expectedHooksVersion) {
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				if (hooks.TryGetValue(patchId, out var context)) RemoveCore(context);
				else if (compiledHooks.TryGetValue(patchId, out var compiled)) { foreach (var patchMethod in compiled.PatchMethods) harmony.Unpatch(compiled.Method, patchMethod); compiledHooks.Remove(patchId); }
				else return new PatchOperationResult(patchId, hooksVersion, false);
				hooksVersion++; return new PatchOperationResult(patchId, hooksVersion, true);
			}
		}

		public PatchOperationResult SetEnabled(string patchId, bool enabled, long expectedHooksVersion) {
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				if (hooks.TryGetValue(patchId, out var observed)) {
					if (observed.Enabled == enabled) return new PatchOperationResult(patchId, hooksVersion, false);
					observed.Enabled = enabled;
				}
				else if (compiledHooks.TryGetValue(patchId, out var compiled)) {
					if (compiled.Enabled == enabled) return new PatchOperationResult(patchId, hooksVersion, false);
					if (enabled) ApplyCompiled(compiled); else foreach (var patchMethod in compiled.PatchMethods) harmony.Unpatch(compiled.Method, patchMethod);
					compiled.Enabled = enabled;
				}
				else throw new KeyNotFoundException("Hook patch was not found: " + patchId);
				hooksVersion++;
				return new PatchOperationResult(patchId, hooksVersion, true);
			}
		}

		public ProbeState GetState() {
			lock (gate) return new ProbeState(1, ProbeInstanceId, initialization.IdentityProvider.GetCurrentIdentity(), Inventory.SelectedIdentity, hooksVersion, hooks.Keys.Concat(compiledHooks.Keys).OrderBy(x => x).ToArray(),compiledHooks.Values.OrderBy(x=>x.PatchId).Select(x=>new CompiledHookState(x.PatchId,x.Method.Module.Assembly.GetName().Name??x.Method.Module.Name,x.Document.Kind,x.Document.Target,Sha256(x.Source),x.Revision,x.Enabled)).ToArray(),
				// Only hooks that still exist: a removed hook's shadowing is not news, and reporting it
				// would keep a resolved problem on the screen.
				shadowed.Values.Where(x=>hooks.ContainsKey(x.PatchId)||compiledHooks.ContainsKey(x.PatchId)).OrderBy(x=>x.PatchId,StringComparer.Ordinal).ToArray());
		}
		static string Sha256(string value) { using(var sha=SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(x=>x.ToString("x2")).ToArray()); }

		void ApplyPatch(MethodBase method, HookKind kind) {
			HarmonyMethod? prefix = null, postfix = null, finalizer = null;
			var dispatch = DispatchMethod(kind);
			if (kind == HookKind.Prefix) prefix = new HarmonyMethod(dispatch);
			else if (kind == HookKind.Postfix) postfix = new HarmonyMethod(dispatch);
			else finalizer = new HarmonyMethod(dispatch);
			harmony.Patch(method, prefix, postfix, null, finalizer);
		}
		void ApplyCompiled(CompiledHookContext context) {
			var prefix=context.PatchMethods.SingleOrDefault(method=>method.Name=="Prefix");
			var postfix=context.PatchMethods.SingleOrDefault(method=>method.Name=="Postfix");
			var finalizer=context.PatchMethods.SingleOrDefault(method=>method.Name=="Finalizer");
			var transpiler=context.PatchMethods.SingleOrDefault(method=>method.Name=="Transpiler");
			harmony.Patch(context.Method,prefix is null?null:new HarmonyMethod(prefix),postfix is null?null:new HarmonyMethod(postfix),transpiler is null?null:new HarmonyMethod(transpiler),finalizer is null?null:new HarmonyMethod(finalizer));
		}

		void RemoveCore(HookContext context) {
			HookDispatch.Unregister(context); hooks.Remove(context.PatchId);
			if (!hooks.Values.Any(value => value.Method == context.Method && value.Document.Kind == context.Document.Kind))
				harmony.Unpatch(context.Method, DispatchMethod(context.Document.Kind));
		}
		static MethodInfo DispatchMethod(HookKind kind) {
			var name = kind == HookKind.Prefix ? nameof(HookDispatch.Prefix) : kind == HookKind.Postfix ? nameof(HookDispatch.Postfix) : kind == HookKind.Finalizer ? nameof(HookDispatch.Finalizer) : throw new NotSupportedException("Unsupported hook kind: " + kind);
			return typeof(HookDispatch).GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(typeof(HookDispatch).FullName, name);
		}
		void CheckVersion(long expected) { if (expected != hooksVersion) throw new StaleHooksVersionException(expected, hooksVersion); }
		void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ProbeRuntime)); }
		string StablePatchId(string hookId) => ProbeInstanceId + ":" + hookId;
		internal void Notify() {
			if (initialization.Consumer == null || Interlocked.Exchange(ref notificationPending, 1) != 0) return;
			ThreadPool.QueueUserWorkItem(_ => Deliver());
		}

		// Clearing the pending flag only after the consumer returned used to lose a wake-up: an event
		// appended between that final drain and the clear saw the flag still set, suppressed its own
		// notification, and then sat in the buffer with nothing scheduled until an unrelated later event
		// arrived. On a hot hook the next event hid it; on a rare hook the stranded event was often the
		// only one. So clear first, then re-check the buffer and re-arm - whoever clears owns the
		// suppressed wake-up.
		void Deliver() {
			var consumer = initialization.Consumer!;
			while (true) {
				var drainedBefore = buffer.DrainedCount;
				try { consumer.EventsAvailable(buffer); }
				catch (Exception ex) { RecordConsumerFailure(ex); }
				Volatile.Write(ref notificationPending, 0);
				if (!buffer.HasPendingEvents) return;
				// Re-arm only when the consumer actually took events. A consumer that returns without
				// draining is normal - the pipe worker does exactly that while disconnected, which is the
				// state "hooks survive detach" requires - and looping on it burns a core inside the
				// target for the life of the process. Leave the flag clear; the next append schedules a
				// fresh attempt, so nothing is stranded.
				if (buffer.DrainedCount == drainedBefore) return;
				if (Interlocked.Exchange(ref notificationPending, 1) != 0) return;
			}
		}

		long consumerDispatchFailures;
		string? lastConsumerDispatchError;
		long reentrantEventsSuppressed;
		long rateLimitedEventsDropped;

		/// <summary>Consumer dispatch failures are counted and their last message kept rather than
		/// swallowed: a consumer that throws on every delivery otherwise looks exactly like one that
		/// works, and nothing can act on a failure it cannot see.</summary>
		public long ConsumerDispatchFailures { get { lock (gate) return consumerDispatchFailures; } }
		public string? LastConsumerDispatchError { get { lock (gate) return lastConsumerDispatchError; } }
		public long ReentrantEventsSuppressed { get { lock (gate) return reentrantEventsSuppressed; } }
		public long RateLimitedEventsDropped { get { lock (gate) return rateLimitedEventsDropped; } }
		void RecordConsumerFailure(Exception ex) {
			lock (gate) { consumerDispatchFailures++; lastConsumerDispatchError = ex.GetType().FullName + ": " + ex.Message; }
		}
		internal void RecordReentrantSuppression() { lock (gate) reentrantEventsSuppressed++; }
		internal void RecordRateLimitedDrop() { lock (gate) rateLimitedEventsDropped++; }
		internal BoundedEventBuffer Buffer => buffer;

		public void Dispose() {
			// Before the lock: the handler runs inside the CLR's loader callback, and leaving it attached
			// to a disposed runtime is a subscription on a dead object for the life of the AppDomain.
			try { AppDomain.CurrentDomain.AssemblyLoad -= assemblyLoad; } catch (Exception) { }
			lock (gate) { if (disposed) return; foreach (var item in hooks.Values.ToArray()) RemoveCore(item); foreach (var item in compiledHooks.Values.ToArray()) { foreach (var patchMethod in item.PatchMethods) harmony.Unpatch(item.Method, patchMethod); compiledHooks.Remove(item.PatchId); } shadowed.Clear(); disposed = true; }
		}
	}

	internal sealed class CompiledHookContext {
		internal CompiledHookContext(MethodBase method, string patchId, HookDocument document, string source, int revision, MethodInfo[] patchMethods) { Method = method; PatchId = patchId; Document = document; Source = source; Revision = revision; PatchMethods = patchMethods; }
		internal MethodBase Method { get; }
		internal string PatchId { get; }
		internal HookDocument Document { get; }
		internal string Source { get; }
		internal int Revision { get; }
		internal bool Enabled { get; set; }=true;
		internal MethodInfo[] PatchMethods { get; }
	}

	internal sealed class HookContext {
		// Per-thread, not per-context: the hazard is a hook re-entering while its own capture runs on the
		// same thread, which an instance flag cannot express. An instance flag also rejects genuinely
		// concurrent invocations from other threads, which are distinct events that belong in the buffer.
		[ThreadStatic] static bool dispatching;
		long sequence; int consecutiveFailures; long rateSecond; int rateCount;
		internal HookContext(ProbeRuntime runtime, MethodBase method, string patchId, HookDocument document, long installedVersion) {
			Runtime = runtime; Method = method; PatchId = patchId; Document = document; InstalledVersion = installedVersion;
		}
		internal ProbeRuntime Runtime { get; } internal MethodBase Method { get; } internal string PatchId { get; }
		internal HookDocument Document { get; } internal long InstalledVersion { get; } internal bool Disabled { get; private set; } internal bool Enabled { get; set; }=true;
		internal void Invoke(object? instance, object[] args, object? result, Exception? exception) {
			if (!Enabled || Disabled) return;
			if (dispatching) { Runtime.RecordReentrantSuppression(); return; }
			dispatching = true;
			try {
				var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
				if (Interlocked.Read(ref rateSecond) != second) { Interlocked.Exchange(ref rateSecond, second); Interlocked.Exchange(ref rateCount, 0); }
				if (Interlocked.Increment(ref rateCount) > Document.Limits.MaximumEventsPerSecond) { Runtime.RecordRateLimitedDrop(); return; }
				if (Document.BehaviorJson.IndexOf("\"throw\":true", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Injected hook behavior failure.");
				var capture = BoundedCapture.Serialize(new CaptureEnvelope(instance, args, result, exception), Document.Limits);
				var sequenceValue = Interlocked.Increment(ref sequence);
				Runtime.Buffer.TryAppend(dropped => new HookEvent(Runtime.ProbeInstanceId, PatchId, InstalledVersion, sequenceValue,
					DateTime.UtcNow, Thread.CurrentThread.ManagedThreadId, capture.Json, capture.Truncated, dropped));
				Interlocked.Exchange(ref consecutiveFailures, 0); Runtime.Notify();
			} catch { if (Interlocked.Increment(ref consecutiveFailures) >= Document.Limits.MaximumConsecutiveFailures) Disabled = true; }
			finally { dispatching = false; }
		}
		sealed class CaptureEnvelope {
			internal CaptureEnvelope(object? instance, object[] arguments, object? result, Exception? exception) { Instance = instance; Arguments = arguments; Result = result; Exception = exception; }
			public readonly object? Instance; public readonly object[] Arguments; public readonly object? Result; public readonly Exception? Exception;
		}
	}

	public static class HookDispatch {
		static readonly ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, HookContext>> contexts = new ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, HookContext>>();
		internal static void Register(HookContext context) => contexts.GetOrAdd(context.Method, _ => new ConcurrentDictionary<string, HookContext>())[context.PatchId] = context;
		internal static void Unregister(HookContext context) { if (contexts.TryGetValue(context.Method, out var set)) { set.TryRemove(context.PatchId, out _); if (set.IsEmpty) contexts.TryRemove(context.Method, out _); } }
		public static void Prefix(MethodBase __originalMethod, object? __instance, object[] __args) => Invoke(__originalMethod, HookKind.Prefix, __instance, __args, null, null);
		public static void Postfix(MethodBase __originalMethod, object? __instance, object[] __args, object? __result) => Invoke(__originalMethod, HookKind.Postfix, __instance, __args, __result, null);
		public static Exception? Finalizer(MethodBase __originalMethod, object? __instance, object[] __args, Exception? __exception) { Invoke(__originalMethod, HookKind.Finalizer, __instance, __args, null, __exception); return __exception; }
		static void Invoke(MethodBase method, HookKind kind, object? instance, object[] args, object? result, Exception? exception) { if (contexts.TryGetValue(method, out var set)) foreach (var context in set.Values) if (context.Document.Kind == kind) context.Invoke(instance, args, result, exception); }
	}
}
