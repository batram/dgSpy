using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace RetargetNetstandardReferences;

/// <summary>Rewrites every <c>[netstandard]</c> type reference in an assembly to the .NET Framework
/// assembly that really defines the type, and drops the <c>netstandard</c> reference itself.
///
/// <para>This is the inverse of dnSpy's own <c>ConvertToNetstandardReferences</c> build task, and it
/// exists for one product reason: a shipped Unity player's <c>Managed</c> directory has nine non-Unity
/// assemblies and no <c>Facades</c> directory, so <c>netstandard, Version=2.0.0.0</c> is not there and
/// cannot be put there - it is strong-named to Microsoft's key, so nothing dgSpy builds can satisfy the
/// reference, and copying a copy off a machine is exactly the disk provenance HookLab forbids. The
/// requirement is therefore removed rather than satisfied.</para>
///
/// <para>The mapping is not guessed and is not taken from the machine. It is read out of the pinned
/// <c>Microsoft.NETFramework.ReferenceAssemblies.net48</c> package: whichever reference assembly
/// defines the type is where the rewritten reference points. Only assemblies named on the command line
/// may be targeted, so a type whose real home a stripped player does not ship fails the build by name
/// instead of failing inside a target.</para></summary>
static class Program {
	const string Netstandard = "netstandard";

	static int Main(string[] args) {
		try { return Run(args); }
		catch (Exception ex) {
			Console.Error.WriteLine("RetargetNetstandardReferences: " + ex.Message);
			return 1;
		}
	}

	static int Run(string[] args) {
		string? input = null, output = null, referenceDirectory = null, allow = null;
		var report = false;
		for (var index = 0; index < args.Length; index++) {
			switch (args[index]) {
			case "--input": input = Next(args, ref index); break;
			case "--output": output = Next(args, ref index); break;
			case "--reference-assemblies": referenceDirectory = Next(args, ref index); break;
			case "--allow": allow = Next(args, ref index); break;
			case "--report": report = true; break;
			default: throw new ArgumentException("Unknown argument: " + args[index]);
			}
		}
		if (input is null || referenceDirectory is null || allow is null)
			throw new ArgumentException("Usage: --input <assembly> [--output <assembly>] --reference-assemblies <dir> --allow <name,name,...> [--report]");
		if (output is null && !report)
			throw new ArgumentException("--output is required unless --report is given.");

		var allowed = allow.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim()).Where(name => name.Length != 0).ToArray();
		var map = BuildTypeMap(referenceDirectory, allowed, out var identities);

		using var module = ModuleDefMD.Load(input, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var netstandardRef = module.GetAssemblyRefs().FirstOrDefault(reference => reference.Name == Netstandard);
		if (netstandardRef is null) {
			Console.WriteLine(Path.GetFileName(input) + ": no netstandard reference, nothing to do.");
			if (output is not null && !report) File.Copy(input, output, overwrite: true);
			return 0;
		}

		// One AssemblyRef per target assembly, created from the reference assembly's own identity so the
		// rewritten reference names the version and public key token a net4x target actually publishes.
		var targets = new Dictionary<string, AssemblyRef>(StringComparer.Ordinal);
		AssemblyRef TargetRef(string name) {
			if (targets.TryGetValue(name, out var existing)) return existing;
			var created = module.GetAssemblyRefs().FirstOrDefault(reference => reference.Name == name)
				?? (AssemblyRef)module.UpdateRowId(new AssemblyRefUser(identities[name]));
			targets.Add(name, created);
			return created;
		}

		var rewritten = new SortedDictionary<string, string>(StringComparer.Ordinal);
		var unmapped = new SortedSet<string>(StringComparer.Ordinal);

		for (uint rid = 1; ; rid++) {
			var typeRef = module.ResolveTypeRef(rid);
			if (typeRef is null) break;
			// Nested types resolve through their declaring TypeRef, so rewriting the outermost scope moves
			// the whole chain. Only a TypeRef whose scope is the netstandard assembly is ours to move.
			if (typeRef.ResolutionScope is not AssemblyRef scope || scope.Name != Netstandard) continue;
			var fullName = typeRef.FullName;
			if (!map.TryGetValue(fullName, out var home)) { unmapped.Add(fullName); continue; }
			rewritten[fullName] = home;
			if (!report) typeRef.ResolutionScope = TargetRef(home);
		}

		foreach (var exported in module.ExportedTypes) {
			if (exported.Implementation is AssemblyRef scope && scope.Name == Netstandard)
				unmapped.Add("(exported type) " + exported.FullName);
		}

		Console.WriteLine(Path.GetFileName(input) + ": " + rewritten.Count + " netstandard type references over " +
			rewritten.Values.Distinct().Count() + " assemblies.");
		foreach (var group in rewritten.GroupBy(pair => pair.Value).OrderBy(group => group.Key, StringComparer.Ordinal))
			Console.WriteLine("  " + group.Key + ": " + group.Count());
		if (report)
			foreach (var pair in rewritten) Console.WriteLine("    " + pair.Key + " -> " + pair.Value);

		if (unmapped.Count != 0) {
			Console.Error.WriteLine(Path.GetFileName(input) + ": " + unmapped.Count +
				" netstandard types have no home among the permitted assemblies (" + string.Join(", ", allowed) + "):");
			foreach (var name in unmapped) Console.Error.WriteLine("  " + name);
			return 1;
		}
		if (report) return 0;

		// Nothing references the netstandard row any more, and dnlib emits only reachable AssemblyRefs, so
		// the reference disappears by construction. Asserted below rather than assumed.
		var options = new ModuleWriterOptions(module) { StrongNameKey = null };
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output!))!);
		module.Write(output, options);

		using var verify = ModuleDefMD.Load(output, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var remaining = verify.GetAssemblyRefs().Select(reference => reference.Name.String).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		if (remaining.Contains(Netstandard)) {
			Console.Error.WriteLine(Path.GetFileName(output) + ": still references netstandard after the rewrite.");
			return 1;
		}
		Console.WriteLine("  -> " + Path.GetFileName(output) + " references: " + string.Join(", ", remaining));
		return 0;
	}

	/// <summary>Full type name to the permitted reference assembly that defines it. Earlier entries in
	/// <paramref name="allowed"/> win, so the corlib is preferred over anything that merely forwards.</summary>
	static Dictionary<string, string> BuildTypeMap(string referenceDirectory, IReadOnlyList<string> allowed, out Dictionary<string, IAssembly> identities) {
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		identities = new Dictionary<string, IAssembly>(StringComparer.Ordinal);
		foreach (var name in allowed) {
			var path = Path.Combine(referenceDirectory, name + ".dll");
			if (!File.Exists(path)) throw new FileNotFoundException("Permitted reference assembly not found: " + path);
			using var module = ModuleDefMD.Load(path, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
			identities[name] = module.Assembly.ToAssemblyRef();
			foreach (var type in module.Types) {
				if (type.IsGlobalModuleType || !type.IsPublic) continue;
				if (!map.ContainsKey(type.FullName)) map.Add(type.FullName, name);
			}
			foreach (var exported in module.ExportedTypes) {
				if (exported.DeclaringType is not null || !exported.IsPublic) continue;
				if (!map.ContainsKey(exported.FullName)) map.Add(exported.FullName, name);
			}
		}
		return map;
	}

	static string Next(string[] args, ref int index) {
		if (++index >= args.Length) throw new ArgumentException("Missing value after " + args[index - 1]);
		return args[index];
	}
}
