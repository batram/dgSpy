using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HookLab.Bootstrap;
using HookLab.Contracts;
using HookLab.Host.Transport;
using HookLab.Host.Transport.Discovery;

/// <summary>One runtime family's complete resident lifecycle, stage by stage.
///
/// <para>Every step names itself before it runs, so a failure reports the stage rather than a stack.
/// The stage names are the ones the roadmap asks residents to report: payload verify, backend
/// selection, target launch, residency commit, listener ready, authenticated, compile, patch-engine
/// load and install, behavior, event, removal, retirement.</para></summary>
sealed class RuntimeLeg {
	const int HookedResult = 777;
	const int Unhooked = 42;

	readonly ProbeOptions options;
	readonly RuntimeFamily family;
	readonly List<string> identities = new();
	readonly List<string> report = new();
	string stage = "payload_verify";

	internal RuntimeLeg(ProbeOptions options, RuntimeFamily family) { this.options = options; this.family = family; }

	internal IReadOnlyList<string> Execute() {
		var run = Path.Combine(Path.GetTempPath(), "hooklab-probe-" + family.Id + "-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(run);
		Process? target = null;
		try {
			SelectPayloads();
			target = LaunchTarget(run);
			var facts = ReadFacts(run);
			VerifyRuntimeRange(target, facts);
			var started = CommitResidency(run, target, facts);
			var record = Authenticate(target, facts, started);
			InstallAndObserve(run, record, facts);
			Retire(run, target);
			return report;
		}
		catch (ProbeRefusal) { throw; }
		catch (Exception ex) { throw new ProbeRefusal(stage, CompatibilityProbe.Flatten(ex.Message), identities, ex); }
		finally {
			if (target is not null && !target.HasExited) { try { target.Kill(true); } catch { } }
			target?.Dispose();
			if (options.Keep) Console.WriteLine("      run_directory=" + run); else CompatibilityProbe.TryDelete(run);
		}
	}

	/// <summary>Reads the shipped matrix, proves it against the payload's own bytes, and then selects the
	/// slots this runtime family needs. Selection is the part that did not exist before: "CoreCLR uses a
	/// different Harmony" was previously a branch inside the target, and is now a stated requirement that
	/// can be missing.</summary>
	void SelectPayloads() {
		Stage("payload_verify");
		PayloadMatrix matrix;
		try { matrix = PayloadMatrixVerification.VerifyPayloadFile(options.Payload); }
		catch (Exception ex) { throw new ProbeRefusal(stage, "The shipped payload does not match its own matrix: " + CompatibilityProbe.Flatten(ex.Message), identities, ex); }

		Stage("backend_selection");
		var selected = matrix.Entries.Where(entry => (entry.Runtimes & family.PayloadFlag) != 0).ToArray();
		foreach (var entry in selected) {
			// "fallback" is part of the identity line, not a footnote: on a fallback slot the shipped bytes
			// are what this leg gets only if the target's runtime cannot satisfy the identity itself, and a
			// refusal listing it as carried would name the wrong assembly.
			var supply = (entry.Fallback & family.PayloadFlag) != 0 ? ", fallback" : "";
			identities.Add($"{entry.Id} -> {entry.AssemblyName}, Version={entry.AssemblyVersion}, PublicKeyToken={entry.PublicKeyToken} ({entry.TargetFramework}, {entry.Provenance}{supply})");
		}
		Require(PayloadRole.Resident, selected, 1);
		Require(PayloadRole.Contracts, selected, 1);
		Require(PayloadRole.PatchEngine, selected, 1);
		if (!selected.Any(entry => entry.Role == PayloadRole.Compiler))
			throw new ProbeRefusal(stage, $"No compiler payload is valid on {family.Id}.", identities);
		var engine = selected.Single(entry => entry.Role == PayloadRole.PatchEngine);
		report.Add($"patch_engine={engine.Id} {engine.AssemblyName} {engine.AssemblyVersion} ({engine.TargetFramework})");
		report.Add($"payload_slots={selected.Length}");
	}

	void Require(PayloadRole role, PayloadMatrixEntry[] selected, int count) {
		var found = selected.Count(entry => entry.Role == role);
		if (found != count) throw new ProbeRefusal(stage, $"Expected exactly {count} {role} payload valid on {family.Id}, found {found}.", identities);
	}

	Process LaunchTarget(string run) {
		Stage("target_launch");
		var executable = Path.Combine(options.Fixtures, family.FixtureFramework, "HookLabProbeTarget.exe");
		if (!File.Exists(executable))
			throw new ProbeRefusal(stage, "The probe fixture is not built for " + family.Id + ": " + executable +
				". Build it: dotnet build tests\\TestTargets\\HookLabProbeTarget\\HookLabProbeTarget.csproj -c Release", identities);
		var runtimeArgument = family.Launched && options.MonoRuntime is not null ? "\"" + options.MonoRuntime + "\" " : "";
		var info = family.Launched
			? new ProcessStartInfo(RequireLauncher(), runtimeArgument + "\"" + executable + "\" \"" + run + "\"")
			: new ProcessStartInfo(executable, "\"" + run + "\"");
		info.UseShellExecute = false;
		info.WorkingDirectory = Path.GetDirectoryName(executable)!;
		if (family.Launched && options.MonoAssemblies is not null) {
			if (!Directory.Exists(options.MonoAssemblies))
				throw new ProbeRefusal(stage, "No Mono assembly directory at " + options.MonoAssemblies + ".", identities);
			info.Environment["MONO_PATH"] = options.MonoAssemblies;
			report.Add("mono_assemblies=" + options.MonoAssemblies);
		}
		var process = Process.Start(info) ?? throw new ProbeRefusal(stage, "Could not start " + info.FileName, identities);
		if (!WaitFor(() => File.Exists(Path.Combine(run, "ready.txt")), process, TimeSpan.FromSeconds(30)))
			throw new ProbeRefusal(stage, "The fixture did not report ready within 30 seconds.", identities);
		report.Add("target_pid=" + process.Id.ToString(CultureInfo.InvariantCulture));
		return process;
	}

	/// <summary>The Mono runtime the leg is allowed to run under. Never discovered: see
	/// <see cref="ProbeOptions.Mono"/>.</summary>
	string RequireLauncher() {
		if (options.Mono is null)
			throw new ProbeRefusal(stage, "The Mono leg needs a Mono runtime to run its fixture under. Pass --mono <mono.exe> or set DGSPY_MONO_EXE; dgSpy does not ship or discover one.", identities);
		if (!File.Exists(options.Mono))
			throw new ProbeRefusal(stage, "No Mono runtime at " + options.Mono + ".", identities);
		if (options.MonoRuntime is not null && !File.Exists(options.MonoRuntime))
			throw new ProbeRefusal(stage, "No Mono runtime library at " + options.MonoRuntime + ".", identities);
		return options.Mono;
	}

	/// <summary>Reads the exact CoreCLR version the way the product does - from the loaded coreclr.dll's
	/// own directory - and requires it to fall inside a declared proved range. CLR v4 has one version and
	/// needs no range. Mono reports its own version, which is the only place it is available: its runtime
	/// module is a game's embedded <c>mono-2.0-*.dll</c> and carries no version in its path.</summary>
	void VerifyRuntimeRange(Process target, Facts facts) {
		Stage("runtime_range");
		if (facts["framework"] != family.Id)
			throw new ProbeRefusal(stage, $"The {family.FixtureFramework} fixture reports framework={facts["framework"]}, not {family.Id}.", identities);
		if (family == RuntimeFamily.Mono) { VerifyMonoRange(facts); return; }
		if (family != RuntimeFamily.CoreClr) { report.Add("runtime=" + facts["runtime_id"] + " (CLR v4)"); return; }
		var modules = target.Modules.Cast<ProcessModule>().Where(module => string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
		if (modules.Length != 1) throw new ProbeRefusal(stage, $"Expected exactly one loaded coreclr.dll, found {modules.Length}.", identities);
		var text = Path.GetFileName(Path.GetDirectoryName(modules[0].FileName));
		if (!Version.TryParse(text, out var version))
			throw new ProbeRefusal(stage, "The loaded CoreCLR runtime directory carries no version: " + modules[0].FileName, identities);
		if (!SupportedCoreClr.Ranges.Any(range => version >= range.MinimumInclusive && version < range.MaximumExclusive))
			throw new ProbeRefusal(stage, $"CoreCLR {version} is outside every proved range. Proved: {SupportedCoreClr.Describe()}.", identities);
		report.Add("runtime=CoreCLR " + version);
	}

	void VerifyMonoRange(Facts facts) {
		var display = facts["runtime_display"];
		// "6.13.0 (Visual Studio built mono)" and Unity's "(2021.3.45f1)" forms both start with the
		// version, and only the version is asserted on: the rest is a build description, not identity.
		var text = display.Split(' ')[0];
		if (!Version.TryParse(text, out var version))
			throw new ProbeRefusal(stage, "The Mono target reports no parseable runtime version: " + display, identities);
		if (!SupportedMono.Ranges.Any(range => version >= range.MinimumInclusive && version < range.MaximumExclusive))
			throw new ProbeRefusal(stage, $"Mono {version} is outside every proved range. Proved: {SupportedMono.Describe()}.", identities);
		report.Add("runtime=Mono " + display);
	}

	Facts ReadFacts(string run) {
		Stage("target_launch");
		return new Facts(Parse(File.ReadAllText(Path.Combine(run, "facts.txt"))));
	}

	/// <summary>Hands the target its parameters and waits for the resident to commit. The secret is this
	/// process's, so the endpoint it authenticates against below is one only this probe could open.</summary>
	Started CommitResidency(string run, Process target, Facts facts) {
		Stage("residency_commit");
		var secret = RandomNumberGenerator.GetBytes(32);
		var creationTicks = target.StartTime.ToUniversalTime().Ticks;
		var imagePath = target.MainModule?.FileName ?? throw new ProbeRefusal(stage, "The target has no main module.", identities);
		File.WriteAllText(Path.Combine(run, "initialize.params"), Parameters(facts, target.Id, creationTicks, imagePath, secret), Utf8);
		File.WriteAllText(Path.Combine(run, "payload.path"), options.Payload, Utf8);

		var reportPath = Path.Combine(run, "report.txt");
		if (!WaitFor(() => File.Exists(reportPath), target, TimeSpan.FromSeconds(60)))
			throw new ProbeRefusal(stage, "The resident published no report within 60 seconds.", identities);
		var committed = Parse(File.ReadAllText(reportPath));
		if (!committed.TryGetValue("status", out var status) || status != "ok")
			throw new ProbeRefusal(stage, "The resident refused: " + Describe(committed), identities);

		// Prepare's report, not Commit's: Prepare is where the payload graph became resident and where the
		// control endpoint was named. Commit reports only that behaviour was started.
		var prepared = Parse(File.ReadAllText(Path.Combine(run, "prepare.txt")));
		report.Add("backend=" + Value(prepared, "backend_identity"));
		report.Add("payload_load_count=" + Value(prepared, "payload_load_count"));
		// Live evidence, and the only place it exists: which fallback slots this particular runtime build
		// satisfied itself. Two Mono builds differ here, so a green leg that does not say which copy it
		// used has not actually distinguished them.
		report.Add("payload_deferrals=" + (prepared.TryGetValue("payload_deferrals", out var deferrals) && deferrals.Length != 0 ? deferrals : "none"));

		Stage("listener_ready");
		if (!prepared.TryGetValue("pipe_name", out var pipeName) || !prepared.TryGetValue("pipe_nonce_base64", out var nonce))
			throw new ProbeRefusal(stage, "The resident became resident without publishing a control endpoint: " + Describe(prepared), identities);
		return new Started(pipeName, Convert.FromBase64String(nonce), secret,
			int.Parse(Value(prepared, "protocol_version"), CultureInfo.InvariantCulture), imagePath, creationTicks);
	}

	ProbeDiscoveryRecord Authenticate(Process target, Facts facts, Started started) {
		Stage("authenticated");
		var identity = new TargetIdentity(DgSpyStateRoot.ResidentHostId, started.ImagePath, target.Id,
			new DateTime(started.CreationTicks, DateTimeKind.Utc), "x64", facts["runtime_id"], facts["appdomain_id"]);
		var record = new ProbeDiscoveryRecord(identity, Guid.NewGuid().ToString("N"), started.PipeName,
			started.EndpointNonce, started.Secret, started.ProtocolVersion, DateTime.UtcNow.AddMinutes(10));
		var health = ProbeTransportClient.VerifyHealth(record, false);
		using var status = JsonDocument.Parse(health.Status.PayloadJson);
		report.Add("probe_instance=" + status.RootElement.GetProperty("probe_instance_id").GetString());
		return record;
	}

	void InstallAndObserve(string run, ProbeDiscoveryRecord record, Facts facts) {
		var behavior = Path.Combine(run, "behavior.txt");
		if (ReadBehavior(behavior) != Unhooked)
			throw new ProbeRefusal("behavior", $"The fixture does not tick {Unhooked} before hooking, so a change would prove nothing.", identities);

		Stage("compile");
		try { ProbeTransportClient.Send(record, "install_compiled_prefix", HookParameters(facts, record, "compatibility-probe-compiled", "Postfix", CompiledPostfixSource), HooksVersion(record), 30000); }
		catch (Exception ex) { throw new ProbeRefusal(stage, "The resident could not compile and install a trivial hook: " + CompatibilityProbe.Flatten(ex.Message), identities, ex); }

		Stage("behavior");
		if (!WaitFor(() => ReadBehavior(behavior) == HookedResult, null, TimeSpan.FromSeconds(15)))
			throw new ProbeRefusal(stage, $"The compiled hook installed but the target still ticks {ReadBehavior(behavior)} rather than {HookedResult}.", identities);
		report.Add("compiled_hook=observed");

		// An observation hook, not the compiled one: compiled hooks change behaviour and emit nothing by
		// themselves, and the resident's event buffer is fed by the observation path. Installing both on
		// the same method is also what the product does, so this covers their coexistence.
		Stage("event");
		try { ProbeTransportClient.Send(record, "install", HookParameters(facts, record, "compatibility-probe-observed", "Prefix", null), HooksVersion(record), 30000); }
		catch (Exception ex) { throw new ProbeRefusal(stage, "The resident could not install an observation hook beside the compiled one: " + CompatibilityProbe.Flatten(ex.Message), identities, ex); }
		var count = 0;
		WaitFor(() => (count = EventCount(record)) >= 1, null, TimeSpan.FromSeconds(15));
		if (count < 1) throw new ProbeRefusal(stage, "The resident reported no hook events while the hooked method was being called.", identities);
		report.Add("events=" + count.ToString(CultureInfo.InvariantCulture));

		Stage("remove");
		foreach (var patchId in PatchIds(record)) ProbeTransportClient.Send(record, "uninstall", patchId, HooksVersion(record));
		if (!WaitFor(() => ReadBehavior(behavior) == Unhooked, null, TimeSpan.FromSeconds(15)))
			throw new ProbeRefusal(stage, "Removing the hooks did not restore the original behavior.", identities);
		if (PatchIds(record).Length != 0)
			throw new ProbeRefusal(stage, "The resident still owns a patch after removal.", identities);
	}

	void Retire(string run, Process target) {
		Stage("retire");
		File.WriteAllText(Path.Combine(run, "stop.txt"), "stop\n", Utf8);
		if (!target.WaitForExit(30000)) throw new ProbeRefusal(stage, "The target did not exit within 30 seconds of being asked to stop.", identities);
		if (target.ExitCode != 0) throw new ProbeRefusal(stage, "The target exited with code " + target.ExitCode.ToString(CultureInfo.InvariantCulture) + ".", identities);
		var shutdown = Path.Combine(run, "shutdown.txt");
		if (!File.Exists(shutdown)) throw new ProbeRefusal(stage, "The target exited without a resident shutdown report.", identities);
		var values = Parse(File.ReadAllText(shutdown));
		// behavior_commit=stopped is the resident's own word for "every hook I owned is gone and the
		// endpoint is down". cleanup_incomplete means a listener may still be accepting connections,
		// which is a leak worth failing a probe over even though the target is gone.
		if (Value(values, "behavior_commit") != "stopped")
			throw new ProbeRefusal(stage, "Retirement was not clean: " + Describe(values), identities);
		report.Add("retired=clean");
	}

	long HooksVersion(ProbeDiscoveryRecord record) {
		using var status = JsonDocument.Parse(ProbeTransportClient.Send(record, "status", "{}", null).PayloadJson);
		return status.RootElement.GetProperty("hooks_version").GetInt64();
	}

	string[] PatchIds(ProbeDiscoveryRecord record) {
		using var status = JsonDocument.Parse(ProbeTransportClient.Send(record, "status", "{}", null).PayloadJson);
		return status.RootElement.GetProperty("patch_ids").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
	}

	string PatchId(ProbeDiscoveryRecord record) {
		var ids = PatchIds(record);
		if (ids.Length != 1) throw new ProbeRefusal(stage, $"Expected exactly one resident patch, found {ids.Length}.", identities);
		return ids[0];
	}

	static int ReadBehavior(string path) {
		try { return int.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture); }
		catch { return int.MinValue; }
	}

	string Parameters(Facts facts, int processId, long creationTicks, string imagePath, byte[] secret) => Join(
		("host_id", DgSpyStateRoot.ResidentHostId), ("image_path", imagePath),
		("process_id", processId.ToString(CultureInfo.InvariantCulture)),
		("process_creation_utc_ticks", creationTicks.ToString(CultureInfo.InvariantCulture)),
		("architecture", "x64"), ("runtime_id", facts["runtime_id"]), ("appdomain_id", facts["appdomain_id"]),
		("endpoint", "pipe"), ("endpoint_secret_base64", Convert.ToBase64String(secret)));

	/// <summary>A Postfix rather than a skipping Prefix, and the difference is not cosmetic: a Harmony
	/// prefix that returns false skips the remaining prefixes as well as the original, so a compiled
	/// replacement prefix would silently suppress the observation hook installed beside it - and the
	/// event stage would fail for a reason that has nothing to do with runtime compatibility.</summary>
	static string CompiledPostfixSource =>
		"public static class H{public static void Postfix(ref int __result){__result=" + HookedResult.ToString(CultureInfo.InvariantCulture) + ";}}";

	/// <summary>An install request, with or without source. It carries the target's exact guards - MVID,
	/// token, declaring type, signature and IL digest - because the resident refuses anything less, and
	/// because a hook that installs against a method it cannot prove is the one failure mode HookLab may
	/// never have.</summary>
	string HookParameters(Facts facts, ProbeDiscoveryRecord record, string hookId, string kind, string? source) {
		var pairs = new List<(string, string)> {
			("host_id", DgSpyStateRoot.ResidentHostId), ("image_path", record.Target.ImagePath),
			("process_id", record.Target.ProcessId.ToString(CultureInfo.InvariantCulture)),
			("process_creation_utc_ticks", record.Target.ProcessCreationTimeUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)),
			("architecture", "x64"), ("runtime_id", facts["runtime_id"]), ("appdomain_id", facts["appdomain_id"]),
			("endpoint", "none"), ("completion_path", "unused"),
			("hook_id", hookId), ("hook_kind", kind), ("hook_assembly", facts["assembly"]),
			("hook_type", facts["type"]), ("hook_method", facts["method"]), ("hook_module_mvid", facts["mvid"]),
			("hook_metadata_token", facts["token"]), ("hook_declaring_type", facts["type"]),
			("hook_method_signature", facts["signature"]), ("hook_il_sha256", facts["il_sha256"]),
			("hook_revision", "1"), ("maximum_events_per_second", "20"), ("maximum_string_length", "128"),
		};
		if (source is not null) pairs.Add(("hook_source_base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(source))));
		return Join(pairs.ToArray());
	}

	int EventCount(ProbeDiscoveryRecord record) {
		var events = Parse(ProbeTransportClient.Send(record, "drain", "16", null).PayloadJson);
		return events.TryGetValue("count", out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
	}

	static string Join(params (string Key, string Value)[] pairs) => string.Join("\n", pairs.Select(pair => pair.Key + "=" + pair.Value)) + "\n";

	void Stage(string name) => stage = name;

	static bool WaitFor(Func<bool> condition, Process? process, TimeSpan timeout) {
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline) {
			if (condition()) return true;
			if (process is not null && process.HasExited) return condition();
			Thread.Sleep(25);
		}
		return condition();
	}

	static Dictionary<string, string> Parse(string text) {
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
			var separator = line.IndexOf('=');
			if (separator > 0) values[line.Substring(0, separator)] = line.Substring(separator + 1);
		}
		return values;
	}

	static string Value(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : "";
	static string Describe(Dictionary<string, string> values) => string.Join("; ", values.Select(pair => pair.Key + "=" + CompatibilityProbe.Flatten(pair.Value)));
	static UTF8Encoding Utf8 => new(false);

	sealed record Started(string PipeName, byte[] EndpointNonce, byte[] Secret, int ProtocolVersion, string ImagePath, long CreationTicks);

	sealed class Facts {
		readonly Dictionary<string, string> values;
		internal Facts(Dictionary<string, string> values) { this.values = values; }
		internal string this[string key] => values.TryGetValue(key, out var value) ? value
			: throw new InvalidOperationException("The fixture reported no " + key + ".");
	}
}
