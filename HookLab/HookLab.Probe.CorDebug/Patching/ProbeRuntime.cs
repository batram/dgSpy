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

	public sealed class StaleHooksVersionException : InvalidOperationException {
		public StaleHooksVersionException(long expected, long actual) : base($"Stale hooks_version: expected {expected}, actual {actual}.") { }
	}

	public sealed class ProbeRuntime : IDisposable {
		readonly object gate = new object();
		readonly Harmony harmony;
		readonly Dictionary<string, HookContext> hooks = new Dictionary<string, HookContext>(StringComparer.Ordinal);
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

		public PatchOperationResult Install(MethodBase method, HookDocument document, long expectedHooksVersion) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (document == null) throw new ArgumentNullException(nameof(document));
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				MethodGuards.ValidateTarget(initialization.ExpectedTarget, initialization.IdentityProvider.GetCurrentIdentity());
				MethodGuards.ValidateMethod(method, document.Target);
				var patchId = StablePatchId(document.HookId);
				if (hooks.TryGetValue(patchId, out var old)) RemoveCore(old);
				var context = new HookContext(this, method, patchId, document, hooksVersion + 1);
				HookDispatch.Register(context);
				try { ApplyPatch(method, document.Kind); } catch { HookDispatch.Unregister(context); throw; }
				hooks[patchId] = context; hooksVersion++;
				return new PatchOperationResult(patchId, hooksVersion, true);
			}
		}

		public PatchOperationResult Uninstall(string patchId, long expectedHooksVersion) {
			lock (gate) {
				ThrowIfDisposed(); CheckVersion(expectedHooksVersion);
				if (!hooks.TryGetValue(patchId, out var context)) return new PatchOperationResult(patchId, hooksVersion, false);
				RemoveCore(context); hooksVersion++; return new PatchOperationResult(patchId, hooksVersion, true);
			}
		}

		public ProbeState GetState() {
			lock (gate) return new ProbeState(1, ProbeInstanceId, initialization.IdentityProvider.GetCurrentIdentity(), Inventory.SelectedIdentity, hooksVersion, hooks.Keys.OrderBy(x => x).ToArray());
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
			harmony.Unpatch(context.Method, HarmonyPatchType.All, harmony.Id);
			HookDispatch.Unregister(context); hooks.Remove(context.PatchId);
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
			var consecutiveFailures = 0;
			while (true) {
				try { consumer.EventsAvailable(buffer); consecutiveFailures = 0; }
				catch (Exception ex) { consecutiveFailures++; RecordConsumerFailure(ex); }
				Volatile.Write(ref notificationPending, 0);
				if (!buffer.HasPendingEvents) return;
				// A consumer that throws every time would otherwise spin here against a buffer it never
				// drains. Stop re-arming and let the next append schedule a fresh attempt.
				if (consecutiveFailures >= ConsumerFailureLimit) return;
				if (Interlocked.Exchange(ref notificationPending, 1) != 0) return;
			}
		}

		const int ConsumerFailureLimit = 8;
		long consumerDispatchFailures;
		string? lastConsumerDispatchError;

		/// <summary>Consumer dispatch failures are counted and their last message kept rather than
		/// swallowed: a consumer that throws on every delivery otherwise looks exactly like one that
		/// works, and nothing can act on a failure it cannot see.</summary>
		public long ConsumerDispatchFailures { get { lock (gate) return consumerDispatchFailures; } }
		public string? LastConsumerDispatchError { get { lock (gate) return lastConsumerDispatchError; } }
		void RecordConsumerFailure(Exception ex) {
			lock (gate) { consumerDispatchFailures++; lastConsumerDispatchError = ex.GetType().FullName + ": " + ex.Message; }
		}
		internal BoundedEventBuffer Buffer => buffer;

		public void Dispose() {
			lock (gate) { if (disposed) return; foreach (var item in hooks.Values.ToArray()) RemoveCore(item); disposed = true; }
		}
	}

	internal sealed class HookContext {
		long sequence; int consecutiveFailures; int active; long rateSecond; int rateCount;
		internal HookContext(ProbeRuntime runtime, MethodBase method, string patchId, HookDocument document, long installedVersion) {
			Runtime = runtime; Method = method; PatchId = patchId; Document = document; InstalledVersion = installedVersion;
		}
		internal ProbeRuntime Runtime { get; } internal MethodBase Method { get; } internal string PatchId { get; }
		internal HookDocument Document { get; } internal long InstalledVersion { get; } internal bool Disabled { get; private set; }
		internal void Invoke(object? instance, object[] args, Exception? exception) {
			if (Disabled || Interlocked.Exchange(ref active, 1) != 0) return; // bounded reentrancy: one level
			try {
				var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
				if (Interlocked.Read(ref rateSecond) != second) { Interlocked.Exchange(ref rateSecond, second); Interlocked.Exchange(ref rateCount, 0); }
				if (Interlocked.Increment(ref rateCount) > Document.Limits.MaximumEventsPerSecond) return;
				if (Document.BehaviorJson.IndexOf("\"throw\":true", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Injected hook behavior failure.");
				var capture = BoundedCapture.Serialize(new CaptureEnvelope(instance, args, exception), Document.Limits);
				var sequenceValue = Interlocked.Increment(ref sequence);
				Runtime.Buffer.TryAppend(dropped => new HookEvent(Runtime.ProbeInstanceId, PatchId, InstalledVersion, sequenceValue,
					DateTime.UtcNow, Thread.CurrentThread.ManagedThreadId, capture.Json, capture.Truncated, dropped));
				Interlocked.Exchange(ref consecutiveFailures, 0); Runtime.Notify();
			} catch { if (Interlocked.Increment(ref consecutiveFailures) >= Document.Limits.MaximumConsecutiveFailures) Disabled = true; }
			finally { Volatile.Write(ref active, 0); }
		}
		sealed class CaptureEnvelope {
			internal CaptureEnvelope(object? instance, object[] arguments, Exception? exception) { Instance = instance; Arguments = arguments; Exception = exception; }
			public readonly object? Instance; public readonly object[] Arguments; public readonly Exception? Exception;
		}
	}

	public static class HookDispatch {
		static readonly ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, HookContext>> contexts = new ConcurrentDictionary<MethodBase, ConcurrentDictionary<string, HookContext>>();
		internal static void Register(HookContext context) => contexts.GetOrAdd(context.Method, _ => new ConcurrentDictionary<string, HookContext>())[context.PatchId] = context;
		internal static void Unregister(HookContext context) { if (contexts.TryGetValue(context.Method, out var set)) { set.TryRemove(context.PatchId, out _); if (set.IsEmpty) contexts.TryRemove(context.Method, out _); } }
		public static void Prefix(MethodBase __originalMethod, object? __instance, object[] __args) => Invoke(__originalMethod, __instance, __args, null);
		public static void Postfix(MethodBase __originalMethod, object? __instance, object[] __args) => Invoke(__originalMethod, __instance, __args, null);
		public static Exception? Finalizer(MethodBase __originalMethod, object? __instance, object[] __args, Exception? __exception) { Invoke(__originalMethod, __instance, __args, __exception); return __exception; }
		static void Invoke(MethodBase method, object? instance, object[] args, Exception? exception) { if (contexts.TryGetValue(method, out var set)) foreach (var context in set.Values) context.Invoke(instance, args, exception); }
	}
}
