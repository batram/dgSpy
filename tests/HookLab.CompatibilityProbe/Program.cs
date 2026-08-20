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
				Console.WriteLine($"PASS  {family.Id}  {watch.ElapsedMilliseconds} ms");
				foreach (var line in report) Console.WriteLine("      " + line);
			}
			catch (ProbeRefusal refusal) {
				failures++;
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
		Console.Error.WriteLine("usage: HookLab.CompatibilityProbe [--payload <hooklab-bootstrap.net48.payload>] [--fixtures <directory>] [--runtime clrv4|coreclr|all] [--negative] [--keep]");
		Console.Error.WriteLine("       Defaults resolve the newest composed layout under artifacts\\layouts and the repository's built fixtures.");
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

/// <summary>One runtime family, and how this repository builds a target for it.</summary>
sealed record RuntimeFamily(string Id, PayloadRuntimes Flag, string FixtureFramework) {
	internal static readonly RuntimeFamily ClrV4 = new("clrv4", PayloadRuntimes.ClrV4, "net48");
	internal static readonly RuntimeFamily CoreClr = new("coreclr", PayloadRuntimes.CoreClr, "net10.0");
	internal static readonly RuntimeFamily[] All = { ClrV4, CoreClr };
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
	internal bool Negative { get; init; }
	internal bool Keep { get; init; }

	internal static ProbeOptions? Parse(string[] arguments) {
		string? payload = null, fixtures = null, runtime = "all";
		var negative = false; var keep = false;
		for (var index = 0; index < arguments.Length; index++) {
			switch (arguments[index]) {
			case "--payload": if (++index == arguments.Length) return null; payload = arguments[index]; break;
			case "--fixtures": if (++index == arguments.Length) return null; fixtures = arguments[index]; break;
			case "--runtime": if (++index == arguments.Length) return null; runtime = arguments[index]; break;
			case "--negative": negative = true; break;
			case "--keep": keep = true; break;
			default: return null;
			}
		}
		var families = runtime switch {
			"all" => RuntimeFamily.All,
			"clrv4" => new[] { RuntimeFamily.ClrV4 },
			"coreclr" => new[] { RuntimeFamily.CoreClr },
			_ => null,
		};
		if (families is null) return null;
		var repository = Repository();
		return new ProbeOptions {
			Payload = Path.GetFullPath(payload ?? DefaultPayload(repository)),
			Fixtures = Path.GetFullPath(fixtures ?? Path.Combine(repository, "tests", "TestTargets", "HookLabProbeTarget", "bin", "Release")),
			Families = families, Negative = negative, Keep = keep,
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
