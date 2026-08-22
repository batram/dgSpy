using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HookLab.Contracts;

namespace HookLab.Probe.CorDebug.Patching {
	public sealed class GuardMismatchException : InvalidOperationException {
		public GuardMismatchException(string guardName, string expected, string actual)
			: base($"Guard '{guardName}' mismatch. Expected '{expected}', actual '{actual}'.") { GuardName = guardName; }
		public string GuardName { get; }
	}

	public static class MethodGuards {
		public static void ValidateTarget(TargetIdentity expected, TargetIdentity actual) {
			if (expected == null) throw new ArgumentNullException(nameof(expected));
			if (actual == null) throw new ArgumentNullException(nameof(actual));
			Check("host_id", expected.HostId, actual.HostId);
			Check("image_path", expected.ImagePath, actual.ImagePath);
			Check("process_id", expected.ProcessId.ToString(), actual.ProcessId.ToString());
			Check("process_creation_time_utc", expected.ProcessCreationTimeUtc.ToUniversalTime().Ticks.ToString(), actual.ProcessCreationTimeUtc.ToUniversalTime().Ticks.ToString());
			Check("architecture", expected.Architecture, actual.Architecture);
			Check("runtime_id", expected.RuntimeId, actual.RuntimeId);
			Check("appdomain_id", expected.AppDomainId, actual.AppDomainId);
		}

		public static void ValidateMethod(MethodBase method, MethodGuard guard) {
			if (method == null) throw new ArgumentNullException(nameof(method));
			if (guard == null) throw new ArgumentNullException(nameof(guard));
			Check("module_mvid", guard.ModuleMvid.ToString("D"), method.Module.ModuleVersionId.ToString("D"));
			Check("metadata_token", guard.MetadataToken.ToString(), unchecked((uint)method.MetadataToken).ToString());
			CheckMethodIdentity("declaring_type", guard.DeclaringType, method.DeclaringType?.FullName ?? "");
			CheckMethodIdentity("method_signature", guard.MethodSignature, Signature(method));
			Check("il_sha256", NormalizeHash(guard.IlSha256), IlSha256(method));
		}

		public static string Signature(MethodBase method) {
			var returnType = method is MethodInfo info ? TypeName(info.ReturnType) : "System.Void";
			return returnType + " " + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
		}

		public static string IlSha256(MethodBase method) {
			var bytes = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
			using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
		}

		static string TypeName(Type type) => type.FullName ?? type.Name;
		static string NormalizeHash(string value) => value.Replace("-", "").ToLowerInvariant();
		static void Check(string name, string expected, string actual) {
			if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new GuardMismatchException(name, expected, actual);
		}
		static void CheckMethodIdentity(string name, string expected, string actual) {
			if (!MethodIdentityText.Equivalent(expected, actual)) throw new GuardMismatchException(name, expected, actual);
		}
	}
}
