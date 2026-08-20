using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppDomainProof.App {
	/// <summary>
	/// Loaded into the secondary AppDomain and never into the default one, so that "can this payload see
	/// AppDomainProof.App" is a real question about where the payload is executing rather than something
	/// it reports about itself.
	///
	/// This stands in for the ASP.NET App_Web_* assembly whose absence produced the failure that
	/// motivated the target environment contract, subslice 6.
	///
	/// The constructor does the reporting, and nothing in the default domain ever calls a method on the
	/// proxy. That is not style: a single <c>proxy.GetType()</c> in the fixture loaded this assembly into
	/// the default domain and made the control read
	/// <c>default_domain_sees_app_assembly=True</c>, which would have made every later result meaningless.
	/// </summary>
	public sealed class AppMarker : MarshalByRefObject {
		public AppMarker(string directory) {
			var domain=AppDomain.CurrentDomain;
			var text=new StringBuilder();
			text.Append("marker_domain_id=").Append(domain.Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			text.Append("marker_domain_name=").Append(domain.FriendlyName).Append("\r\n");
			text.Append("marker_is_default=").Append(domain.IsDefaultAppDomain().ToString()).Append("\r\n");
			File.WriteAllText(Path.Combine(directory,"marker-facts.txt"),text.ToString(),new UTF8Encoding(false));
		}
		// The fixture holds this object for the life of the process, which keeps the domain and this
		// assembly loaded while the injection happens.
		public override object InitializeLifetimeService()=>null!;
	}
}
