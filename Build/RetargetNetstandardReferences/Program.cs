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
/// defines the type is where the rewritten reference points.</para>
///
/// <para>A type whose real home a player does not ship goes to a generated compatibility assembly
/// instead - see <see cref="EmitCompatibilityAssembly"/> - and every such placeholder is reported, so
/// the set cannot grow silently.</para></summary>
static class Program {
	const string Netstandard = "netstandard";
	// Fixed, not Guid.NewGuid(): the generated assembly is hashed into the payload matrix, and a fresh
	// MVID per build would make an identical input produce a different digest every time.
	static readonly Guid CompatModuleVersionId = new("6e8b4f21-0d3c-4a7e-9b52-1f0c5a6d8e34");

	static int Main(string[] args) {
		try { return Run(args); }
		catch (Exception ex) {
			Console.Error.WriteLine("RetargetNetstandardReferences: " + ex.Message);
			return 1;
		}
	}

	static int Run(string[] args) {
		var inputs = new List<string>();
		string? outputDirectory = null, referenceDirectory = null, allow = null, compatName = null, placeholdersFrom = null;
		var report = false;
		var demandReport = false;
		for (var index = 0; index < args.Length; index++) {
			switch (args[index]) {
			case "--input": inputs.Add(Next(args, ref index)); break;
			case "--output-directory": outputDirectory = Next(args, ref index); break;
			case "--reference-assemblies": referenceDirectory = Next(args, ref index); break;
			case "--allow": allow = Next(args, ref index); break;
			case "--compat-assembly": compatName = Next(args, ref index); break;
			case "--placeholders-from": placeholdersFrom = Next(args, ref index); break;
			case "--report": report = true; break;
			case "--demand-report": demandReport = true; break;
			default: throw new ArgumentException("Unknown argument: " + args[index]);
			}
		}
		if (inputs.Count == 0 || referenceDirectory is null || allow is null)
			throw new ArgumentException("Usage: --input <assembly> [--input ...] --output-directory <dir> --reference-assemblies <dir> " +
				"--allow <name,...> [--compat-assembly <name> --placeholders-from <name,...>] [--report] [--demand-report]");

		var allowed = Split(allow);
		if (demandReport) { foreach (var input in inputs) DemandReport(input, allowed); return 0; }
		if (outputDirectory is null && !report) throw new ArgumentException("--output-directory is required unless --report is given.");

		var map = BuildTypeMap(referenceDirectory, allowed, out var identities);

		// Pass one: what every input needs, and which of it has no home a supported target ships. The two
		// passes exist because one generated compatibility assembly serves all the inputs, so its contents
		// have to be known before any input is rewritten.
		var plans = inputs.Select(input => Plan(input, map)).ToArray();
		var homeless = plans.SelectMany(plan => plan.Homeless).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();

		AssemblyRef? compatRef = null;
		if (homeless.Length != 0) {
			if (compatName is null) {
				Console.Error.WriteLine(homeless.Length + " netstandard types have no home among the permitted assemblies (" + string.Join(", ", allowed) + "):");
				foreach (var name in homeless) Console.Error.WriteLine("  " + name);
				return 1;
			}
			var compatPath = Path.Combine(outputDirectory ?? ".", compatName + ".dll");
			compatRef = EmitCompatibilityAssembly(compatName, compatPath, homeless, referenceDirectory, Split(placeholdersFrom ?? ""), report);
		}

		foreach (var plan in plans) {
			Console.WriteLine(Path.GetFileName(plan.Input) + ": " + plan.Rewrites.Count + " netstandard type references.");
			foreach (var group in plan.Rewrites.GroupBy(pair => pair.Value).OrderBy(group => group.Key, StringComparer.Ordinal))
				Console.WriteLine("  " + group.Key + ": " + group.Count());
			if (report) continue;
			Rewrite(plan, map, identities, compatName, compatRef, Path.Combine(outputDirectory!, Path.GetFileName(plan.Input)));
		}
		return 0;
	}

	sealed record RetargetPlan(string Input, IReadOnlyDictionary<string, string> Rewrites, IReadOnlyList<string> Homeless);

	/// <summary>Which netstandard types an input references, and where each is going. Read-only: the
	/// module is reopened for the rewrite so a failed plan cannot leave a half-edited image behind.</summary>
	static RetargetPlan Plan(string input, IReadOnlyDictionary<string, string> map) {
		using var module = ModuleDefMD.Load(input, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var rewrites = new SortedDictionary<string, string>(StringComparer.Ordinal);
		var homeless = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var typeRef in NetstandardTypeRefs(module)) {
			var fullName = typeRef.FullName;
			if (map.TryGetValue(fullName, out var home)) rewrites[fullName] = home;
			else homeless.Add(fullName);
		}
		foreach (var exported in module.ExportedTypes)
			if (exported.Implementation is AssemblyRef scope && scope.Name == Netstandard)
				throw new InvalidOperationException(Path.GetFileName(input) + " forwards " + exported.FullName + " to netstandard, which this tool does not rewrite.");
		return new RetargetPlan(input, rewrites, homeless.ToArray());
	}

	static IEnumerable<TypeRef> NetstandardTypeRefs(ModuleDefMD module) {
		for (uint rid = 1; ; rid++) {
			var typeRef = module.ResolveTypeRef(rid);
			if (typeRef is null) yield break;
			// Nested types resolve through their declaring TypeRef, so moving the outermost scope moves the
			// whole chain. Only a TypeRef whose own scope is netstandard is ours to move.
			if (typeRef.ResolutionScope is AssemblyRef scope && scope.Name == Netstandard) yield return typeRef;
		}
	}

	static void Rewrite(RetargetPlan plan, IReadOnlyDictionary<string, string> map, IReadOnlyDictionary<string, IAssembly> identities,
		string? compatName, AssemblyRef? compatRef, string output) {
		using var module = ModuleDefMD.Load(plan.Input, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var targets = new Dictionary<string, AssemblyRef>(StringComparer.Ordinal);
		AssemblyRef TargetRef(string name) {
			if (targets.TryGetValue(name, out var existing)) return existing;
			var identity = name == compatName ? compatRef! : identities[name];
			var created = module.GetAssemblyRefs().FirstOrDefault(reference => reference.Name == name)
				?? (AssemblyRef)module.UpdateRowId(new AssemblyRefUser(identity));
			targets.Add(name, created);
			return created;
		}

		foreach (var typeRef in NetstandardTypeRefs(module).ToArray())
			typeRef.ResolutionScope = TargetRef(map.TryGetValue(typeRef.FullName, out var home) ? home : compatName!);

		// Nothing references the netstandard row any more, and dnlib emits only reachable AssemblyRefs, so
		// the reference disappears by construction. Asserted below rather than assumed.
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
		module.Write(output, new ModuleWriterOptions(module) { StrongNameKey = null });

		using var verify = ModuleDefMD.Load(output, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var remaining = verify.GetAssemblyRefs().Select(reference => reference.Name.String).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		if (remaining.Contains(Netstandard))
			throw new InvalidOperationException(Path.GetFileName(output) + " still references netstandard after the rewrite.");
		Console.WriteLine("  -> " + Path.GetFileName(output) + " references: " + string.Join(", ", remaining));
	}

	/// <summary>Emits placeholder definitions for the types a supported target does not ship, so that
	/// loading the compiler can succeed where using those types never would.
	///
	/// <para>The need is narrow and specific. Mono resolves a type's base type and its field types when it
	/// <em>prepares</em> the type, not when the code using them runs. Roslyn's ordinary Emit path prepares
	/// <c>DocumentationCommentCompiler</c>, whose <c>_includedFileCache</c> field is a
	/// <c>DocumentationCommentIncludeCache</c>, whose base type mentions <c>System.Xml.Linq.XDocument</c> -
	/// and a player ships no <c>System.Xml.Linq</c>. Measured: compiling a hook inside a real player died
	/// there with <c>TypeLoadException</c>, over a cache the compile never reads. On CLR v4 the same code
	/// is fine only because that runtime resolves lazily.</para>
	///
	/// <para>So the placeholders exist to be <em>found</em>, not to work. They carry no members: the method
	/// bodies that would use them are never executed, and if that assumption were ever wrong the failure is
	/// a loud <c>MissingMethodException</c> at the call rather than a silently wrong answer. Base types are
	/// copied from the pinned reference assemblies and emitted transitively, so an attribute stays an
	/// <c>Attribute</c> and a value type stays a value type.</para>
	///
	/// <para>Every placeholder is printed by the build. A set that grows is a set someone has to look
	/// at.</para></summary>
	static AssemblyRef EmitCompatibilityAssembly(string name, string path, IReadOnlyList<string> homeless,
		string referenceDirectory, IReadOnlyList<string> placeholderSources, bool report) {
		var shapes = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
		foreach (var source in placeholderSources) {
			var sourcePath = Path.Combine(referenceDirectory, source + ".dll");
			if (!File.Exists(sourcePath)) throw new FileNotFoundException("Placeholder shape assembly not found: " + sourcePath);
			var sourceModule = ModuleDefMD.Load(sourcePath, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
			foreach (var type in sourceModule.Types) if (!shapes.ContainsKey(type.FullName)) shapes.Add(type.FullName, type);
		}

		var corlib = new AssemblyRefUser(new AssemblyNameInfo("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
		var module = new ModuleDefUser(name + ".dll", CompatModuleVersionId, corlib) { Kind = ModuleKind.Dll, RuntimeVersion = "v4.0.30319" };
		var assembly = new AssemblyDefUser(name, new Version(1, 0, 0, 0));
		assembly.Modules.Add(module);

		var emitted = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
		TypeDef Emit(string fullName) {
			if (emitted.TryGetValue(fullName, out var existing)) return existing;
			if (!shapes.TryGetValue(fullName, out var shape))
				throw new InvalidOperationException("No pinned shape for placeholder type '" + fullName + "'. Add its assembly to --placeholders-from.");
			var separator = fullName.LastIndexOf('.');
			var created = new TypeDefUser(separator < 0 ? "" : fullName.Substring(0, separator), fullName.Substring(separator + 1)) {
				Attributes = TypeAttributes.Public | TypeAttributes.Class | (shape.IsSealed ? TypeAttributes.Sealed : 0) |
					(shape.IsAbstract && !shape.IsInterface ? TypeAttributes.Abstract : 0),
			};
			emitted.Add(fullName, created);
			module.Types.Add(created);
			// The base chain is followed rather than flattened to object, so a placeholder for an attribute
			// still derives from System.Attribute and a value type still derives from System.ValueType. A
			// base that is itself homeless becomes a placeholder too.
			var baseName = shape.BaseType?.FullName ?? "System.Object";
			created.BaseType = shapes.ContainsKey(baseName) ? Emit(baseName) : module.CorLibTypes.GetTypeRef("System", LastPart(baseName));
			if (shape.IsEnum) {
				// An enum needs its backing field or the runtime rejects the type outright.
				created.Fields.Add(new FieldDefUser("value__", new FieldSig(module.CorLibTypes.Int32),
					FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName));
			}
			foreach (var parameter in shape.GenericParameters)
				created.GenericParameters.Add(new GenericParamUser(parameter.Number, parameter.Flags, parameter.Name));
			return created;
		}

		foreach (var fullName in homeless) Emit(fullName);

		Console.WriteLine(name + ": " + emitted.Count + " placeholder type(s) for types no supported target ships:");
		foreach (var pair in emitted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			Console.WriteLine("  " + pair.Key + " : " + (pair.Value.BaseType?.FullName ?? "System.Object"));
		if (report) return assembly.ToAssemblyRef();

		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		module.Write(path);
		return assembly.ToAssemblyRef();
	}

	static string LastPart(string fullName) {
		var separator = fullName.LastIndexOf('.');
		return separator < 0 ? fullName : fullName.Substring(separator + 1);
	}

	/// <summary>What Mono demands <em>eagerly</em>, which is a strictly smaller and far more dangerous set
	/// than what an assembly merely references: a reference buried in an unreachable method body costs
	/// nothing, while a base type or a field type is resolved the moment the type is prepared.
	///
	/// <para>Diagnostic only, and the instrument that sized the placeholder problem at one type. Not part
	/// of the build.</para></summary>
	static void DemandReport(string input, IReadOnlyList<string> allowed) {
		using var module = ModuleDefMD.Load(input, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
		var permitted = new HashSet<string>(allowed, StringComparer.Ordinal);
		var findings = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

		// Every TypeRef a signature mentions, generic arguments included. Walking only the scope type is
		// the mistake that made a first version of this report say "none" while a real target died on
		// Dictionary<string, XDocument>: the scope type there is Dictionary, in mscorlib, and XDocument -
		// the type Mono actually could not load - is an argument.
		static IEnumerable<TypeRef> Mentions(TypeSig? signature) {
			switch (signature) {
			case GenericInstSig generic:
				foreach (var found in Mentions(generic.GenericType)) yield return found;
				foreach (var argument in generic.GenericArguments)
					foreach (var found in Mentions(argument)) yield return found;
				yield break;
			case NonLeafSig wrapper:
				foreach (var found in Mentions(wrapper.Next)) yield return found;
				yield break;
			case TypeDefOrRefSig direct:
				if (direct.TypeDefOrRef is TypeRef reference) yield return reference;
				yield break;
			}
		}

		void Consider(TypeDef owner, string what, TypeSig? signature) {
			foreach (var reference in Mentions(signature)) {
				var home = reference;
				while (home.ResolutionScope is TypeRef outer) home = outer;
				if (home.ResolutionScope is not AssemblyRef assembly || permitted.Contains(assembly.Name)) continue;
				if (!findings.TryGetValue(assembly.Name, out var owners)) findings[assembly.Name] = owners = new SortedSet<string>(StringComparer.Ordinal);
				owners.Add(owner.FullName + " " + what + " : " + reference.FullName);
			}
		}

		foreach (var type in module.GetTypes()) {
			Consider(type, "base", type.BaseType?.ToTypeSig());
			foreach (var iface in type.Interfaces) Consider(type, "implements", iface.Interface?.ToTypeSig());
			foreach (var field in type.Fields) Consider(type, "field " + field.Name, field.FieldType);
		}

		Console.WriteLine(Path.GetFileName(input) + ": eagerly demanded types outside the permitted set:");
		if (findings.Count == 0) { Console.WriteLine("  none."); return; }
		foreach (var pair in findings) {
			Console.WriteLine("  " + pair.Key + ": " + pair.Value.Count + " site(s)");
			foreach (var site in pair.Value) Console.WriteLine("    " + site);
		}
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

	static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part.Length != 0).ToArray();

	static string Next(string[] args, ref int index) {
		if (++index >= args.Length) throw new ArgumentException("Missing value after " + args[index - 1]);
		return args[index];
	}
}
