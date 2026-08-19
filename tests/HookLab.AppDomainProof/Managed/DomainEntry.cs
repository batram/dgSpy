using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AppDomainProof.Payload {
	/// <summary>
	/// The payload the native prototype creates inside a chosen AppDomain.
	///
	/// The work happens in the constructor, because <c>_AppDomain::CreateInstanceFrom</c> runs it in
	/// the target domain and that is all this prototype has to demonstrate. Product code would want a
	/// named entry point and a way to report failure; this is a spike answering one question.
	/// </summary>
	public sealed class DomainEntry : MarshalByRefObject {
		public DomainEntry() {
			var domain=AppDomain.CurrentDomain;
			var directory=domain.BaseDirectory;
			var assemblies=domain.GetAssemblies();
			var report=new StringBuilder();
			report.Append("domain_id=").Append(domain.Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			report.Append("domain_name=").Append(domain.FriendlyName).Append("\r\n");
			report.Append("is_default_domain=").Append(domain.IsDefaultAppDomain().ToString()).Append("\r\n");
			// The discriminator. In the default domain this is False, which is precisely what the live
			// resident reported when it could not find the ASP.NET application assembly.
			report.Append("can_see_app_assembly=")
				.Append(assemblies.Any(assembly=>assembly.GetName().Name=="AppDomainProof.App").ToString())
				.Append("\r\n");
			report.Append("assembly_count=").Append(assemblies.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			try {
				File.WriteAllText(Path.Combine(directory,"domain-proof.txt"),report.ToString(),new UTF8Encoding(false));
			}
			catch(Exception ex) {
				try { File.WriteAllText(Path.Combine(directory,"domain-proof-error.txt"),ex.ToString(),new UTF8Encoding(false)); } catch { }
			}
		}
		public override object InitializeLifetimeService()=>null!;
	}
}
