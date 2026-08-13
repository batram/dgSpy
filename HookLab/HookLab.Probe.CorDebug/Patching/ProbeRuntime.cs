using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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
		}

		internal static ProbeRuntime CreateAfterInventory(ProbeInitialization initialization, BackendInventoryResult inventory) => new ProbeRuntime(initialization, inventory);
		public string ProbeInstanceId { get; }
		public BackendInventoryResult Inventory { get; }
		public IHookEventSource Events => buffer;
		public long HooksVersion { get { lock (gate) return hooksVersion; } }
		public bool IsAutoDisabled(string patchId) { lock (gate) return hooks.TryGetValue(patchId, out var context) && context.Disabled; }
		public int? CompiledRevision(string patchId) { lock (gate) return compiledHooks.TryGetValue(patchId, out var context) ? context.Revision : (int?)null; }

		public CompiledPatchOperationResult InstallCompiledHook(MethodBase method, HookDocument document, string source, int revision, long expectedHooksVersion) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (document == null) throw new ArgumentNullException(nameof(document));
			if (document.Kind != HookKind.Prefix && document.Kind != HookKind.Postfix) throw new NotSupportedException("Compiled hooks currently support Prefix and Postfix only.");
			if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
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
				var candidate = new CompiledHookContext(method, patchId, document, source, revision, compiled.Methods);
				try {
					harmony.Patch(method,
						compiled.Prefix == null ? null : new HarmonyMethod(compiled.Prefix),
						compiled.Postfix == null ? null : new HarmonyMethod(compiled.Postfix));
				}
				catch { throw; }
				try {
					if (old != null) foreach (var patchMethod in old.PatchMethods) harmony.Unpatch(old.Method, patchMethod);
					compiledHooks[patchId] = candidate;
					hooksVersion++;
					return new CompiledPatchOperationResult(patchId, hooksVersion, revision, true);
				}
				catch {
					foreach (var patchMethod in compiled.Methods) harmony.Unpatch(method, patchMethod);
					throw;
				}
			}
		}

		public PatchOperationResult Install(MethodBase method, HookDocument document, long expectedHooksVersion) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (document == null) throw new ArgumentNullException(nameof(document));
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
				hooks[patchId] = context; hooksVersion++;
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

		public ProbeState GetState() {
			lock (gate) return new ProbeState(1, ProbeInstanceId, initialization.IdentityProvider.GetCurrentIdentity(), Inventory.SelectedIdentity, hooksVersion, hooks.Keys.Concat(compiledHooks.Keys).OrderBy(x => x).ToArray());
		}

		void ApplyPatch(MethodBase method, HookKind kind) {
			var type = typeof(HookDispatch);
			HarmonyMethod? prefix = null, postfix = null, finalizer = null;
			if (kind == HookKind.Prefix) prefix = new HarmonyMethod(type.GetMethod(nameof(HookDispatch.Prefix), BindingFlags.Public | BindingFlags.Static));
			else if (kind == HookKind.Postfix) postfix = new HarmonyMethod(type.GetMethod(nameof(HookDispatch.Postfix), BindingFlags.Public | BindingFlags.Static));
			else if (kind == HookKind.Finalizer) finalizer = new HarmonyMethod(type.GetMethod(nameof(HookDispatch.Finalizer), BindingFlags.Public | BindingFlags.Static));
			else throw new NotSupportedException("Unsupported hook kind: " + kind);
			harmony.Patch(method, prefix, postfix, null, finalizer);
		}

		void RemoveCore(HookContext context) {
			HookDispatch.Unregister(context); hooks.Remove(context.PatchId);
			if (!hooks.Values.Any(value => value.Method == context.Method)) harmony.Unpatch(context.Method, HarmonyPatchType.All, harmony.Id);
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
			lock (gate) { if (disposed) return; foreach (var item in hooks.Values.ToArray()) RemoveCore(item); foreach (var item in compiledHooks.Values.ToArray()) { foreach (var patchMethod in item.PatchMethods) harmony.Unpatch(item.Method, patchMethod); compiledHooks.Remove(item.PatchId); } disposed = true; }
		}
	}

	internal sealed class CompiledHookContext {
		internal CompiledHookContext(MethodBase method, string patchId, HookDocument document, string source, int revision, MethodInfo[] patchMethods) { Method = method; PatchId = patchId; Document = document; Source = source; Revision = revision; PatchMethods = patchMethods; }
		internal MethodBase Method { get; }
		internal string PatchId { get; }
		internal HookDocument Document { get; }
		internal string Source { get; }
		internal int Revision { get; }
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
		internal HookDocument Document { get; } internal long InstalledVersion { get; } internal bool Disabled { get; private set; }
		internal void Invoke(object? instance, object[] args, object? result, Exception? exception) {
			if (Disabled) return;
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
