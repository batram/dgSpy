using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookLab.Bootstrap;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;

return CompatibilityProbe.Run(args);

/// <summary>A fast, real-process check that the shipped resident payload actually works on each runtime
/// family dgSpy supports - without dnSpy, without a GUI, and in seconds rather than minutes.
///
/// <para>It exists because runtime differences surfaced late. A wrong Harmony asset, an unresolvable
/// compiler dependency or a payload that cannot be loaded at all used to appear only inside a packaged
/// live gate, as a timeout or an opaque func-eval failure, after everything else had already run. This
/// fails first, in one named stage, and says which assembly identity was involved.</para>
///
/// <para>It complements the packaged live gates and never replaces them. The two things it deliberately
/// does not prove are arrival - native injection on CLR v4, debugger evaluation on CoreCLR - and
/// anything about dnSpy. The target loads the payload into itself, which is the CoreCLR delivery path
/// minus the debugger, so what is measured here is everything downstream of arrival: payload selection,
/// dependency closure, compiler creation, compilation, patch-engine load, behavior, events, removal and
/// retirement.</para></summary>
static class CompatibilityProbe {
	internal static int Run(string[] arguments) {
		var options = ProbeOptions.Parse(arguments);
		if (options is null) return Usage();
		var failures = 0;
		foreach (var family in options.Families) {
			var watch = Stopwatch.StartNew();
			try {
				var report = new RuntimeLeg(options, family).Execute();
				if (family.ExpectedRefusal is not null) {
					failures++;
					Console.Error.WriteLine($"FAIL  {family.Id}  a leg that is supposed to be refused completed instead.");
					// The lifecycle it managed, not just the fact that it managed one: a boundary that moves
					// is a question about what is now possible, and the stages it reached are the answer.
					foreach (var line in report) Console.Error.WriteLine("      " + line);
					Console.Error.WriteLine($"      Expected: {family.ExpectedRefusal.Because}");
					Console.Error.WriteLine("      If that boundary has genuinely moved, the family's ExpectedRefusal is what to update - and the product's Mono support statements with it.");
					continue;
				}
				Console.WriteLine($"PASS  {family.Id}  {watch.ElapsedMilliseconds} ms");
				foreach (var line in report) Console.WriteLine("      " + line);
			}
			catch (ProbeRefusal refusal) when (family.ExpectedRefusal is not null && family.ExpectedRefusal.Matches(refusal)) {
				Console.WriteLine($"REFUSED  {family.Id}  {watch.ElapsedMilliseconds} ms  stage={refusal.Stage}  (expected)");
				Console.WriteLine("      " + family.ExpectedRefusal.Because);
				Console.WriteLine("      " + refusal.Message);
			}
			catch (ProbeRefusal refusal) {
				failures++;
				if (family.ExpectedRefusal is not null)
					Console.Error.WriteLine($"      This leg is expected to be refused at stage={family.ExpectedRefusal.Stage}, and was not.");
				Console.Error.WriteLine($"FAIL  {family.Id}  stage={refusal.Stage}");
				Console.Error.WriteLine($"      {refusal.Message}");
				foreach (var identity in refusal.Identities) Console.Error.WriteLine("      identity=" + identity);
				if (refusal.InnerException is not null) Console.Error.WriteLine("      cause=" + refusal.InnerException.GetType().Name + ": " + Flatten(refusal.InnerException.Message));
			}
		}
		if (options.Negative && !NegativePayloadIsRefused(options)) failures++;
		Console.WriteLine(failures == 0 ? "compatibility probe: all legs passed" : $"compatibility probe: {failures} leg(s) failed");
		return failures == 0 ? 0 : 1;
	}

	/// <summary>Proves the payload-verify stage is not vacuous, before any GUI gate has a chance to run.
	/// A payload with one byte changed must be refused by identity, not merely fail later and elsewhere.</summary>
	static bool NegativePayloadIsRefused(ProbeOptions options) {
		var directory = Path.Combine(Path.GetTempPath(), "hooklab-probe-negative-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			var tampered = Path.Combine(directory, Path.GetFileName(options.Payload));
			var bytes = File.ReadAllBytes(options.Payload);
			// Flip a byte inside a payload's own bytes, located by searching the image for a window taken
			// from the extracted resource. Picking an offset by guesswork lands in PE padding as often as
			// not, and a tamper that changes no payload is a negative test that quietly proves nothing.
			var contracts = PayloadMatrixVerification.ReadEmbeddedResources(bytes, options.Payload)
				.First(pair => pair.Key.EndsWith("HookLab.Contracts.dll", StringComparison.Ordinal)).Value;
			var window = contracts.AsSpan(contracts.Length / 2, 32);
			var at = new ReadOnlySpan<byte>(bytes).IndexOf(window);
			if (at < 0) throw new InvalidOperationException("Could not locate the embedded payload inside its own carrier.");
			bytes[at + 16] ^= 0xFF;
			File.WriteAllBytes(tampered, bytes);
			try { PayloadMatrixVerification.VerifyPayloadFile(tampered); }
			catch (Exception ex) {
				Console.WriteLine("PASS  negative  a tampered payload is refused: " + Flatten(ex.Message));
				return true;
			}
			Console.Error.WriteLine("FAIL  negative  stage=payload_verify");
			Console.Error.WriteLine("      A payload with a changed byte was accepted, so payload verification proves nothing.");
			return false;
		}
		finally { TryDelete(directory); }
	}

	static int Usage() {
		Console.Error.WriteLine("usage: HookLab.CompatibilityProbe [--payload <hooklab-bootstrap.net48.payload>] [--fixtures <directory>] [--runtime clrv4|coreclr|mono|all] [--mono <mono.exe>] [--mono-assemblies <dir>] [--negative] [--keep]");
		Console.Error.WriteLine("       Defaults resolve the newest composed layout under artifacts\\layouts and the repository's built fixtures.");
		Console.Error.WriteLine("       --runtime mono is opt-in and needs --mono or DGSPY_MONO_EXE; 'all' stays clrv4 and coreclr.");
		return 2;
	}

	internal static string Flatten(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
	internal static void TryDelete(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { } }
}

/// <summary>A refusal that names the stage it happened in and the payload identities that stage had
/// selected. "It failed" is what this replaces: the point of the probe is that the first line of output
/// is enough to know which runtime, which step, and which assembly.</summary>
sealed class ProbeRefusal : Exception {
	internal ProbeRefusal(string stage, string message, IReadOnlyList<string> identities, Exception? inner = null) : base(message, inner) {
		Stage = stage; Identities = identities;
	}
	internal string Stage { get; }
	internal IReadOnlyList<string> Identities { get; }
}

/// <summary>One runtime family, and how this repository builds and starts a target for it.
///
/// <para><paramref name="PayloadFlag"/> is deliberately separate from <paramref name="Id"/>. Mono is its
/// own runtime family with its own leg, but the shipped matrix declares no Mono row yet: the point of
/// the Mono leg is to find out whether the CLR v4 slots are loadable there, and a matrix row saying so
/// in advance would be the claim rather than the evidence. When the answer is in, Mono gets rows of its
/// own and this collapses back to one field.</para>
///
/// <para><paramref name="Launched"/> is false for a family whose fixture is its own executable. Mono is
/// reached by handing the same net48 fixture to a <c>mono.exe</c>, which is what makes the leg a Mono
/// leg rather than a second CLR v4 one - no Unity, no mod loader, no game.</para></summary>
/// <param name="ExpectedRefusal">The refusal this family is currently supposed to produce, or null when
/// it must complete a lifecycle. Mono has one: HookLab residency is refused there because Unity's Mono
/// implements neither <c>WindowsIdentity.User</c> nor <c>PipeSecurity.AddAccessRule</c>, so the control
/// endpoint's access control cannot be built. Asserting the boundary rather than merely failing is what
/// makes the leg worth running: it fails if a Mono target ever stops at a <em>different</em> stage, which
/// is how a regression on the way to that boundary - or progress past it - would be noticed.</param>
sealed record RuntimeFamily(string Id, PayloadRuntimes PayloadFlag, string FixtureFramework, bool Launched,
	bool BorrowsClrV4Payloads, ExpectedRefusal? ExpectedRefusal = null) {
	internal static readonly RuntimeFamily ClrV4 = new("clrv4", PayloadRuntimes.ClrV4, "net48", false, false);
	internal static readonly RuntimeFamily CoreClr = new("coreclr", PayloadRuntimes.CoreClr, "net10.0", false, false);
	/// <summary>A full lifecycle, not a boundary assertion. It was the latter while Unity's Mono could not
	/// build the control endpoint's access control; the endpoint is now created through the Win32 API and
	/// the leg runs every stage the other two do. What it still does not prove is arrival, exactly as for
	/// the other families - which is why a Mono row in <c>HookLabBackends</c> does not follow from it.</summary>
	internal static readonly RuntimeFamily Mono = new("mono", PayloadRuntimes.ClrV4, "net48", true, true);

	/// <summary>The legs <c>--runtime all</c> runs. Mono is not in it, and that is a statement rather
	/// than an oversight: it needs a Mono runtime this repository does not ship, so including it would
	/// turn "no Mono installed" into a gate failure on every machine without one. It is asked for by
	/// name until a packaged Mono backend exists.</summary>
	internal static readonly RuntimeFamily[] All = { ClrV4, CoreClr };
}

/// <summary>A refusal a leg is expected to reach, and the boundary it stands for.</summary>
sealed record ExpectedRefusal(string Stage, string MessageContains, string Because) {
	internal bool Matches(ProbeRefusal refusal) =>
		refusal.Stage == Stage && refusal.Message.Contains(MessageContains, StringComparison.Ordinal);
}

/// <summary>The Mono runtimes HookLab has been driven against, stated the way <see cref="SupportedCoreClr"/>
/// states CoreCLR's: as a range with the evidence beside it, not as the word "Mono".</summary>
sealed record SupportedMono(Version MinimumInclusive, Version MaximumExclusive, string Evidence) {
	internal static readonly SupportedMono[] Ranges = {
		new(new Version(6, 12), new Version(6, 14), "Unity 2021.3 MonoBleedingEdge and this probe"),
	};
	internal static string Describe() => string.Join(", ", Ranges.Select(range => $"[{range.MinimumInclusive}, {range.MaximumExclusive}) - {range.Evidence}"));
}

/// <summary>The CoreCLR runtime versions HookLab is proved on, stated as data rather than left implied
/// by the word "CoreCLR".
///
/// <para>An entry means a packaged live hook lifecycle has actually run on that range, not that it is
/// expected to work. Adding a major version here is a claim that needs its own evidence; a target
/// outside every range is refused by name, which is a far more useful answer than a compile or patch
/// failure five stages later.</para></summary>
sealed record SupportedCoreClr(Version MinimumInclusive, Version MaximumExclusive, string Evidence) {
	internal static readonly SupportedCoreClr[] Ranges = {
		new(new Version(10, 0), new Version(11, 0), "packaged CoreCLR HookLab live gate and this probe"),
	};
	internal static string Describe() => string.Join(", ", Ranges.Select(range => $"[{range.MinimumInclusive}, {range.MaximumExclusive}) - {range.Evidence}"));
}

sealed class ProbeOptions {
	internal required string Payload { get; init; }
	internal required string Fixtures { get; init; }
	internal required RuntimeFamily[] Families { get; init; }
	/// <summary>The <c>mono.exe</c> the Mono leg runs its fixture under, or null. Never guessed: a Mono
	/// runtime found by scanning the machine would make the leg's result depend on which game or editor
	/// happened to be installed, and the runtime identity is the first thing this leg has to be exact
	/// about.</summary>
	internal string? Mono { get; init; }
	/// <summary>The assembly directory the Mono leg's fixture resolves its framework assemblies from, or
	/// null for whatever <see cref="Mono"/> ships with.
	///
	/// <para>This is not a convenience. A standalone <c>mono.exe</c> and a Unity player do not have the
	/// same class libraries: Unity 2021.3's <c>mono.exe</c> carries a CoreFX-derived
	/// <c>System.IO.Pipes</c> that P/Invokes a <c>System.Native</c> shim absent on Windows, so every
	/// named-pipe server fails there - while the player's own <c>System.Core.dll</c> is classic Mono and
	/// its pipe servers work. Measured on 2026-08-20; the editor's <c>unityjit-win32</c> profile is
	/// byte-identical to the shipped player's Managed directory, which is what makes a Unity-accurate
	/// Mono fixture possible without a game.</para></summary>
	internal string? MonoAssemblies { get; init; }
	/// <summary>The Mono runtime library the launcher should load, passed to it as its first argument, or
	/// null when the launcher is a complete <c>mono.exe</c>.
	///
	/// <para>Unity ships <c>mono.exe</c> as x86 only - measured on 2026-08-20 - so it cannot host an x64
	/// Mono target at all, and dgSpy is x64. The x64 runtime exists only as the player's embedded
	/// <c>MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll</c>, so an x64 Mono fixture has to be a small
	/// host that loads it and calls <c>mono_main</c>. That host is <c>MonoHost64</c> in this
	/// repository.</para></summary>
	internal string? MonoRuntime { get; init; }
	internal bool Negative { get; init; }
	internal bool Keep { get; init; }

	internal static ProbeOptions? Parse(string[] arguments) {
		string? payload = null, fixtures = null, runtime = "all", mono = null, monoAssemblies = null, monoRuntime = null;
		var negative = false; var keep = false;
		for (var index = 0; index < arguments.Length; index++) {
			switch (arguments[index]) {
			case "--payload": if (++index == arguments.Length) return null; payload = arguments[index]; break;
			case "--fixtures": if (++index == arguments.Length) return null; fixtures = arguments[index]; break;
			case "--runtime": if (++index == arguments.Length) return null; runtime = arguments[index]; break;
			case "--mono": if (++index == arguments.Length) return null; mono = arguments[index]; break;
			case "--mono-assemblies": if (++index == arguments.Length) return null; monoAssemblies = arguments[index]; break;
			case "--mono-runtime": if (++index == arguments.Length) return null; monoRuntime = arguments[index]; break;
			case "--negative": negative = true; break;
			case "--keep": keep = true; break;
			default: return null;
			}
		}
		var families = runtime switch {
			"all" => RuntimeFamily.All,
			"clrv4" => new[] { RuntimeFamily.ClrV4 },
			"coreclr" => new[] { RuntimeFamily.CoreClr },
			"mono" => new[] { RuntimeFamily.Mono },
			_ => null,
		};
		if (families is null) return null;
		mono ??= Environment.GetEnvironmentVariable("DGSPY_MONO_EXE");
		monoAssemblies ??= Environment.GetEnvironmentVariable("DGSPY_MONO_ASSEMBLIES");
		monoRuntime ??= Environment.GetEnvironmentVariable("DGSPY_MONO_RUNTIME");
		var repository = Repository();
		return new ProbeOptions {
			Payload = Path.GetFullPath(payload ?? DefaultPayload(repository)),
			Fixtures = Path.GetFullPath(fixtures ?? Path.Combine(repository, "tests", "TestTargets", "HookLabProbeTarget", "bin", "Release")),
			Families = families, Mono = string.IsNullOrWhiteSpace(mono) ? null : Path.GetFullPath(mono),
			MonoAssemblies = string.IsNullOrWhiteSpace(monoAssemblies) ? null : Path.GetFullPath(monoAssemblies),
			MonoRuntime = string.IsNullOrWhiteSpace(monoRuntime) ? null : Path.GetFullPath(monoRuntime),
			Negative = negative, Keep = keep,
		};
	}

	static string Repository() {
		for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
			if (File.Exists(Path.Combine(current.FullName, "dnSpy.sln"))) return current.FullName;
		return Environment.CurrentDirectory;
	}

	/// <summary>The newest composed layout's payload. The probe is about the artifact that ships, so a
	/// project bin directory is deliberately not a fallback: a layout is the only place the payload has
	/// been through composition and verification.</summary>
	static string DefaultPayload(string repository) {
		var configured = Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(configured, "hooklab", "hooklab-bootstrap.net48.payload");
		var layouts = Path.Combine(repository, "artifacts", "layouts");
		var newest = Directory.Exists(layouts)
			? new DirectoryInfo(layouts).GetDirectories().OrderByDescending(directory => directory.LastWriteTimeUtc).FirstOrDefault()
			: null;
		return newest is null
			? Path.Combine(layouts, "local", "hooklab", "hooklab-bootstrap.net48.payload")
			: Path.Combine(newest.FullName, "hooklab", "hooklab-bootstrap.net48.payload");
	}
}
