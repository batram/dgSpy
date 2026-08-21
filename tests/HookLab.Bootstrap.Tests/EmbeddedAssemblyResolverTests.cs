using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	public class EmbeddedAssemblyResolverTests {
		const string ContractsResource = "HookLab.Bootstrap.Payloads.HookLab.Contracts.dll";
		static Assembly BootstrapAssembly => typeof(HookLabBootstrap).Assembly;

		static byte[] Payload(string resourceName) {
			using (var stream = BootstrapAssembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("Missing " + resourceName)) {
				var bytes = new byte[stream.Length];
				var offset = 0;
				while (offset != bytes.Length) offset += stream.Read(bytes, offset, bytes.Length - offset);
				return bytes;
			}
		}

		static EmbeddedAssemblyResolver Resolver(string name, string digest) {

			var bytes = Payload(ContractsResource);
			var resolver = new EmbeddedAssemblyResolver(
				new[] { new EmbeddedAssemblyEntry(name, ContractsResource, digest) },
				resourceName => {
					if (resourceName != ContractsResource) throw new InvalidOperationException("The resolver asked for an unexpected resource: " + resourceName);

					return bytes;
				});

			return resolver;
		}

		static string RealDigest => EmbeddedAssemblyResolver.Sha256Hex(Payload(ContractsResource));

		[Fact]
		public void Manifest_carries_exactly_the_probe_contracts_and_compiler_runtime() {
			var resolver = EmbeddedAssemblyResolver.FromEmbeddedManifest();
			Assert.Equal(new[] { "HookLab.Contracts", "HookLab.Probe.CorDebug", "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "System.Collections.Immutable", "System.Reflection.Metadata", "System.Runtime.CompilerServices.Unsafe" }, resolver.ManifestIdentities.ToArray());
			// 0Harmony is deliberately absent: the probe carries and resolves its own pinned backend, and a
			// second embedded copy served by this resolver would win the bind and leave two 0Harmony
			// assemblies resident - the duplicate patching backend the plan forbids.
			Assert.DoesNotContain("0Harmony", resolver.ManifestIdentities);
		}

		[Fact]
		public void A_tampered_digest_refuses_instead_of_loading() {
			var tampered = new string('a', 64);
			var resolver = Resolver("HookLab.Contracts", tampered);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.Resolve("HookLab.Contracts"));
			Assert.Contains("digest mismatch", error.Message, StringComparison.Ordinal);
			Assert.Equal(0, resolver.LoadCount);
		}

		[Fact]
		public void The_digest_check_is_not_vacuous_the_real_digest_loads() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			Assert.NotNull(resolver.Resolve("HookLab.Contracts"));
			Assert.Equal(1, resolver.LoadCount);
		}

		[Fact]
		public void An_identity_absent_from_the_manifest_does_not_resolve() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			Assert.Null(resolver.Resolve("System.Xml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
			Assert.Null(resolver.Resolve("0Harmony, Version=2.4.2.0, Culture=neutral, PublicKeyToken=null"));
			Assert.Equal(0, resolver.LoadCount);
			Assert.Equal(2, resolver.RefusedCount);
		}

		[Fact]
		public void Repeat_resolution_returns_the_same_instance_and_loads_once() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			var first = resolver.Resolve("HookLab.Contracts");
			var second = resolver.Resolve("HookLab.Contracts, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
			Assert.NotNull(first);
			Assert.Same(first, second);
			Assert.Equal(1, resolver.LoadCount);
		}

		/// <summary>A reference to something newer than the bundle carries is still refused. Handing back the
		/// older assembly is how a missing method becomes a crash in the target instead of a refusal here.</summary>
		[Fact]
		public void A_version_newer_than_the_bundle_carries_refuses() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.Resolve("HookLab.Contracts, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"));
			Assert.Contains("older than the reference", error.Message, StringComparison.Ordinal);
		}

		/// <summary>What a binding redirect would do, and the case Unity's Mono is the first runtime to
		/// reach: the pinned Roslyn references System.Collections.Immutable 10.0.0.0 while the payload
		/// carries 10.0.0.1. CLR v4 never loads Roslyn and CoreCLR has that assembly in its shared
		/// framework, so an exact-match rule looked correct until a third runtime bound a payload's payload.</summary>
		[Fact]
		public void A_reference_older_than_the_bundle_carries_is_unified() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			var resolved = resolver.Resolve("HookLab.Contracts, Version=0.9.0.0, Culture=neutral, PublicKeyToken=null");
			Assert.NotNull(resolved);
			Assert.Equal(new Version(1, 0, 0, 0), resolved!.GetName().Version);
		}

		[Fact]
		public void A_manifest_identity_that_the_bundle_does_not_match_refuses() {
			var resolver = Resolver("NotWhatIsEmbedded", RealDigest);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.Resolve("NotWhatIsEmbedded"));
			Assert.Contains("identity mismatch", error.Message, StringComparison.Ordinal);
		}

		/// <summary>The contract, stated as the binder sees it: ask for each payload's exact identity the way
		/// payload code's own references do, and get back the digest-verified instance this resolver loaded.
		/// An empty Location is reported as supporting evidence, but instance equality is the decision.</summary>
		[Fact]
		public void Every_payload_identity_binds_to_the_verified_embedded_instance() =>
			Assert.Equal("ok", InChildDomain("bindings-clean", probe => probe.VerifyBindings(null)));

		/// <summary>The case that broke the predicate this replaced, reproduced: a disk-backed assembly with a
		/// payload's simple name and a different strong identity is loaded in the domain. That is ordinary in
		/// an application domain - an IIS worker running Dynamics 365 carries its own
		/// System.Collections.Immutable 1.2.1.0 - and it says nothing about what our references bound to,
		/// because it cannot satisfy the exact identity they name.</summary>
		[Fact]
		public void A_foreign_assembly_sharing_a_payload_simple_name_is_not_a_refusal() {
			var directory = Path.Combine(Path.GetTempPath(), "hooklab-foreign-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			// Left on disk deliberately: the child domain that loaded it is gone, but this process cannot
			// always release the file, and a failed delete must not fail the test it is cleaning up after.
			try { Assert.Equal("ok", InChildDomain("bindings-foreign", probe => probe.VerifyBindings(directory))); }
			finally { try { Directory.Delete(directory, true); } catch (Exception) { } }
		}

		/// <summary>The genuine hijack, which must still refuse: a disk-backed assembly that can satisfy the
		/// payload's exact identity wins ordinary probing before AssemblyResolve is ever raised, so the binder
		/// answers with something other than the verified bytes. The test assembly plays the hijacker against a
		/// manifest entry naming it, which is that shape exactly.</summary>
		[Fact]
		public void An_exact_payload_identity_satisfied_from_disk_is_refused() {
			var refusal = InChildDomain("bindings-hijack", probe => probe.VerifySelfAsPayload());
			Assert.Contains("instead of the verified embedded payload", refusal, StringComparison.Ordinal);
			// The file, not the full path: the child domain loads this assembly from the application base
			// while the test host itself may be running a shadow copy from elsewhere.
			Assert.Contains("HookLab.Bootstrap.Tests.dll", refusal, StringComparison.Ordinal);
		}

		/// <summary>Each binder case gets its own AppDomain, because the product's rule is one resolver per
		/// domain: a second resolver byte-loading identities a first one already loaded is a situation the
		/// product never creates, and only the test could.</summary>
		static string InChildDomain(string name, Func<ResolverProbe, string> body) {
			var setup = new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.SetupInformation.ApplicationBase };
			var domain = AppDomain.CreateDomain("hooklab-" + name, null, setup);
			try {
				var probe = (ResolverProbe)domain.CreateInstanceAndUnwrap(typeof(ResolverProbe).Assembly.FullName, typeof(ResolverProbe).FullName!);
				return body(probe);
			}
			finally { AppDomain.Unload(domain); }
		}
	}

	/// <summary>Runs one binder verification inside a fresh AppDomain and reports the outcome as a string,
	/// because an exception would have to cross the domain boundary to be asserted on.</summary>
	public sealed class ResolverProbe : MarshalByRefObject {
		public override object? InitializeLifetimeService() => null;

		/// <summary>Verifies every manifest binding, optionally after planting a disk-backed assembly that
		/// shares a payload's simple name at a different version.</summary>
		public string VerifyBindings(string? foreignDirectory) {
			try {
				if (foreignDirectory != null) {
					var foreign = EmitDiskAssembly(foreignDirectory, "HookLab.Contracts", new Version(9, 9, 9, 9));
					if (foreign.Location.Length == 0) return "the planted assembly was not disk-backed";
					if (foreign.GetName().Name != "HookLab.Contracts") return "the planted assembly has the wrong name";
				}
				var resolver = EmbeddedAssemblyResolver.FromEmbeddedManifest();
				resolver.Install();
				var bindings = resolver.VerifyPayloadBindings();
				if (bindings.Count != resolver.ManifestIdentities.Count) return "verified " + bindings.Count + " of " + resolver.ManifestIdentities.Count + " identities";
				foreach (var binding in bindings) {
					if (!binding.MatchedVerifiedInstance) return binding.Expected + " bound to " + binding.Actual;
					if (binding.Location.Length != 0) return binding.Expected + " reports location " + binding.Location;
				}
				return "ok";
			}
			catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
		}

		/// <summary>Names this already-loaded, disk-backed test assembly as a payload, so the binder can
		/// satisfy the expected identity from disk and must be caught doing it.</summary>
		public string VerifySelfAsPayload() {
			try {
				var onDisk = typeof(ResolverProbe).Assembly;
				var bytes = File.ReadAllBytes(onDisk.Location);
				var resolver = new EmbeddedAssemblyResolver(
					new[] { new EmbeddedAssemblyEntry(onDisk.GetName().Name!, "self", EmbeddedAssemblyResolver.Sha256Hex(bytes)) },
					_ => bytes);
				resolver.Install();
				resolver.VerifyPayloadBindings();
				return "the hijacked binding was accepted";
			}
			catch (BootstrapIntegrityException ex) { return ex.Message; }
			catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
		}

		static Assembly EmitDiskAssembly(string directory, string name, Version version) {
			var assemblyName = new AssemblyName(name) { Version = version };
			var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, System.Reflection.Emit.AssemblyBuilderAccess.RunAndSave, directory);
			builder.DefineDynamicModule(name, name + ".dll").CreateGlobalFunctions();
			builder.Save(name + ".dll");
			return Assembly.LoadFrom(Path.Combine(directory, name + ".dll"));
		}

		[Fact]
		public void A_malformed_matrix_refuses() {
			Assert.Throws<BootstrapIntegrityException>(() => PayloadMatrix.Parse("only|two"));
			Assert.Throws<BootstrapIntegrityException>(() => PayloadMatrix.Parse(PayloadMatrix.Header + "\nname|resident|bootstrap|r|net48|x64|clrv4|name|1.0.0.0|none|project:a||not-a-digest"));
		}

		/// <summary>The resident reads its own payload matrix at load time, so a matrix that cannot be
		/// parsed there is a target that cannot be initialized. This proves the shipped one parses and that
		/// it describes the runtime axis the two backends actually differ on.</summary>
		[Fact]
		public void The_embedded_matrix_describes_every_runtime_family() {
			var matrix = EmbeddedAssemblyResolver.EmbeddedMatrix();
			Assert.Equal(PayloadRuntimes.ClrV4 | PayloadRuntimes.Unity, matrix["Harmony.Desktop"].Runtimes);
			Assert.Equal(PayloadRuntimes.CoreClr, matrix["Harmony.CoreClr"].Runtimes);
			// Carried by the probe, so deliberately not served by this resolver - but described, which is
			// the whole difference between a payload that ships and a payload that is accounted for.
			Assert.All(matrix.Carried(PayloadCarrier.Probe), entry => Assert.Equal(PayloadRole.PatchEngine, entry.Role));
			Assert.Equal(7, matrix.Carried(PayloadCarrier.Bootstrap).Count());
			Assert.Equal("nuget:lib.harmony/2.4.2", matrix["Harmony.Desktop"].Provenance);
			Assert.Equal("project:HookLab/HookLab.Probe.CorDebug", matrix["HookLab.Probe.CorDebug"].Provenance);
		}

		[Fact]
		public void The_embedded_manifest_digests_match_the_embedded_bytes() {
			var resolver = EmbeddedAssemblyResolver.FromEmbeddedManifest();
			foreach (var identity in resolver.ManifestIdentities) {
				var assembly = resolver.Resolve(identity);
				Assert.NotNull(assembly);
				Assert.Equal("", assembly!.Location);
			}
			Assert.Equal(7, resolver.LoadCount);
		}
	}
}
