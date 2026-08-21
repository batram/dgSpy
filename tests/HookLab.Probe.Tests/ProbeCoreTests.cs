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
using HookLab.Probe.CorDebug.Transport;
using Xunit;
using dgSpy.Extension;

namespace HookLab.Probe.Tests {
	public sealed class ProbeCoreTests {
		static readonly MethodInfo TargetMethod = typeof(Fixture).GetMethod(nameof(Fixture.Add))!;
		/// <summary>A top-level target, so an emitted assembly can genuinely redefine its type by name.</summary>
		static readonly MethodInfo ShadowableMethod = typeof(ShadowableFixture).GetMethod(nameof(ShadowableFixture.Add))!;
		static readonly MethodInfo PreexistingShadowMethod = typeof(PreexistingShadowFixture).GetMethod(nameof(PreexistingShadowFixture.Add))!;
		static readonly MethodInfo UnrelatedLoadMethod = typeof(UnrelatedLoadFixture).GetMethod(nameof(UnrelatedLoadFixture.Add))!;
		static readonly MethodInfo RemovedShadowMethod = typeof(RemovedShadowFixture).GetMethod(nameof(RemovedShadowFixture.Add))!;
		static readonly MethodInfo IdenticalCopyMethod = typeof(IdenticalCopyFixture).GetMethod(nameof(IdenticalCopyFixture.Add))!;
		static readonly MethodInfo InstanceTargetMethod = typeof(InstanceFixture).GetMethod(nameof(InstanceFixture.Calculate))!;
		static readonly MethodInfo RefOutTargetMethod = typeof(Fixture).GetMethod(nameof(Fixture.RefOut))!;
		static readonly MethodInfo PrivateTargetMethod = typeof(PrivateInstanceFixture).GetMethod(nameof(PrivateInstanceFixture.Calculate))!;
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
		public void ObservationalHookCanBeDisabledAndEnabledWithoutLosingItsRecord() {
			using (var runtime = Runtime()) {
				var installed = runtime.Install(TargetMethod, Document(), 0);
				Fixture.Add(2, 3); Assert.Single(runtime.Events.Drain(10));
				var disabled = runtime.SetEnabled(installed.PatchId, false, 1);
				Assert.True(disabled.Changed); Assert.False(runtime.IsEnabled(installed.PatchId));
				Fixture.Add(2, 3); Assert.Empty(runtime.Events.Drain(10));
				Assert.False(runtime.SetEnabled(installed.PatchId, false, 2).Changed);
				var enabled = runtime.SetEnabled(installed.PatchId, true, 2);
				Assert.True(enabled.Changed); Assert.True(runtime.IsEnabled(installed.PatchId));
				Fixture.Add(2, 3); Assert.Single(runtime.Events.Drain(10));
			}
		}

		/// <summary>A generic carrier is refused by name, before anything is compiled or patched.
		///
		/// <para>It was already impossible - Harmony answers a generic method with
		/// <c>NotSupportedException: Specified method is not supported.</c>, measured live on Mono 6.12 -
		/// but that refusal names neither the method nor the reason, and reads like a HookLab defect rather
		/// than a property of the shape. <c>get_hook_template</c> refuses the same shape, but a template is
		/// an authoring convenience that an exported package or a hand-written request never asks for.</para></summary>
		[Fact]
		public void AGenericCarrierIsRefusedByNameRatherThanByHarmony() {
			var genericMethod = typeof(Fixture).GetMethod(nameof(Fixture.Generic))!;
			var methodOnGenericType = typeof(Fixture.Holder<>).GetMethod(nameof(Fixture.Holder<int>.Work))!;
			foreach (var carrier in new[] { genericMethod, methodOnGenericType }) {
				using (var runtime = Runtime()) {
					var compiled = Assert.Throws<NotSupportedException>(() =>
						runtime.InstallCompiledHook(carrier, Document(), PrefixReturning(41), 1, 0));
					Assert.Contains("generic carrier", compiled.Message, StringComparison.Ordinal);
					Assert.Contains(carrier.Name, compiled.Message, StringComparison.Ordinal);
					var observed = Assert.Throws<NotSupportedException>(() =>
						runtime.Install(carrier, Document(), 0));
					Assert.Contains("generic carrier", observed.Message, StringComparison.Ordinal);
				}
			}
		}

		/// <summary>The refusal is about unbound generic parameters, not about the word "generic": a closed
		/// instantiation is a real runtime method and must still be patchable. Without this the fix above
		/// could be a blanket ban that nothing noticed.</summary>
		[Fact]
		public void AClosedGenericInstantiationIsStillPatchable() {
			var closed = typeof(Fixture.Holder<int>).GetMethod(nameof(Fixture.Holder<int>.Work))!;
			Assert.False(closed.ContainsGenericParameters);
			using (var runtime = Runtime()) {
				var document = new HookDocument(1, "closed", HookKind.Postfix, GuardFor(closed), "{}", Limits, true);
				var installed = runtime.InstallCompiledHook(closed, document, PostfixReturning(7), 1, 0);
				Assert.Equal(1, installed.Revision);
				Assert.Equal(7, Fixture.Holder<int>.Work());
			}
		}

		/// <summary>A runtime whose class libraries cannot open a named pipe is refused as unsupported,
		/// rather than surfacing a bare DllNotFoundException for a library nobody asked for.
		///
		/// <para>Unit-level on purpose. The measured example is Unity 2021.3's own <c>mono.exe</c>, whose
		/// CoreFX-derived <c>System.IO.Pipes</c> P/Invokes a <c>System.Native</c> shim absent on Windows -
		/// and that build is x86-only, so the resident's architecture guard refuses it before the endpoint
		/// is ever reached. There is no x64 combination of that runtime to drive live, so the classification
		/// is asserted where it is decided.</para></summary>
		[Fact]
		public void ARuntimeThatCannotOpenANamedPipeIsRefusedAsUnsupported() {
			var cause = new DllNotFoundException("Unable to load DLL 'System.Native'");
			var refusal = ProbePipeServer.DescribeUnsupportedRuntime(cause);
			Assert.IsType<PlatformNotSupportedException>(refusal);
			Assert.Contains("cannot open a named pipe server", refusal.Message, StringComparison.Ordinal);
			Assert.Contains("System.Native", refusal.Message, StringComparison.Ordinal);
			// The cause is kept, not swallowed: an operator on some other runtime needs the original name.
			Assert.Same(cause, refusal.InnerException);
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
		public void CompiledHookCanBeDisabledAndEnabledWithoutRecompilationOrRevisionLoss() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(41), 1, 0);
				Assert.Equal(41, Fixture.Add(1, 2));
				runtime.SetEnabled(installed.PatchId, false, 1);
				Assert.Equal(3, Fixture.Add(1, 2)); Assert.Equal(1, runtime.CompiledRevision(installed.PatchId)); Assert.False(runtime.IsEnabled(installed.PatchId));
				runtime.SetEnabled(installed.PatchId, true, 2);
				Assert.Equal(41, Fixture.Add(1, 2)); Assert.Equal(1, runtime.CompiledRevision(installed.PatchId)); Assert.True(runtime.IsEnabled(installed.PatchId));
			}
		}

		[Fact]
		public void UpdatingADisabledCompiledHookKeepsTheNewRevisionDisabled() {
			using (var runtime = Runtime()) {
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(41), 1, 0);
				runtime.SetEnabled(installed.PatchId, false, 1);
				var updated = runtime.InstallCompiledHook(TargetMethod, Document(), PrefixReturning(73), 2, 2);
				Assert.False(runtime.IsEnabled(updated.PatchId)); Assert.Equal(2, runtime.CompiledRevision(updated.PatchId)); Assert.Equal(3, Fixture.Add(1, 2));
				runtime.SetEnabled(updated.PatchId, true, 3);
				Assert.Equal(73, Fixture.Add(1, 2));
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
		public void CompiledFinalizerCanSuppressAndThenReplaceAnException() {
			var method=typeof(Fixture).GetMethod(nameof(Fixture.Throwing))!;
			var document=new HookDocument(1,"compiled-finalizer",HookKind.Finalizer,GuardFor(method),"{}",Limits,true);
			using(var runtime=Runtime()) {
				var installed=runtime.InstallCompiledHook(method,document,"public static class UserHook { public static System.Exception Finalizer(System.Exception __exception) { return null; } }",1,0);
				Fixture.Throwing();
				var updated=runtime.InstallCompiledHook(method,document,"public static class UserHook { public static System.Exception Finalizer(System.Exception __exception) { return new System.InvalidOperationException(\"replaced\", __exception); } }",2,1);
				var error=Assert.Throws<InvalidOperationException>(()=>Fixture.Throwing()); Assert.Equal("replaced",error.Message); Assert.IsType<FixtureException>(error.InnerException);
				Assert.Equal(installed.PatchId,updated.PatchId); Assert.Equal(2,updated.Revision);
			}
		}

		[Fact]
		public void CompiledFinalizerToggleRestoresAndSuppressesTheOriginalException() {
			var method=typeof(Fixture).GetMethod(nameof(Fixture.Throwing))!;
			var document=new HookDocument(1,"toggle-finalizer",HookKind.Finalizer,GuardFor(method),"{}",Limits,true);
			using(var runtime=Runtime()) {
				var installed=runtime.InstallCompiledHook(method,document,"public static class UserHook { public static System.Exception Finalizer(System.Exception __exception) { return null; } }",1,0);
				Fixture.Throwing(); runtime.SetEnabled(installed.PatchId,false,1); Assert.Throws<FixtureException>(()=>Fixture.Throwing()); runtime.SetEnabled(installed.PatchId,true,2); Fixture.Throwing();
			}
		}

		[Fact]
		public void CompiledTranspilerCanUpdateRollbackToggleAndRemove() {
			var document=new HookDocument(1,"compiled-transpiler",HookKind.Transpiler,GuardFor(TargetMethod),"{}",Limits,true);
			using(var runtime=Runtime()) {
				var installed=runtime.InstallCompiledHook(TargetMethod,document,TranspilerReturning(41),1,0);
				Assert.Equal(41,Fixture.Add(1,2));
				var error=Assert.Throws<HookCompilationException>(()=>runtime.InstallCompiledHook(TargetMethod,document,"this is not C#",2,1));
				Assert.NotEmpty(error.Diagnostics); Assert.Equal(1,runtime.CompiledRevision(installed.PatchId)); Assert.Equal(41,Fixture.Add(1,2));
				var updated=runtime.InstallCompiledHook(TargetMethod,document,TranspilerReturning(73),2,1);
				Assert.Equal(73,Fixture.Add(1,2)); Assert.Equal(2,runtime.CompiledRevision(updated.PatchId));
				runtime.SetEnabled(updated.PatchId,false,2); Assert.Equal(3,Fixture.Add(1,2));
				runtime.SetEnabled(updated.PatchId,true,3); Assert.Equal(73,Fixture.Add(1,2));
				runtime.Uninstall(updated.PatchId,4); Assert.Equal(3,Fixture.Add(1,2));
			}
		}

		[Fact]
		public void ObservationalPostfixCoexistsWithCompiledPostfix() {
			using (var runtime = Runtime()) {
				var compiled = new HookDocument(1, "compiled", HookKind.Postfix, GuardFor(TargetMethod), "{}", Limits, true);
				var observer = new HookDocument(1, "observer", HookKind.Postfix, GuardFor(TargetMethod), "{}", Limits, true);
				runtime.InstallCompiledHook(TargetMethod, compiled, PostfixReturning(41), 1, 0);
				var observed = runtime.Install(TargetMethod, observer, runtime.HooksVersion);
				Assert.Equal(41, Fixture.Add(1, 2));
				Assert.Single(runtime.Events.Drain(10));
				runtime.Uninstall(observed.PatchId, runtime.HooksVersion);
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
		public void CompiledPrefixReceivesTheTargetInstance() {
			using (var runtime = Runtime()) {
				var document = new HookDocument(1, "instance", HookKind.Prefix, GuardFor(InstanceTargetMethod), "{}", Limits, true);
				runtime.InstallCompiledHook(InstanceTargetMethod, document, "public static class UserHook { public static void Prefix(HookLab.Probe.Tests.ProbeCoreTests.InstanceFixture __instance) { __instance.Offset += 10; } }", 1, 0);
				Assert.Equal(16, new InstanceFixture(5).Calculate(1));
			}
		}

		[Fact]
		public void CompiledPostfixReceivesTheTargetInstance() {
			using (var runtime = Runtime()) {
				var document = new HookDocument(1, "instance", HookKind.Postfix, GuardFor(InstanceTargetMethod), "{}", Limits, true);
				runtime.InstallCompiledHook(InstanceTargetMethod, document, "public static class UserHook { public static void Postfix(HookLab.Probe.Tests.ProbeCoreTests.InstanceFixture __instance, ref int __result) { __result += __instance.Offset; } }", 1, 0);
				Assert.Equal(11, new InstanceFixture(5).Calculate(1));
			}
		}

		[Fact]
		public void CompiledPrefixAndPostfixShareHarmonyState() {
			using (var runtime = Runtime()) {
				var source = "public static class UserHook { " +
					"public static void Prefix(int left, out int __state) { __state = left * 10; } " +
					"public static void Postfix(int __state, ref int __result) { __result += __state; } }";
				var installed = runtime.InstallCompiledHook(TargetMethod, Document(), source, 1, 0);
				Assert.Equal(13, Fixture.Add(1, 2));
				runtime.Uninstall(installed.PatchId, 1);
				Assert.Equal(3, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void CompiledSourceRejectsDuplicatePatchPhases() {
			using (var runtime = Runtime()) {
				var source = "public static class First { public static void Prefix() { } } public static class Second { public static void Prefix() { } }";
				var error = Assert.Throws<HookCompilationException>(() => runtime.InstallCompiledHook(TargetMethod, Document(), source, 1, 0));
				Assert.Contains("at most one", error.Diagnostics.Single(), StringComparison.Ordinal);
			}
		}

		[Fact]
		public void CompiledPrefixCanMutateTheHarmonyArgumentArray() {
			using (var runtime = Runtime()) {
				runtime.InstallCompiledHook(TargetMethod, Document(), "public static class UserHook { public static void Prefix(object[] __args) { __args[0] = 10; } }", 1, 0);
				Assert.Equal(12, Fixture.Add(1, 2));
			}
		}

		[Fact]
		public void CompiledPrefixCanAccessAnInstanceFieldThroughHarmony() {
			using (var runtime = Runtime()) {
				var document = new HookDocument(1, "private-field", HookKind.Prefix, GuardFor(PrivateTargetMethod), "{}", Limits, true);
				runtime.InstallCompiledHook(PrivateTargetMethod, document, "public static class UserHook { public static void Prefix(ref int ___offset) { ___offset += 10; } }", 1, 0);
				Assert.Equal(16, new PrivateInstanceFixture(5).Calculate(1));
			}
		}

		[Theory]
		[InlineData("Prefix")]
		[InlineData("Postfix")]
		[InlineData("PrefixPostfix")]
		[InlineData("Finalizer")]
		[InlineData("Transpiler")]
		public void GeneratedHookTemplatesCompileAndPatch(string template) {
			var target = new HookTemplateTarget("HookLab.Probe.Tests.ProbeCoreTests.InstanceFixture",false,"System.Int32",new[] { new HookTemplateParameter("value","System.Int32","") });
			var source = HookSourceTemplate.Generate(target,template);
			if(template!="Transpiler") Assert.Contains("InstanceFixture __instance",source,StringComparison.Ordinal);
			if(template=="PrefixPostfix") { Assert.Contains("out object __state",source,StringComparison.Ordinal); Assert.Contains("object __state",source,StringComparison.Ordinal); }
			using(var runtime=Runtime()) {
				var kind=template=="Postfix"?HookKind.Postfix:template=="Finalizer"?HookKind.Finalizer:template=="Transpiler"?HookKind.Transpiler:HookKind.Prefix;
				var document=new HookDocument(1,"generated",kind,GuardFor(InstanceTargetMethod),"{}",Limits,true);
				runtime.InstallCompiledHook(InstanceTargetMethod,document,source,1,0);
				Assert.Equal(6,new InstanceFixture(5).Calculate(1));
			}
		}

		[Fact]
		public void GeneratedFinalizerTemplatePreservesExceptionsByDefault() {
			var target=new HookTemplateTarget("HookLab.Probe.Tests.ProbeCoreTests.Fixture",true,"System.Void",Array.Empty<HookTemplateParameter>());
			var source=HookSourceTemplate.Generate(target,"Finalizer");
			Assert.Contains("System.Exception Finalizer(System.Exception __exception)",source,StringComparison.Ordinal); Assert.Contains("return __exception;",source,StringComparison.Ordinal);
			var method=typeof(Fixture).GetMethod(nameof(Fixture.Throwing))!; var document=new HookDocument(1,"generated-finalizer",HookKind.Finalizer,GuardFor(method),"{}",Limits,true);
			using(var runtime=Runtime()) { runtime.InstallCompiledHook(method,document,source,1,0); Assert.Throws<FixtureException>(()=>Fixture.Throwing()); }
		}

		[Fact]
		public void GeneratedTemplatePreservesByRefAndArraySyntax() {
			var target=new HookTemplateTarget("Fixture",true,"System.Void",new[] {
				new HookTemplateParameter("left","System.Int32","ref"),
				new HookTemplateParameter("right","System.String[]","ref")
			});
			var source=HookSourceTemplate.Generate(target,"Prefix");
			Assert.Contains("ref System.Int32 @left",source,StringComparison.Ordinal);
			Assert.Contains("ref System.String[] @right",source,StringComparison.Ordinal);
			Assert.DoesNotContain("__result",source,StringComparison.Ordinal);
		}

		[Fact]
		public void GeneratedTemplateForRefAndOutTargetCompilesAndRuns() {
			var target=new HookTemplateTarget("HookLab.Probe.Tests.ProbeCoreTests.Fixture",true,"System.Void",new[] {
				new HookTemplateParameter("left","System.Int32","ref"),
				new HookTemplateParameter("right","System.String","ref")
			});
			var source=HookSourceTemplate.Generate(target,"Prefix");
			using(var runtime=Runtime()) {
				var document=new HookDocument(1,"generated-ref-out",HookKind.Prefix,GuardFor(RefOutTargetMethod),"{}",Limits,true);
				runtime.InstallCompiledHook(RefOutTargetMethod,document,source,1,0);
				var left=7;
				Fixture.RefOut(ref left,out var right);
				Assert.Equal(8,left);
				Assert.Equal("8",right);
			}
		}

		[Fact]
		public void GeneratedTemplateUsesValidNamesForKeywordsMissingAndInvalidMetadataNames() {
			var target=new HookTemplateTarget("Fixture",true,"System.Void",new[] {
				new HookTemplateParameter("class","System.Int32",""),
				new HookTemplateParameter("","System.Int32",""),
				new HookTemplateParameter("not valid","System.Int32","")
			});
			var source=HookSourceTemplate.Generate(target,"Prefix");
			Assert.Contains("System.Int32 @class",source,StringComparison.Ordinal);
			Assert.Contains("System.Int32 @__1",source,StringComparison.Ordinal);
			Assert.Contains("System.Int32 @__2",source,StringComparison.Ordinal);
		}

		[Fact]
		public void GeneratedTemplateRejectsUnknownSelection() {
			var target=new HookTemplateTarget("Fixture",true,"System.Void",Array.Empty<HookTemplateParameter>());
			Assert.Throws<ArgumentException>(()=>HookSourceTemplate.Generate(target,"Unknown"));
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

		/// <summary>A hook whose target assembly has been superseded keeps every guard valid and observes
		/// nothing, which is indistinguishable from a method that is never called. Measured twice on
		/// 2026-08-20 against two separate IIS workers: ASP.NET recompiled a page, loaded the new assembly
		/// beside the old one, and a correct hook on the old one went silent.
		///
		/// The fixture emits a real assembly declaring a type with the hooked type's full name, because
		/// the shadowing that matters is by name across modules - which is exactly what a recompiled page
		/// produces.</summary>
		[Fact]
		public void AShadowedHookIsReportedWhenTheRedefiningAssemblyLoadsAfterInstall() {
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-shadow-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(directory);
			try {
				using (var runtime = Runtime()) {
					runtime.Install(ShadowableMethod, ShadowableDocument(ShadowableMethod), 0);
					Assert.Empty(runtime.GetState().ShadowedHooks);

					// From disk, not a dynamic assembly: DefineDynamicAssembly raises AssemblyLoad before
					// any type is defined in it, so a probe checking types at load time correctly sees
					// nothing. A recompiled page arrives as a real file with its types already in it.
					LoadFromDisk(directory, ShadowableMethod.DeclaringType!.FullName!, "Shadowing.Generation2");

					var shadowed = Assert.Single(runtime.GetState().ShadowedHooks);
					Assert.Equal(ShadowableMethod.DeclaringType!.FullName, shadowed.DeclaringType);
					Assert.Contains("Shadowing.Generation2", shadowed.ShadowingAssembly, StringComparison.Ordinal);
					Assert.Contains(shadowed.PatchId, runtime.GetState().PatchIds);
				}
			}
			finally { try { System.IO.Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>The commoner case, and the one an event-only check misses completely: the superseding
		/// assembly was already resident when the hook went on. Measured on 2026-08-20 - the IIS worker
		/// held both generations of the recompiled page before anything was installed, so nothing loaded
		/// afterwards and there was no event to notice.</summary>
		[Fact]
		public void AHookInstalledIntoAnAlreadyShadowedDomainIsReportedImmediately() {
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-shadow-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(directory);
			try {
				LoadFromDisk(directory, PreexistingShadowMethod.DeclaringType!.FullName!, "Shadowing.Preexisting");
				using (var runtime = Runtime()) {
					runtime.Install(PreexistingShadowMethod, ShadowableDocument(PreexistingShadowMethod), 0);
					var shadowed = Assert.Single(runtime.GetState().ShadowedHooks);
					Assert.Contains("Shadowing.Preexisting", shadowed.ShadowingAssembly, StringComparison.Ordinal);
				}
			}
			finally { try { System.IO.Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>Byte-identical assemblies share a module version id and are still two modules, and
		/// Harmony patched exactly one of them - so an MVID comparison calls a genuinely dead hook healthy.
		/// The first implementation of this check compared MVIDs and would have.
		///
		/// <para>Reached by byte-loading, which is the route that actually produces this: <c>LoadFrom</c> on
		/// a copied file returns the assembly already loaded, because the LoadFrom context binds by
		/// assembly identity rather than by path, so two identical files cannot coexist that way.
		/// <c>Assembly.Load(byte[])</c> creates a second, context-free assembly with the same identity and
		/// the same MVID - and HookLab's own payloads arrive exactly like that.</para></summary>
		[Fact]
		public void AByteLoadedIdenticalCopyStillShadowsTheHook() {
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-shadow-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(directory);
			try {
				// The hook goes on the EMITTED assembly's method, not on a fixture in this one. An earlier
				// version of this test hooked the local fixture and asserted only NotEmpty - which passed
				// against the MVID implementation too, because the fixture and the emitted copy are
				// different modules anyway. It proved nothing about identical copies.
				var path = System.IO.Path.Combine(directory, "Shadowing.Copy1.dll");
				var first = EmitCallableAssembly(directory, "Shadowing.Copy1", "Shadowing.Work");
				var method = first.GetType("Shadowing.Work", true)!.GetMethod("Tick")!;
				var second = Assembly.Load(System.IO.File.ReadAllBytes(path));
				Assert.Equal(first.ManifestModule.ModuleVersionId, second.ManifestModule.ModuleVersionId);
				Assert.NotSame(first.ManifestModule, second.ManifestModule);

				using (var runtime = Runtime()) {
					runtime.Install(method, ShadowableDocument(method), 0);
					var shadowed = Assert.Single(runtime.GetState().ShadowedHooks);
					Assert.Equal("Shadowing.Work", shadowed.DeclaringType);
				}
			}
			finally { try { System.IO.Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>An assembly with one real, callable static method, saved and loaded from disk, so a hook
		/// can be installed on it and a second byte-identical load can compete with it.</summary>
		static Assembly EmitCallableAssembly(string directory, string assemblyName, string typeName) {
			var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), System.Reflection.Emit.AssemblyBuilderAccess.RunAndSave, directory);
			var type = builder.DefineDynamicModule(assemblyName, assemblyName + ".dll").DefineType(typeName, System.Reflection.TypeAttributes.Public);
			var method = type.DefineMethod("Tick", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(int), new[] { typeof(int) });
			var il = method.GetILGenerator();
			il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
			il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
			il.Emit(System.Reflection.Emit.OpCodes.Add);
			il.Emit(System.Reflection.Emit.OpCodes.Ret);
			type.CreateType();
			builder.Save(assemblyName + ".dll");
			return Assembly.LoadFrom(System.IO.Path.Combine(directory, assemblyName + ".dll"));
		}

		/// <summary>An assembly that redefines nothing the probe hooked is ordinary traffic - a busy
		/// process loads assemblies constantly - and reporting it would make the real signal worthless.</summary>
		[Fact]
		public void AnUnrelatedAssemblyLoadIsNotReportedAsShadowing() {
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-shadow-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(directory);
			try {
				using (var runtime = Runtime()) {
					runtime.Install(UnrelatedLoadMethod, ShadowableDocument(UnrelatedLoadMethod), 0);
					LoadFromDisk(directory, "Some.Other.Type", "Shadowing.Unrelated");
					Assert.Empty(runtime.GetState().ShadowedHooks);
				}
			}
			finally { try { System.IO.Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>Removing the hook removes the report with it: a resolved problem that stays on the
		/// screen trains people to ignore the screen.</summary>
		[Fact]
		public void RemovingAShadowedHookRemovesItsReport() {
			var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hooklab-shadow-" + Guid.NewGuid().ToString("N"));
			System.IO.Directory.CreateDirectory(directory);
			try {
				using (var runtime = Runtime()) {
					var installed = runtime.Install(RemovedShadowMethod, ShadowableDocument(RemovedShadowMethod), 0);
					LoadFromDisk(directory, RemovedShadowMethod.DeclaringType!.FullName!, "Shadowing.Generation3");
					Assert.NotEmpty(runtime.GetState().ShadowedHooks);
					runtime.Uninstall(installed.PatchId, runtime.HooksVersion);
					Assert.Empty(runtime.GetState().ShadowedHooks);
				}
			}
			finally { try { System.IO.Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>Emits an assembly declaring one type by full name, saves it, and loads it from disk so
		/// the probe sees a real assembly with its types already present - the shape a recompiled ASP.NET
		/// page arrives in.</summary>
		static Assembly LoadFromDisk(string directory, string fullTypeName, string assemblyName) {
			var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(assemblyName), System.Reflection.Emit.AssemblyBuilderAccess.RunAndSave, directory);
			builder.DefineDynamicModule(assemblyName, assemblyName + ".dll").DefineType(fullTypeName, System.Reflection.TypeAttributes.Public).CreateType();
			builder.Save(assemblyName + ".dll");
			return Assembly.LoadFrom(System.IO.Path.Combine(directory, assemblyName + ".dll"));
		}

		static ProbeRuntime Runtime(IHookEventConsumer? consumer = null) {
			var identity = Identity();
			return ProbeInitializer.Initialize(new ProbeInitialization(identity, new IdentityProvider(identity), consumer, eventCapacity: 16, byteCapacity: 65536));
		}
		static TargetIdentity Identity() => new TargetIdentity("test", Assembly.GetExecutingAssembly().Location, System.Diagnostics.Process.GetCurrentProcess().Id,
			System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), "x64", Environment.Version.ToString(), AppDomain.CurrentDomain.Id.ToString());
		static HookDocument Document(string behavior = "{}") => Document(HookKind.Prefix, behavior);
		static HookDocument Document(HookKind kind, string behavior = "{}") => new HookDocument(1, "add", kind, GuardFor(TargetMethod), behavior, Limits, true);
		/// <summary>Guards have to match the method being installed, so each shadowing test builds its own
		/// from its own fixture.</summary>
		static HookDocument ShadowableDocument(MethodInfo method) => new HookDocument(1, "shadow-" + method.DeclaringType!.Name, HookKind.Prefix, GuardFor(method), "{}", Limits, true);
		static string PrefixReturning(int value) => "public static class UserHook { public static bool Prefix(ref int __result) { __result = " + value + "; return false; } }";
		static string PostfixReturning(int value) => "public static class UserHook { public static void Postfix(ref int __result) { __result = " + value + "; } }";
		static string TranspilerReturning(int value) => "public static class UserHook { public static System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction> instructions) { return new[] { new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4, " + value + "), new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }; } }";
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
			/// <summary>A generic method definition and a method on an open generic type: both are shapes the
			/// runtime compiles one copy of per set of type arguments, so neither is a single method to
			/// patch. Present so the refusal can be asserted rather than described.</summary>
			[MethodImpl(MethodImplOptions.NoInlining)] public static int Generic<T>(T value) => 0;
			internal static class Holder<T> {
				[MethodImpl(MethodImplOptions.NoInlining)] public static int Work() => 0;
			}
			[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
			[MethodImpl(MethodImplOptions.NoInlining)] public static object Echo(object value) => value;
			[MethodImpl(MethodImplOptions.NoInlining)] public static int Caller() => Add(1, 2);
			[MethodImpl(MethodImplOptions.NoInlining)] public static void Throwing() { throw new FixtureException(); }
			[MethodImpl(MethodImplOptions.NoInlining)] public static void RefOut(ref int left,out string right) { left++; right=left.ToString(); }
		}
		public sealed class InstanceFixture {
			public InstanceFixture(int offset) { Offset = offset; }
			public int Offset;
			[MethodImpl(MethodImplOptions.NoInlining)] public int Calculate(int value) => value + Offset;
		}
		public sealed class PrivateInstanceFixture {
			readonly int offset;
			public PrivateInstanceFixture(int offset) { this.offset = offset; }
			[MethodImpl(MethodImplOptions.NoInlining)] public int Calculate(int value) => value + offset;
		}
		sealed class FixtureException : Exception { }
	}

	/// <summary>Top-level on purpose. The shadowing check asks an assembly for a type by full name, and a
	/// nested type's full name carries a '+' that GetType reads as the nesting separator - so a nested
	/// fixture cannot be impersonated by a separately emitted assembly, while the real targets this
	/// exists for (a recompiled ASP.file_aspx) are top-level and can.
	///
	/// <para>One per test, and that is the product's property rather than test tidiness: an assembly
	/// cannot be unloaded from a .NET Framework AppDomain, so the first test to shadow a type shadows it
	/// for every test after it in the same process. Sharing one fixture made three later tests fail with
	/// a report they had not caused.</para></summary>
	public static class ShadowableFixture {
		[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
	}
	public static class PreexistingShadowFixture {
		[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
	}
	public static class UnrelatedLoadFixture {
		[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
	}
	public static class IdenticalCopyFixture {
		[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
	}
	public static class RemovedShadowFixture {
		[MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int left, int right) => left + right;
	}
}
