using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HookLab.Bootstrap {
	/// <summary>Which runtime family a payload is valid on. This is the axis the matrix is keyed by:
	/// CoreCLR needed a different patch engine and a different compiler, and those differences were
	/// previously discoverable only by reading the code that branched on them.</summary>
	[Flags]
	enum PayloadRuntimes {
		None = 0,
		ClrV4 = 1,
		CoreClr = 2,
		/// <summary>Mono, including the Mono a Unity player embeds. Its own family rather than a synonym for
		/// <see cref="ClrV4"/>, even though it loads the same net48 assets: the payloads are shared, the
		/// compiler is not - Mono's CodeDom shells out to an <c>mcs</c> that is not there to shell to, so
		/// Mono uses the Roslyn slots CLR v4 carries but never selects.
		///
		/// <para>One family, not one per host. Mono 6.12 as installed by mono-project and the Mono 6.13 a
		/// Unity 2021.3 player embeds answer identically on everything this matrix decides - measured, not
		/// assumed. What does vary between Mono builds is the class libraries, and that is a property of a
		/// build rather than of the family: Unity's own standalone <c>mono.exe</c> carries a CoreFX-derived
		/// <c>System.IO.Pipes</c> whose servers cannot open at all on Windows, while both of the runtimes
		/// above are fine. A payload set cannot be keyed on that, so it is not.</para></summary>
		Mono = 4,
	}

	/// <summary>What a payload is for. The role is declared rather than inferred from a file name so that
	/// verification can say "the CoreCLR patch engine slot is empty" instead of "a file is missing".</summary>
	enum PayloadRole {
		Contracts,
		Resident,
		Compiler,
		CompilerSupport,
		PatchEngine,
	}

	/// <summary>Which assembly carries the payload's bytes. <see cref="PayloadCarrier.Bootstrap"/> payloads
	/// are embedded in HookLab.Bootstrap and served by <see cref="EmbeddedAssemblyResolver"/>;
	/// <see cref="PayloadCarrier.Probe"/> payloads are embedded in HookLab.Probe.CorDebug and loaded by its
	/// own pinned backend loader. Both are resident payloads and both belong in the matrix - the second
	/// kind was previously described nowhere, which is exactly the unmanifested-resident-DLL hole.</summary>
	enum PayloadCarrier {
		Bootstrap,
		Probe,
	}

	sealed class PayloadMatrixEntry {
		internal PayloadMatrixEntry(string id, PayloadRole role, PayloadCarrier carrier, string resourceName,
			string targetFramework, string architecture, PayloadRuntimes runtimes, PayloadRuntimes fallback,
			string assemblyName, Version assemblyVersion, string publicKeyToken, string provenance,
			IReadOnlyList<string> requires, string sha256) {
			Id = id; Role = role; Carrier = carrier; ResourceName = resourceName;
			TargetFramework = targetFramework; Architecture = architecture; Runtimes = runtimes; Fallback = fallback;
			AssemblyName = assemblyName; AssemblyVersion = assemblyVersion; PublicKeyToken = publicKeyToken;
			Provenance = provenance; Requires = requires; Sha256 = sha256;
		}
		internal string Id { get; }
		internal PayloadRole Role { get; }
		internal PayloadCarrier Carrier { get; }
		internal string ResourceName { get; }
		internal string TargetFramework { get; }
		internal string Architecture { get; }
		internal PayloadRuntimes Runtimes { get; }
		/// <summary>The subset of <see cref="Runtimes"/> on which this payload is a <em>fallback</em> for
		/// something the runtime may already supply, rather than an asset dgSpy insists on.
		///
		/// <para>This is the axis the family flags could not express. A payload set is a fallback for what a
		/// runtime lacks, and what a runtime lacks varies between builds of one family, not between
		/// families: mono-project's Mono 6.12 ships <c>System.Memory</c> 4.0.1.1 - too old for the pinned
		/// Roslyn - and no <c>System.Buffers</c> at all, while the Mono a Unity 2021.3 player embeds
		/// supplies both at 4.0.99.0 in its <c>Facades</c> directory. Carrying the slot unconditionally put
		/// two <c>System.Memory</c> assemblies in one Unity AppDomain, which made <c>ReadOnlySpan&lt;T&gt;</c>
		/// two distinct types and Roslyn's own <c>ImmutableArray.Create</c> overload unfindable.</para>
		///
		/// <para>On a fallback family the resident asks the binder for the payload's exact declared identity
		/// <em>before</em> byte-loading it. Whatever answers is what payload code would have bound to
		/// anyway: the runtime's copy if it can satisfy the reference, ours - served by the resolve hook,
		/// which the CLR raises only after ordinary probing failed - if it cannot. Either way exactly one
		/// copy is ever loaded, and which one is reported by identity.</para>
		///
		/// <para>Deliberately confined to <see cref="PayloadRole.CompilerSupport"/>. dgSpy's contracts, its
		/// resident, its pinned compiler and its patch engine are the assemblies whose provenance the whole
		/// bootstrap exists to guarantee; none of them may ever defer to something found in the target.
		/// A compiler-support facade is a different kind of thing - a versioned BCL shim whose only correct
		/// number of copies in a process is one - and deferring is what keeps that true.</para></summary>
		internal PayloadRuntimes Fallback { get; }
		internal string AssemblyName { get; }
		internal Version AssemblyVersion { get; }
		/// <summary>Lower-case hex, or "none" for an unsigned assembly.</summary>
		internal string PublicKeyToken { get; }
		/// <summary>Where the bytes came from: "project:&lt;path&gt;", "nuget:&lt;package&gt;/&lt;version&gt;", or
		/// "build:&lt;transform&gt;(&lt;inputs&gt;)" for bytes dgSpy's own build derived from pinned inputs it
		/// names. The third kind exists because the pinned Roslyn ships as <c>netstandard2.0</c> and a
		/// stripped Unity player has no <c>netstandard</c> facade to satisfy that with, so the build
		/// rewrites those references to the .NET Framework assemblies that really define the types. The
		/// bytes are then ours, and saying "nuget:" about them would be a claim the digest does not
		/// support - the transform is named here instead, with the package it consumed.</summary>
		internal string Provenance { get; }
		/// <summary>Ids of the other matrix entries this payload needs at run time. A name that is not an
		/// id in the same matrix is an incomplete dependency closure and fails the parse.</summary>
		internal IReadOnlyList<string> Requires { get; }
		internal string Sha256 { get; }
	}

	/// <summary>The authoritative, build-generated description of every resident payload dgSpy ships.
	///
	/// <para>It exists because runtime differences were real but distributed: which Harmony asset, which
	/// compiler, which framework, which runtime family each was valid on, and where each came from could
	/// only be recovered by reading five projects and two loaders. The matrix states all of it in one
	/// build-owned artifact that both the resident and the packaging tool verify against the actual bytes.</para>
	///
	/// <para>Format is line-oriented and pipe-delimited rather than JSON so the resident can parse it with
	/// no dependency of its own - the resident is loaded before its own payload dependencies exist.</para></summary>
	sealed class PayloadMatrix {
		internal const string ResourceName = "HookLab.Bootstrap.Payloads.matrix.txt";
		internal const string Header = "hooklab-payload-matrix|4";
		const int FieldCount = 14;

		readonly Dictionary<string, PayloadMatrixEntry> byId;

		PayloadMatrix(IReadOnlyList<PayloadMatrixEntry> entries) {
			Entries = entries;
			byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
		}

		internal IReadOnlyList<PayloadMatrixEntry> Entries { get; }

		internal PayloadMatrixEntry this[string id] => byId.TryGetValue(id, out var entry) ? entry
			: throw new BootstrapIntegrityException("No payload matrix entry with id '" + id + "'.");

		internal IEnumerable<PayloadMatrixEntry> Carried(PayloadCarrier carrier) => Entries.Where(entry => entry.Carrier == carrier);

		internal static PayloadMatrix Parse(string text) {
			if (text == null) throw new ArgumentNullException(nameof(text));
			var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Trim()).Where(line => line.Length != 0 && line[0] != '#').ToArray();
			if (lines.Length == 0) throw new BootstrapIntegrityException("The payload matrix is empty.");
			if (!string.Equals(lines[0], Header, StringComparison.Ordinal))
				throw new BootstrapIntegrityException("Unsupported payload matrix format: '" + lines[0] + "'. Expected '" + Header + "'.");

			var entries = new List<PayloadMatrixEntry>();
			var ids = new HashSet<string>(StringComparer.Ordinal);
			var resources = new HashSet<string>(StringComparer.Ordinal);
			foreach (var line in lines.Skip(1)) {
				var field = line.Split('|');
				if (field.Length != FieldCount)
					throw new BootstrapIntegrityException("Malformed payload matrix row (expected " + FieldCount + " fields, found " + field.Length + "): " + line);
				var id = Required(field[0], "id", line);
				if (!ids.Add(id)) throw new BootstrapIntegrityException("Duplicate payload matrix id: " + id);
				var role = ParseRole(field[1], line);
				var carrier = ParseCarrier(field[2], line);
				var resource = Required(field[3], "resource", line);
				if (!resources.Add(carrier + "/" + resource))
					throw new BootstrapIntegrityException("Duplicate payload matrix resource: " + resource);
				var tfm = Required(field[4], "target framework", line);
				var architecture = Required(field[5], "architecture", line);
				if (architecture != "any" && architecture != "x64")
					throw new BootstrapIntegrityException("Unsupported payload architecture '" + architecture + "': " + line);
				var runtimes = ParseRuntimes(field[6], line, atLeastOne: true);
				RejectFrameworkIncompatibility(tfm, runtimes, line);
				var fallback = ParseRuntimes(field[7], line, atLeastOne: false);
				if ((fallback & ~runtimes) != PayloadRuntimes.None)
					throw new BootstrapIntegrityException("A payload cannot be a fallback on a runtime family it does not claim: " + line);
				if (fallback != PayloadRuntimes.None && role != PayloadRole.CompilerSupport)
					throw new BootstrapIntegrityException("Only a compiler-support payload may defer to a runtime-supplied assembly; '" +
						id + "' is a " + field[1].Trim() + " payload: " + line);
				var assemblyName = Required(field[8], "assembly name", line);
				if (!Version.TryParse(field[9].Trim(), out var version))
					throw new BootstrapIntegrityException("Malformed payload assembly version '" + field[9] + "': " + line);
				var token = ParseToken(field[10], line);
				var provenance = Required(field[11], "provenance", line);
				if (!provenance.StartsWith("project:", StringComparison.Ordinal) && !provenance.StartsWith("nuget:", StringComparison.Ordinal) &&
					!provenance.StartsWith("build:", StringComparison.Ordinal))
					throw new BootstrapIntegrityException("Payload provenance must name a project, a package, or a build transform: " + line);
				// A derived payload has to say what it was derived from, or "build:" would be a way to
				// describe bytes of no stated origin at all - which is the one thing provenance is for.
				if (provenance.StartsWith("build:", StringComparison.Ordinal) &&
					provenance.IndexOf("nuget:", StringComparison.Ordinal) < 0 && provenance.IndexOf("project:", StringComparison.Ordinal) < 0)
					throw new BootstrapIntegrityException("A build-derived payload must name its pinned inputs: " + line);
				var requires = field[12].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToArray();
				var digest = NormalizeDigest(field[13]);
				if (digest.Length != 64 || !digest.All(IsHex))
					throw new BootstrapIntegrityException("Malformed payload digest '" + field[13] + "': " + line);
				entries.Add(new PayloadMatrixEntry(id, role, carrier, resource, tfm, architecture, runtimes, fallback,
					assemblyName, version, token, provenance, requires, digest));
			}

			var matrix = new PayloadMatrix(entries);
			matrix.ValidateShape();
			return matrix;
		}

		/// <summary>Rejects a matrix that is internally consistent row by row but does not describe a
		/// shippable product: an unresolvable dependency, an empty slot, or a runtime family with no way to
		/// patch anything.</summary>
		void ValidateShape() {
			foreach (var entry in Entries)
				foreach (var required in entry.Requires)
					if (!byId.ContainsKey(required))
						throw new BootstrapIntegrityException("Incomplete dependency closure: '" + entry.Id + "' requires '" + required + "', which is not in the matrix.");

			var residents = Entries.Where(entry => entry.Role == PayloadRole.Resident).ToArray();
			if (residents.Length != 1)
				throw new BootstrapIntegrityException("The matrix must declare exactly one resident payload, found " + residents.Length + ".");
			if (!Entries.Any(entry => entry.Role == PayloadRole.Contracts))
				throw new BootstrapIntegrityException("The matrix declares no contracts payload.");
			foreach (var runtime in new[] { PayloadRuntimes.ClrV4, PayloadRuntimes.CoreClr, PayloadRuntimes.Mono }) {
				if (!Entries.Any(entry => entry.Role == PayloadRole.PatchEngine && (entry.Runtimes & runtime) != 0))
					throw new BootstrapIntegrityException("The matrix declares no patch engine payload for " + Describe(runtime) + ".");
				if (!Entries.Any(entry => entry.Role == PayloadRole.Compiler && (entry.Runtimes & runtime) != 0))
					throw new BootstrapIntegrityException("The matrix declares no compiler payload for " + Describe(runtime) + ".");
				if ((residents[0].Runtimes & runtime) == 0)
					throw new BootstrapIntegrityException("The resident payload does not claim " + Describe(runtime) + ".");
			}
		}

		/// <summary>The one framework rule that is a fact rather than a judgement: a modern .NET target
		/// framework (net5.0 and later) cannot be loaded by CLR v4 or by Mono, so no such asset may
		/// claim either. The reverse is deliberately not asserted - net462 System.Collections.Immutable
		/// really does load in a CoreCLR target, and inventing a symmetric rule here would refuse a payload
		/// that ships and works.</summary>
		static void RejectFrameworkIncompatibility(string targetFramework, PayloadRuntimes runtimes, string line) {
			if (!IsModernDotNet(targetFramework)) return;
			if ((runtimes & PayloadRuntimes.ClrV4) != 0)
				throw new BootstrapIntegrityException("Framework-incompatible payload: '" + targetFramework + "' cannot load on CLR v4: " + line);
			if ((runtimes & PayloadRuntimes.Mono) != 0)
				throw new BootstrapIntegrityException("Framework-incompatible payload: '" + targetFramework + "' cannot load on Mono: " + line);
		}

		internal static bool IsModernDotNet(string targetFramework) {
			if (targetFramework == null) return false;
			var value = targetFramework.Trim();
			if (!value.StartsWith("net", StringComparison.Ordinal)) return false;
			var rest = value.Substring(3);
			var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
			if (digits.Length == 0) return false;
			// net48 and net472 have no dot; net5.0 and later always do. That separates the two eras exactly.
			if (rest.Length == digits.Length || rest[digits.Length] != '.') return false;
			return int.Parse(digits, CultureInfo.InvariantCulture) >= 5;
		}

		static string Describe(PayloadRuntimes runtime) =>
			runtime == PayloadRuntimes.ClrV4 ? "CLR v4" : runtime == PayloadRuntimes.CoreClr ? "CoreCLR" : "Mono";

		static PayloadRole ParseRole(string value, string line) {
			switch (value.Trim()) {
			case "contracts": return PayloadRole.Contracts;
			case "resident": return PayloadRole.Resident;
			case "compiler": return PayloadRole.Compiler;
			case "compiler-support": return PayloadRole.CompilerSupport;
			case "patch-engine": return PayloadRole.PatchEngine;
			default: throw new BootstrapIntegrityException("Unknown payload role '" + value + "': " + line);
			}
		}

		static PayloadCarrier ParseCarrier(string value, string line) {
			switch (value.Trim()) {
			case "bootstrap": return PayloadCarrier.Bootstrap;
			case "probe": return PayloadCarrier.Probe;
			default: throw new BootstrapIntegrityException("Unknown payload carrier '" + value + "': " + line);
			}
		}

		static PayloadRuntimes ParseRuntimes(string value, string line, bool atLeastOne) {
			var runtimes = PayloadRuntimes.None;
			foreach (var token in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
				switch (token.Trim()) {
				case "clrv4": runtimes |= PayloadRuntimes.ClrV4; break;
				case "coreclr": runtimes |= PayloadRuntimes.CoreClr; break;
				case "mono": runtimes |= PayloadRuntimes.Mono; break;
				default: throw new BootstrapIntegrityException("Unknown runtime family '" + token + "': " + line);
				}
			}
			if (atLeastOne && runtimes == PayloadRuntimes.None) throw new BootstrapIntegrityException("A payload must claim at least one runtime family: " + line);
			return runtimes;
		}

		static string ParseToken(string value, string line) {
			var token = value.Trim().ToLowerInvariant();
			if (token == "none") return token;
			if (token.Length != 16 || !token.All(IsHex))
				throw new BootstrapIntegrityException("Malformed public key token '" + value + "': " + line);
			return token;
		}

		static string Required(string value, string what, string line) {
			var trimmed = (value ?? "").Trim();
			if (trimmed.Length == 0) throw new BootstrapIntegrityException("Payload matrix row has an empty " + what + ": " + line);
			return trimmed;
		}

		static bool IsHex(char value) => (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');

		internal static string NormalizeDigest(string value) => (value ?? "").Replace("-", "").Trim().ToLowerInvariant();
	}
}
