using System;
using System.Collections.Generic;
using System.Linq;

namespace dgSpy.Extension {
	readonly struct HookLabRuntimeIdentity {
		public HookLabRuntimeIdentity(Guid guid,string name) { Guid=guid; Name=name ?? String.Empty; }
		public Guid Guid { get; }
		public string Name { get; }
	}

	static class HookLabTargetEligibility {
		static readonly Guid DotNetFrameworkRuntimeGuid=new Guid("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");
		static readonly Guid CoreClrRuntimeGuid=new Guid("E0B4EB52-D1D9-42AB-B130-028CA31CF9F6");

		public enum Backend { DesktopClrV4,CoreClr }

		public static Backend? SelectBackend(int bitness,string architecture,IEnumerable<HookLabRuntimeIdentity> runtimes) {
			if(bitness!=64 || !String.Equals(architecture,"X64",StringComparison.OrdinalIgnoreCase)) return null;
			var attached=(runtimes ?? throw new ArgumentNullException(nameof(runtimes))).ToArray();
			if(attached.Any(runtime=>runtime.Guid==DotNetFrameworkRuntimeGuid && runtime.Name.StartsWith("CLR v4.",StringComparison.OrdinalIgnoreCase))) return Backend.DesktopClrV4;
			if(attached.Any(runtime=>runtime.Guid==CoreClrRuntimeGuid)) return Backend.CoreClr;
			return null;
		}

		public static string? UnsupportedReason(int bitness,string architecture,IEnumerable<HookLabRuntimeIdentity> runtimes) {
			if(bitness!=64 || !String.Equals(architecture,"X64",StringComparison.OrdinalIgnoreCase))
				return "HookLab currently supports x64 desktop CLR v4 and CoreCLR targets; the attached process architecture is "+architecture+" ("+bitness+"-bit).";
			var attached=(runtimes ?? throw new ArgumentNullException(nameof(runtimes))).ToArray();
			if(SelectBackend(bitness,architecture,attached) is not null) return null;
			var names=attached.Select(runtime=>String.IsNullOrWhiteSpace(runtime.Name)?runtime.Guid.ToString("D"):runtime.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			return "HookLab currently supports x64 desktop CLR v4 and CoreCLR targets; the attached process exposes "+(names.Length==0?"no managed runtime":String.Join(", ",names))+".";
		}
	}
}
