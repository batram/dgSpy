using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HookLab.Bootstrap.Tests {
	public interface IFixtureWorker {
		int Run(int value);
	}

	/// <summary>The hook target. Virtual and reached through an interface for the same reason T00's fixture
	/// was: an inlined call cannot be intercepted, and this suite is not testing the inlining report.</summary>
	public class FixtureWorker : IFixtureWorker {
		public static int Observed;
		public virtual int Run(int value) {
			Observed += value;
			return value + 7;
		}
	}

	/// <summary>Guard values computed host-side, without referencing the probe. Reimplementing
	/// MethodGuards' formats here is the point: a guard the target derives from the method it is about to
	/// patch checks nothing.</summary>
	public static class GuardFacts {
		public static MethodInfo FixtureMethod => typeof(FixtureWorker).GetMethod(nameof(FixtureWorker.Run))!;

		public static string Signature(MethodBase method) {
			var returnType = method is MethodInfo info ? TypeName(info.ReturnType) : "System.Void";
			return returnType + " " + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
		}

		public static string IlSha256(MethodBase method) {
			var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
		}

		static string TypeName(Type type) => type.FullName ?? type.Name;

		public static IDictionary<string, string> HookLines(string hookId, string kind) => HookLines(FixtureMethod, hookId, kind);

		public static IDictionary<string, string> HookLines(MethodInfo method, string hookId, string kind) {
			return new Dictionary<string, string>(StringComparer.Ordinal) {
				["hook_id"] = hookId,
				["hook_kind"] = kind,
				["hook_assembly"] = method.Module.Assembly.GetName().Name!,
				["hook_type"] = method.DeclaringType!.FullName!,
				["hook_method"] = method.Name,
				["hook_module_mvid"] = method.Module.ModuleVersionId.ToString("D"),
				["hook_metadata_token"] = unchecked((uint)method.MetadataToken).ToString(CultureInfo.InvariantCulture),
				["hook_declaring_type"] = method.DeclaringType!.FullName!,
				["hook_method_signature"] = Signature(method),
				["hook_il_sha256"] = IlSha256(method),
			};
		}
	}

	/// <summary>Builds the bounded key/value initialization string the bootstrap accepts.</summary>
	public sealed class ParameterBuilder {
		readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

		public static ParameterBuilder ForCurrentProcess() {
			using (var process = System.Diagnostics.Process.GetCurrentProcess()) {
				var builder = new ParameterBuilder();
				builder.values["host_id"] = "hooklab-bootstrap-tests";
				builder.values["image_path"] = process.MainModule!.FileName;
				builder.values["process_id"] = process.Id.ToString(CultureInfo.InvariantCulture);
				builder.values["process_creation_utc_ticks"] = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
				builder.values["architecture"] = IntPtr.Size == 8 ? "x64" : "x86";
				builder.values["runtime_id"] = System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion();
				builder.values["appdomain_id"] = AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture);
				builder.values["endpoint"] = "pipe";
				return builder;
			}
		}

		public ParameterBuilder With(string key, string value) { values[key] = value; return this; }

		public ParameterBuilder WithHook(string hookId = "fixture-hook", string kind = "Prefix") {
			foreach (var pair in GuardFacts.HookLines(hookId, kind)) values[pair.Key] = pair.Value;
			return this;
		}

		public ParameterBuilder WithHookFor(MethodInfo method, string hookId, string kind = "Prefix") {
			foreach (var pair in GuardFacts.HookLines(method, hookId, kind)) values[pair.Key] = pair.Value;
			return this;
		}

		public override string ToString() {
			var builder = new StringBuilder();
			foreach (var pair in values) builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
			return builder.ToString();
		}
	}

	public static class Report {
		public static string Get(this IDictionary<string, string> report, string key) => report.TryGetValue(key, out var value) ? value : "";

		public static IDictionary<string, string> Parse(string report) {
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var line in report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
				var separator = line.IndexOf('=');
				if (separator <= 0) continue;
				values[line.Substring(0, separator)] = line.Substring(separator + 1);
			}
			return values;
		}
	}
}
