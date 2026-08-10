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
		public void Manifest_carries_exactly_the_probe_and_its_contracts() {
			var resolver = EmbeddedAssemblyResolver.FromEmbeddedManifest();
			Assert.Equal(new[] { "HookLab.Contracts", "HookLab.Probe.CorDebug" }, resolver.ManifestIdentities.ToArray());
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
			Assert.Null(resolver.Resolve("0Harmony, Version=2.3.6.0, Culture=neutral, PublicKeyToken=null"));
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

		[Fact]
		public void A_version_the_bundle_does_not_carry_refuses() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.Resolve("HookLab.Contracts, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"));
			Assert.Contains("version mismatch", error.Message, StringComparison.Ordinal);
		}

		[Fact]
		public void A_manifest_identity_that_the_bundle_does_not_match_refuses() {
			var resolver = Resolver("NotWhatIsEmbedded", RealDigest);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.Resolve("NotWhatIsEmbedded"));
			Assert.Contains("identity mismatch", error.Message, StringComparison.Ordinal);
		}

		[Fact]
		public void A_payload_that_came_from_disk_is_refused() {
			// This assembly is on disk, so naming it in the manifest reproduces exactly the case the check
			// exists for: the CLR probed the application directory before AssemblyResolve was ever raised.
			var onDisk = typeof(EmbeddedAssemblyResolverTests).Assembly;
			var resolver = Resolver(onDisk.GetName().Name!, RealDigest);
			var error = Assert.Throws<BootstrapIntegrityException>(() => resolver.VerifyNoDiskProvenance(new[] { onDisk }));
			Assert.Contains("resolved from disk", error.Message, StringComparison.Ordinal);
		}

		[Fact]
		public void Disk_provenance_ignores_assemblies_the_bundle_never_claimed() {
			var resolver = Resolver("HookLab.Contracts", RealDigest);
			resolver.VerifyNoDiskProvenance(AppDomain.CurrentDomain.GetAssemblies());
		}

		[Fact]
		public void A_malformed_manifest_line_refuses() {
			Assert.Throws<BootstrapIntegrityException>(() => EmbeddedAssemblyResolver.ParseManifest("only|two"));
			Assert.Throws<BootstrapIntegrityException>(() => EmbeddedAssemblyResolver.ParseManifest("name|resource|not-a-digest"));
		}

		[Fact]
		public void The_embedded_manifest_digests_match_the_embedded_bytes() {
			var resolver = EmbeddedAssemblyResolver.FromEmbeddedManifest();
			foreach (var identity in resolver.ManifestIdentities) {
				var assembly = resolver.Resolve(identity);
				Assert.NotNull(assembly);
				Assert.Equal("", assembly!.Location);
			}
			Assert.Equal(2, resolver.LoadCount);
		}
	}
}
