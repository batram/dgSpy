using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HookLab.Probe.CorDebug.Patching;
using Xunit;

namespace HookLab.Probe.Tests {
	/// <summary>
	/// The resident carries two compilers and can only ever have one of them. CodeDom is a .NET
	/// Framework facility a CoreCLR target cannot find; Roslyn ships in the payload because CoreCLR has
	/// no CodeDom, and its dependency closure is what failed loudest during CoreCLR bring-up.
	///
	/// Keeping them apart was previously a convention - two [MethodImpl(NoInlining)] methods on one type
	/// - with nothing checking it, and it had already slipped: a private Format(CompilerError) helper put
	/// a CodeDom type in a signature on the type CoreCLR residents go through. These tests make the
	/// boundary a property of the assembly instead of a habit.
	///
	/// Signatures, fields and generic instantiations are what is checked, because those are what the CLR
	/// resolves when it prepares a member - which is the moment a missing compiler assembly turns into a
	/// TypeLoadException in a target rather than a compile error the caller can read.
	/// </summary>
	public class CompilerBoundaryTests {
		static readonly string[] ForbiddenNamespacePrefixes = {
			"Microsoft.CodeAnalysis", "System.CodeDom", "Microsoft.CSharp",
		};

		// Reached through the one public type on this path. The compiler internals are deliberately
		// internal, and widening them with InternalsVisibleTo just to inspect their shape would enlarge
		// the surface this test exists to keep narrow.
		static Assembly Probe => typeof(HookCompilationException).Assembly;

		static Type Type(string name) => Probe.GetType("HookLab.Probe.CorDebug.Patching." + name, true)!;

		/// <summary>Types every resident reaches on the way to a compile, on either runtime - nested types
		/// included, because a lambda that captured a compiler value becomes a nested closure class and
		/// would otherwise be outside the scan.</summary>
		static IEnumerable<Type> SharedTypes => new[] {
			Type("CompiledHookCompiler"), Type("HookCompileRequest"), Type("IHookSourceCompiler"),
			Type("HookSourceCompilerSelector"), Type("HookCompilationException"), Type("CompiledHook"),
		}.SelectMany(WithNested);

		static IEnumerable<Type> WithNested(Type type) {
			yield return type;
			foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				foreach (var inner in WithNested(nested)) yield return inner;
		}

		/// <summary>The outermost type a type is declared in. A compiler-generated closure such as
		/// <c>&lt;&gt;c</c> belongs to whatever wrote it, so it is reported under that name rather than as
		/// a type of its own.</summary>
		static string Owner(Type type) {
			while (type.DeclaringType is Type declaring) type = declaring;
			return type.Name;
		}

		[Fact]
		public void No_compiler_technology_appears_in_the_shared_boundary() {
			var leaks = SharedTypes.SelectMany(Leaks).ToArray();
			Assert.True(leaks.Length == 0,
				"A runtime-specific compiler type is reachable from the shared compiler boundary:" + Environment.NewLine +
				string.Join(Environment.NewLine, leaks) + Environment.NewLine +
				"Move it into DesktopCodeDomHookCompiler or CoreClrRoslynHookCompiler. The CLR resolves the types in a " +
				"member's signature when it prepares that member, so a mention here makes the runtime that lacks that " +
				"compiler load it anyway.");
		}

		/// <summary>The other half of the bargain: each technology must appear in exactly one type, so
		/// "it is isolated" cannot be satisfied by it having quietly disappeared.</summary>
		[Fact]
		public void Each_compiler_technology_lives_in_exactly_one_type() {
			Assert.Equal(new[] { "DesktopCodeDomHookCompiler" }, TypesMentioning("System.CodeDom", "Microsoft.CSharp"));
			Assert.Equal(new[] { "CoreClrRoslynHookCompiler" }, TypesMentioning("Microsoft.CodeAnalysis"));
		}

		[Fact]
		public void Both_backends_implement_the_runtime_neutral_boundary() {
			var contract = Type("IHookSourceCompiler");
			foreach (var name in new[] { "DesktopCodeDomHookCompiler", "CoreClrRoslynHookCompiler" }) {
				var backend = Type(name);
				Assert.True(contract.IsAssignableFrom(backend), name + " does not implement IHookSourceCompiler.");
				// One method, and it hands back a BCL Assembly. A backend that returned its own result type
				// would put that type back on the shared side the moment anything consumed it.
				var compile = contract.GetMethod("Compile")!;
				Assert.Equal(typeof(Assembly), compile.ReturnType);
				Assert.Equal(new[] { "HookCompileRequest" }, compile.GetParameters().Select(parameter => parameter.ParameterType.Name).ToArray());
			}
		}

		/// <summary>Neither backend may be held in a static field of a shared type: a static that
		/// referenced one would drag its compiler in through the class constructor, before any decision
		/// about which runtime this is had been made.</summary>
		[Fact]
		public void The_shared_boundary_holds_no_static_state_that_could_pull_a_backend_in() {
			var backends = new[] { "DesktopCodeDomHookCompiler", "CoreClrRoslynHookCompiler" };
			foreach (var type in SharedTypes)
				foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
					Assert.False(backends.Contains(field.FieldType.Name),
						type.Name + "." + field.Name + " is a static reference to " + field.FieldType.Name + ".");
		}

		[Fact]
		public void The_selector_reads_the_corlib_name_and_this_test_host_is_desktop_clr() {
			// net48 test host, so the desktop answer is the correct one here - which also means the
			// assertion is not vacuous: it would fail if the check were inverted.
			Assert.True(HookSourceCompilerSelectorIsDesktop());
		}

		static bool HookSourceCompilerSelectorIsDesktop() {
			var selector = Type("HookSourceCompilerSelector");
			return (bool)selector.GetProperty("IsDesktopClr", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
		}

		static string[] TypesMentioning(params string[] prefixes) => Probe.GetTypes()
			.Where(type => Mentions(type, prefixes).Any())
			.Select(Owner).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();

		static IEnumerable<string> Leaks(Type type) =>
			Mentions(type, ForbiddenNamespacePrefixes).Select(mention => "  " + type.Name + ": " + mention);

		/// <summary>Every type named in a type's own surface - base type, interfaces, field types, method
		/// and property and constructor signatures, and any generic argument of those - that sits under
		/// one of the given namespaces.</summary>
		static IEnumerable<string> Mentions(Type type, string[] prefixes) {
			const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
			var referenced = new List<(string Where, Type Type)>();
			if (type.BaseType != null) referenced.Add(("base type", type.BaseType));
			foreach (var contract in type.GetInterfaces()) referenced.Add(("interface", contract));
			foreach (var field in type.GetFields(all)) referenced.Add(("field " + field.Name, field.FieldType));
			foreach (var property in type.GetProperties(all)) referenced.Add(("property " + property.Name, property.PropertyType));
			foreach (var method in type.GetMethods(all)) {
				referenced.Add(("return of " + method.Name, method.ReturnType));
				foreach (var parameter in method.GetParameters()) referenced.Add(("parameter of " + method.Name, parameter.ParameterType));
			}
			foreach (var constructor in type.GetConstructors(all))
				foreach (var parameter in constructor.GetParameters()) referenced.Add(("constructor parameter", parameter.ParameterType));

			foreach (var (where, referencedType) in referenced)
				foreach (var candidate in Expand(referencedType))
					if (candidate.FullName is string name && prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
						yield return where + " -> " + name;
		}

		/// <summary>A type and everything inside it: element types of arrays and by-refs, and the
		/// arguments of a generic instantiation. ImmutableArray&lt;Diagnostic&gt; hides Roslyn one level
		/// down, and a check that only looked at the outer type would not see it.</summary>
		static IEnumerable<Type> Expand(Type type) {
			yield return type;
			if (type.HasElementType && type.GetElementType() is Type element)
				foreach (var inner in Expand(element)) yield return inner;
			if (type.IsGenericType)
				foreach (var argument in type.GetGenericArguments())
					foreach (var inner in Expand(argument)) yield return inner;
		}
	}
}
