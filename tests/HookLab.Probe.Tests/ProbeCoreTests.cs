using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HookLab.Contracts;
using HookLab.Probe.CorDebug;
using HookLab.Probe.CorDebug.Patching;
using Xunit;

namespace HookLab.Probe.Tests {
	public sealed class ProbeCoreTests {
		static readonly MethodInfo TargetMethod = typeof(Fixture).GetMethod(nameof(Fixture.Add))!;
		static readonly HookLimits Limits = new HookLimits(1000, 4096, 4, 8, 32, 3);

		[Fact]
		public void EveryMethodGuardRejectsIndependently() {
			var valid = GuardFor(TargetMethod);
			AssertGuard("module_mvid", new MethodGuard(Guid.NewGuid(), valid.MetadataToken, valid.DeclaringType, valid.MethodSignature, valid.IlSha256));
			AssertGuard("metadata_token", new MethodGuard(valid.ModuleMvid, valid.MetadataToken + 1, valid.DeclaringType, valid.MethodSignature, valid.IlSha256));
			AssertGuard("declaring_type", new MethodGuard(valid.ModuleMvid, valid.MetadataToken, "Wrong.Type", valid.MethodSignature, valid.IlSha256));
			AssertGuard("method_signature", new MethodGuard(valid.ModuleMvid, valid.MetadataToken, valid.DeclaringType, "System.Int32 Add(System.String)", valid.IlSha256));
			AssertGuard("il_sha256", new MethodGuard(valid.ModuleMvid, valid.MetadataToken, valid.DeclaringType, valid.MethodSignature, new string('0', 64)));
			MethodGuards.ValidateMethod(TargetMethod, valid);
		}

		[Fact]
		public void EveryTargetIdentityGuardRejectsIndependently() {
			var now = new DateTime(638900000000000000, DateTimeKind.Utc);
			var valid = new TargetIdentity("host", "c:\\fixture.exe", 42, now, "x64", "v4.0.30319", "1");
			var variants = new[] {
				new TargetIdentity("other", valid.ImagePath, valid.ProcessId, now, valid.Architecture, valid.RuntimeId, valid.AppDomainId),
				new TargetIdentity(valid.HostId, "c:\\other.exe", valid.ProcessId, now, valid.Architecture, valid.RuntimeId, valid.AppDomainId),
				new TargetIdentity(valid.HostId, valid.ImagePath, 43, now, valid.Architecture, valid.RuntimeId, valid.AppDomainId),
				new TargetIdentity(valid.HostId, valid.ImagePath, valid.ProcessId, now.AddTicks(1), valid.Architecture, valid.RuntimeId, valid.AppDomainId),
				new TargetIdentity(valid.HostId, valid.ImagePath, valid.ProcessId, now, "x86", valid.RuntimeId, valid.AppDomainId),
				new TargetIdentity(valid.HostId, valid.ImagePath, valid.ProcessId, now, valid.Architecture, "mono", valid.AppDomainId),
				new TargetIdentity(valid.HostId, valid.ImagePath, valid.ProcessId, now, valid.Architecture, valid.RuntimeId, "2") };
			foreach (var variant in variants) Assert.Throws<GuardMismatchException>(() => MethodGuards.ValidateTarget(valid, variant));
			MethodGuards.ValidateTarget(valid, valid);
		}

		[Fact]
		public void InventoryFailsClosedBeforeBackendAssemblyResolution() {
			var before = Loaded("0Harmony");
			var incompatible = new AssemblyName("HarmonyX") { Version = new Version(2, 10, 0, 0) };
			var error = Assert.Throws<BackendCompatibilityException>(() => BackendInventory.Inspect(new[] { incompatible }));
			Assert.Contains(incompatible.FullName, error.Message);
			Assert.Equal(before, Loaded("0Harmony"));
			var exact = BackendInventory.Inspect(new[] { new AssemblyName("0Harmony") { Version = BackendInventory.PinnedVersion } });
			Assert.True(exact.UseResident);
			var order = new List<string>();
			var value = BackendInventory.SelectBeforeResolve(
				() => { order.Add("inventory"); return BackendInventory.Inspect(Array.Empty<AssemblyName>()); },
				_ => { order.Add("resolve"); return 42; });
			Assert.Equal(42, value);
			Assert.Equal(new[] { "inventory", "resolve" }, order);
		}

		[Theory]
		[InlineData(HookKind.Prefix)]
		[InlineData(HookKind.Postfix)]
		[InlineData(HookKind.Finalizer)]
		public void SupportedPatchKindsCaptureAndPreserveBehavior(HookKind kind) {
			var method = kind == HookKind.Finalizer ? typeof(Fixture).GetMethod(nameof(Fixture.Throwing))! : TargetMethod;
			using (var runtime = Runtime()) {
				var document = new HookDocument(1, kind.ToString(), kind, GuardFor(method), "{}", Limits, true);
				runtime.Install(method, document, 0);
				if (kind == HookKind.Finalizer) Assert.Throws<FixtureException>(() => Fixture.Throwing());
				else Assert.Equal(3, Fixture.Add(1, 2));
				Assert.Single(runtime.Events.Drain(10));
			}
		}

		[Fact]
		public void MultipleKindsShareOneMethodPatchAndUnpatchIndependently() {
			using (var runtime = Runtime()) {
				var installed = new List<PatchOperationResult>();
				foreach (var kind in new[] { HookKind.Prefix, HookKind.Postfix, HookKind.Finalizer })
					installed.Add(runtime.Install(TargetMethod, new HookDocument(1, kind.ToString(), kind, GuardFor(TargetMethod), "{}", Limits, true), runtime.HooksVersion));
				Assert.Equal(3, Fixture.Add(1, 2));
				Assert.Equal(3, runtime.Events.Drain(10).Count);
				runtime.Uninstall(installed[0].PatchId, runtime.HooksVersion);
				Assert.Equal(3, Fixture.Add(1, 2));
				Assert.Equal(2, runtime.Events.Drain(10).Count);
				runtime.Uninstall(installed[1].PatchId, runtime.HooksVersion);
				runtime.Uninstall(installed[2].PatchId, runtime.HooksVersion);
				Assert.Equal(3, Fixture.Add(1, 2));
				Assert.Empty(runtime.Events.Drain(10));
			}
		}

		[Fact]
		public void PatchLifecycleVersionsAndStaleRejectionAreAtomic() {
			using (var runtime = Runtime()) {
				Assert.Equal(5, Fixture.Add(2, 3));
				var installed = runtime.Install(TargetMethod, Document(), 0);
				Assert.Equal(1, installed.HooksVersion);
				Assert.Equal(5, Fixture.Add(2, 3));
				Assert.Single(runtime.Events.Drain(10));
				Assert.Throws<StaleHooksVersionException>(() => runtime.Uninstall(installed.PatchId, 0));
				Assert.Equal(1, runtime.HooksVersion);
				var removed = runtime.Uninstall(installed.PatchId, 1);
				Assert.Equal(2, removed.HooksVersion);
				Assert.Equal(5, Fixture.Add(2, 3));
				Assert.Empty(runtime.Events.Drain(10));
			}
		}

		[Fact]
		public void CompiledPrefixCanReplaceTheResultAndSkipTheOriginal() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(41), 1, 0);
				Assert.Equal(41, Fixture.Add(1, 2));
				Assert.Equal(1, installed.Revision);
				Assert.Equal(1, runtime.CompiledRevision(installed.PatchId));
			}
		}

		[Fact]
		public void FailedCompiledUpdateLeavesThePreviousRevisionActive() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(41), 1, 0);
				var error = Assert.Throws<HookCompilationException>(() => runtime.InstallCompiledHook(TargetMethod, Document(), "this is not C#", 2, 1));
				Assert.NotEmpty(error.Diagnostics);
				Assert.Equal(1, runtime.HooksVersion);
				Assert.Equal(1, runtime.CompiledRevision(installed.PatchId));
				Assert.Equal(41, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void SuccessfulCompiledUpdateActivatesTheNewRevision() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(41), 1, 0);
				var updated = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(73), 2, 1);
				Assert.Equal(installed.PatchId, updated.PatchId);
				Assert.Equal(2, updated.Revision);
				Assert.Equal(73, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void CompiledPostfixRunsAfterTheOriginalWithoutChangingItsResult() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(HookKind.Postfix), "public static class UserHook { public static void Postfix() { } }", 1, 0);
				Assert.Equal(3, Fixture.Add(1, 2));
				Assert.Equal(1, runtime.CompiledRevision(installed.PatchId));
			}
		}

		[Fact]
		public void CompiledPostfixCanReplaceTheOriginalResult() {
			using (var runtime = Runtime()) {
				runtime.InstallCompiledHook(TargetMethod, Document(HookKind.Postfix), PostfixReturning(41), 1, 0);
				Assert.Equal(41, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void CompiledPrefixCanMutateANamedOriginalArgument() {
			using (var runtime = Runtime()) {
				runtime.InstallCompiledHook(TargetMethod, Document(), "public static class UserHook { public static void Prefix(ref int left) { left += 10; } }", 1, 0);
				Assert.Equal(13, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void CompiledPostfixCanReadANamedOriginalArgument() {
			using (var runtime = Runtime()) {
				runtime.InstallCompiledHook(TargetMethod, Document(HookKind.Postfix), "public static class UserHook { public static void Postfix(int left, ref int __result) { __result += left; } }", 1, 0);
				Assert.Equal(4, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void RingOverflowCarriesProvenDropAccounting() {
			var buffer = new BoundedEventBuffer(2, 1000);
			for (var index = 0; index < 5; index++) buffer.TryAppend(dropped => Event(index, dropped));
			Assert.Equal(3, buffer.DroppedCount);
			var first = buffer.Drain(10);
			Assert.Equal(new long[] { 0, 0 }, first.Select(x => x.DroppedCount));
			buffer.TryAppend(dropped => Event(6, dropped));
			Assert.Equal(3, buffer.Drain(10).Single().DroppedCount);
		}

		[Fact]
		public void CaptureEnforcesBoundsWithoutGettersOrToString() {
			var hostile = new Hostile { Visible = new string('x', 100), Items = Enumerable.Range(0, 20).ToArray() };
			var limits = new HookLimits(10, 4096, 3, 2, 5, 2);
			var capture = BoundedCapture.Serialize(hostile, limits);
			Assert.True(capture.Truncated);
			Assert.Contains("xxxxx", capture.Json);
			Assert.DoesNotContain(new string('x', 6), capture.Json);
			Assert.Equal(0, hostile.GetterCalls);
			Assert.Equal(0, hostile.ToStringCalls);
		}

		[Fact]
		public void CallbackFailuresFailOpenAndAutoDisable() {
			using (var runtime = Runtime()) {
				var installed = runtime.Install(TargetMethod, Document("{\"throw\":true}"), 0);
				for (var count = 0; count < Limits.MaximumConsecutiveFailures; count++) Assert.Equal(3, Fixture.Add(1, 2));
				Assert.True(runtime.IsAutoDisabled(installed.PatchId));
				Assert.Empty(runtime.Events.Drain(10));
			}
		}

		[Fact]
		public void HookCallbackDoesNotBlockOnConsumerDelivery() {
			var consumer = new BlockingConsumer();
			using (var runtime = Runtime(consumer)) {
				runtime.Install(TargetMethod, Document(), 0);
				try {
					var invocation = Task.Run(() => Fixture.Add(1, 2));
					Assert.True(invocation.Wait(TimeSpan.FromSeconds(2)));
					Assert.Equal(3, invocation.Result);
					Assert.True(consumer.Started.WaitOne(TimeSpan.FromSeconds(2)));
				} finally { consumer.Release.Set(); }
			}
		}

		[Fact]
		public void RateAndByteBoundsProduceExplicitDropsAndTruncation() {
			var tiny = new HookLimits(1, 16, 2, 1, 2, 2);
			var capture = BoundedCapture.Serialize(new string('a', 50), tiny);
			Assert.True(capture.Truncated);
			Assert.True(System.Text.Encoding.UTF8.GetByteCount(capture.Json) <= tiny.MaximumEventBytes);
			var buffer = new BoundedEventBuffer(10, 5);
			Assert.False(buffer.TryAppend(dropped => Event(1, dropped, "123456")));
			Assert.Equal(1, buffer.DroppedCount);
		}

		[Fact]
		public void ConcurrentInvocationsAreCapturedIndependently() {
			var method = typeof(Fixture).GetMethod(nameof(Fixture.Echo))!;
			var blocking = new BlockingEnumerable();
			using (var runtime = Runtime()) {
				runtime.Install(method, new HookDocument(1, "echo", HookKind.Prefix, GuardFor(method), "{}", Limits, true), 0);
				var first = Task.Run(() => Fixture.Echo(blocking));
				Assert.True(blocking.Entered.WaitOne(TimeSpan.FromSeconds(5)));
				try { Assert.Same(Array.Empty<int>(), Fixture.Echo(Array.Empty<int>())); }
				finally { blocking.Release.Set(); }
				Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
				Assert.Equal(2, runtime.Events.Drain(10).Count);
				Assert.Equal(0, runtime.ReentrantEventsSuppressed);
			}
		}

		[Fact]
		public void SameThreadRecursiveHookIsSuppressedAndCounted() {
			var echo = typeof(Fixture).GetMethod(nameof(Fixture.Echo))!;
			using (var runtime = Runtime()) {
				runtime.Install(TargetMethod, Document(), 0);
				runtime.Install(echo, new HookDocument(1, "echo", HookKind.Prefix, GuardFor(echo), "{}", Limits, true), 1);
				Fixture.Echo(new ReentrantEnumerable());
				Assert.Single(runtime.Events.Drain(10));
				Assert.Equal(1, runtime.ReentrantEventsSuppressed);
			}
		}

		[Fact]
		public void RateLimitedInvocationsAreCounted() {
			var limited = new HookLimits(1, 4096, 4, 8, 32, 3);
			using (var runtime = Runtime()) {
				runtime.Install(TargetMethod, new HookDocument(1, "limited", HookKind.Prefix, GuardFor(TargetMethod), "{}", limited, true), 0);
				for (var index = 0; index < 10; index++) Fixture.Add(index, 1);
				Assert.True(runtime.RateLimitedEventsDropped > 0);
			}
		}

		[Fact]
		public void InliningIsReportedAsRiskWithCallerValidationHooks() {
			var caller = typeof(Fixture).GetMethod(nameof(Fixture.Caller))!;
			var assessment = InliningInspector.Assess(TargetMethod, new[] { caller });
			Assert.Contains("validation", assessment.Reason, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(caller, assessment.ValidationCallers.Single());
		}

		// The wake-up used to be lost when an event landed between the consumer's final drain and the
		// pending-flag reset: the append saw the flag still set and suppressed its own notification.
		// This forces exactly that window by appending from inside the consumer callback.
		[Fact]
		public void EventAppendedDuringDeliveryStillWakesTheConsumer() {
			var consumer = new InterleavingConsumer();
			using (var runtime = Runtime(consumer)) {
				consumer.Runtime = runtime;
				runtime.Install(TargetMethod, Document(), 0);
				Fixture.Add(1, 2);
				Assert.True(consumer.SecondDelivery.WaitOne(TimeSpan.FromSeconds(5)),
					"An event appended during delivery never woke the consumer again.");
				Assert.Equal(2, consumer.Delivered);
			}
		}

		// A consumer that returns without draining is normal - the pipe worker does exactly that while
		// disconnected. An earlier delivery loop re-armed on any non-empty buffer and called such a
		// consumer 232 million times from one hook invocation, pinning a core inside the target.
		[Fact]
		public void NonDrainingConsumerIsNotCalledInALoop() {
			var consumer = new NonDrainingConsumer();
			using (var runtime = Runtime(consumer)) {
				runtime.Install(TargetMethod, Document(), 0);
				Fixture.Add(1, 2);
				Assert.True(consumer.Called.WaitOne(TimeSpan.FromSeconds(5)));
				Thread.Sleep(250);
				Assert.InRange(Volatile.Read(ref consumer.Calls), 1, 2);
				Assert.True(runtime.Events.Drain(10).Count > 0, "The undrained event must still be waiting.");
			}
		}

		[Fact]
		public void ConsumerFailuresAreRecordedRatherThanSwallowed() {
			var consumer = new ThrowingConsumer();
			using (var runtime = Runtime(consumer)) {
				runtime.Install(TargetMethod, Document(), 0);
				Fixture.Add(1, 2);
				Assert.True(consumer.Called.WaitOne(TimeSpan.FromSeconds(5)));
				SpinWait.SpinUntil(() => runtime.ConsumerDispatchFailures > 0, TimeSpan.FromSeconds(5));
				Assert.True(runtime.ConsumerDispatchFailures > 0);
				Assert.Contains("consumer exploded", runtime.LastConsumerDispatchError ?? "", StringComparison.Ordinal);
			}
		}

		static ProbeRuntime Runtime(IHookEventConsumer? consumer = null) {
			var identity = Identity();
			return ProbeInitializer.Initialize(new ProbeInitialization(identity, new IdentityProvider(identity), consumer, eventCapacity: 16, byteCapacity: 65536));
		}
		static TargetIdentity Identity() => new TargetIdentity("test", Assembly.GetExecutingAssembly().Location, System.Diagnostics.Process.GetCurrentProcess().Id,
			System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), "x64", Environment.Version.ToString(), AppDomain.CurrentDomain.Id.ToString());
		static HookDocument Document(string behavior = "{}") => Document(HookKind.Prefix, behavior);
		static HookDocument Document(HookKind kind, string behavior = "{}") => new HookDocument(1, "add", kind, GuardFor(TargetMethod), behavior, Limits, true);
		static string PrefixReturning(int value) => "public static class UserHook { public static bool Prefix(ref int __result) { __result = " + value + "; return false; } }";
		static string PostfixReturning(int value) => "public static class UserHook { public static void Postfix(ref int __result) { __result = " + value + "; } }";
		static MethodGuard GuardFor(MethodInfo method) => new MethodGuard(method.Module.ModuleVersionId, unchecked((uint)method.MetadataToken), method.DeclaringType!.FullName!, MethodGuards.Signature(method), MethodGuards.IlSha256(method));
		static void AssertGuard(string name, MethodGuard guard) { var error = Assert.Throws<GuardMismatchException>(() => MethodGuards.ValidateMethod(TargetMethod, guard)); Assert.Equal(name, error.GuardName); }
		static bool Loaded(string name) => AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal));
		static HookEvent Event(long sequence, long dropped, string payload = "{}") => new HookEvent("probe", "patch", 1, sequence, DateTime.UtcNow, 1, payload, false, dropped);

		sealed class IdentityProvider : ITargetIdentityProvider { readonly TargetIdentity identity; public IdentityProvider(TargetIdentity identity) { this.identity = identity; } public TargetIdentity GetCurrentIdentity() => identity; }
		sealed class BlockingConsumer : IHookEventConsumer {
			public readonly ManualResetEvent Started = new ManualResetEvent(false); public readonly ManualResetEvent Release = new ManualResetEvent(false);
			public void EventsAvailable(IHookEventSource source) { Started.Set(); Release.WaitOne(); }
		}
		sealed class InterleavingConsumer : IHookEventConsumer {
			public readonly ManualResetEvent SecondDelivery = new ManualResetEvent(false);
			public ProbeRuntime? Runtime;
			public int Delivered;
			public void EventsAvailable(IHookEventSource source) {
				var round = Interlocked.Increment(ref Delivered);
				source.Drain(16);
				// Round 1 appends while this delivery is still in flight, which is the window that used
				// to swallow the notification. Round 2 must therefore happen without any further hook
				// activity, or the wake-up was lost.
				if (round == 1) Fixture.Add(3, 4);
				else SecondDelivery.Set();
			}
		}
		sealed class NonDrainingConsumer : IHookEventConsumer {
			public readonly ManualResetEvent Called = new ManualResetEvent(false);
			public int Calls;
			public void EventsAvailable(IHookEventSource source) { Interlocked.Increment(ref Calls); Called.Set(); }
		}
		sealed class ThrowingConsumer : IHookEventConsumer {
			public readonly ManualResetEvent Called = new ManualResetEvent(false);
			public void EventsAvailable(IHookEventSource source) { Called.Set(); throw new InvalidOperationException("consumer exploded"); }
		}
		sealed class Hostile {
			public string Visible = ""; public int[] Items = Array.Empty<int>(); public int GetterCalls; public int ToStringCalls;
			public string Dangerous { get { GetterCalls++; throw new InvalidOperationException(); } }
			public override string ToString() { ToStringCalls++; throw new InvalidOperationException(); }
		}
		sealed class BlockingEnumerable : IEnumerable {
			public readonly ManualResetEvent Entered = new ManualResetEvent(false);
			public readonly ManualResetEvent Release = new ManualResetEvent(false);
			public IEnumerator GetEnumerator() { Entered.Set(); Release.WaitOne(); return Array.Empty<object>().GetEnumerator(); }
		}
		sealed class ReentrantEnumerable : IEnumerable {
			public IEnumerator GetEnumerator() { Fixture.Add(10, 20); return Array.Empty<object>().GetEnumerator(); }
		}
		static class Fixture {
			[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
			[MethodImpl(MethodImplOptions.NoInlining)] public static object Echo(object value) => value;
			[MethodImpl(MethodImplOptions.NoInlining)] public static int Caller() => Add(1, 2);
			[MethodImpl(MethodImplOptions.NoInlining)] public static void Throwing() { throw new FixtureException(); }
		}
		sealed class FixtureException : Exception { }
	}
}
