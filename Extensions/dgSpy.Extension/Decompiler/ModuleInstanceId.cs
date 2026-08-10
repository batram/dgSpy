using System;
using System.Globalization;

namespace dgSpy.Extension {
	/// <summary>An opaque, session-scoped identity for one loaded debugger module. Display names and
	/// paths are deliberately absent: neither is an identity when one assembly is loaded into several
	/// app domains or two assemblies share a filename.</summary>
	static class ModuleInstanceId {
		const string Prefix="dm1";

		public static string Create(string sessionId,int processId,Guid runtimeGuid,int? appDomainId,int order) =>
			string.Join(":",Prefix,sessionId,processId.ToString(CultureInfo.InvariantCulture),runtimeGuid.ToString("N"),
				(appDomainId ?? -1).ToString(CultureInfo.InvariantCulture),order.ToString(CultureInfo.InvariantCulture));

		public static bool TryParse(string? value,out string sessionId,out int processId,out Guid runtimeGuid,out int? appDomainId,out int order) {
			sessionId=""; processId=0; runtimeGuid=Guid.Empty; appDomainId=null; order=0;
			if (string.IsNullOrEmpty(value)) return false;
			var parts=value!.Split(':');
			if (parts.Length!=6 || parts[0]!=Prefix || !Guid.TryParseExact(parts[1],"N",out _)
				|| !int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out processId)
				|| !Guid.TryParseExact(parts[3],"N",out runtimeGuid)
				|| !int.TryParse(parts[4],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var parsedAppDomain)
				|| !int.TryParse(parts[5],NumberStyles.None,CultureInfo.InvariantCulture,out order)
				|| processId<=0 || runtimeGuid==Guid.Empty || order<=0 || parsedAppDomain< -1) return false;
			sessionId=parts[1];
			appDomainId=parsedAppDomain<0 ? null : parsedAppDomain;
			return true;
		}
	}
}
