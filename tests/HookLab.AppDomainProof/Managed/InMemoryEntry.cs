using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AppDomainProof.Payload {
	/// <summary>
	/// The in-memory half of the subslice 6 prototype.
	///
	/// <see cref="DomainEntry"/> proved native code can enter a chosen AppDomain, but it got there
	/// through <c>_AppDomain::CreateInstanceFrom</c>, which takes a file path and lets the CLR bind the
	/// assembly from disk. Product HookLab does not work that way: the bootstrap is entered by name and
	/// then byte-loads its probe assemblies, which report <c>is_in_memory: true</c>.
	///
	/// So this type is reached differently on purpose. The native prototype reads this assembly's bytes,
	/// hands them to <c>_AppDomain::Load_3</c>, and invokes this method as a static entry point with an
	/// argument - which is the exact shape of <c>NativeEntry.Initialize(parametersPath)</c>.
	/// </summary>
	public static class InMemoryEntry {
		public static int Initialize(string directory) {
			var report=new StringBuilder();
			try {
				var domain=AppDomain.CurrentDomain;
				var self=typeof(InMemoryEntry).Assembly;
				report.Append("domain_id=").Append(domain.Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
				report.Append("domain_name=").Append(domain.FriendlyName).Append("\r\n");
				report.Append("is_default_domain=").Append(domain.IsDefaultAppDomain().ToString()).Append("\r\n");
				// A byte-loaded assembly has no location. This is the assertion that separates "loaded
				// from the bytes we passed" from "the CLR quietly found the same file on disk".
				report.Append("self_location_is_empty=").Append(String.IsNullOrEmpty(self.Location).ToString()).Append("\r\n");
				report.Append("self_name=").Append(self.GetName().Name).Append("\r\n");
				// The same domain discriminator the file-path proof used: an assembly present only in the
				// application domain.
				report.Append("can_see_app_assembly=")
					.Append(domain.GetAssemblies().Any(assembly=>assembly.GetName().Name=="AppDomainProof.App").ToString())
					.Append("\r\n");

				// Now the nested case: a byte-loaded assembly byte-loading another one, inside a secondary
				// domain. This is what HookLab.Bootstrap does with its embedded probe assemblies.
				var embeddedPath=Path.Combine(directory,"AppDomainProof.Embedded.dll");
				var embedded=Assembly.Load(File.ReadAllBytes(embeddedPath));
				var described=embedded.GetType("AppDomainProof.Embedded.EmbeddedMarker")!
					.GetMethod("Describe",BindingFlags.Public|BindingFlags.Static)!
					.Invoke(null,null) as string;
				report.Append("embedded_location_is_empty=").Append(String.IsNullOrEmpty(embedded.Location).ToString()).Append("\r\n");
				report.Append("embedded_says=").Append(described).Append("\r\n");
				report.Append("status=ok\r\n");
			}
			catch(Exception ex) {
				report.Append("status=error\r\n").Append("error=").Append(ex.GetType().FullName).Append(": ").Append(ex.Message.Replace('\r',' ').Replace('\n',' ')).Append("\r\n");
			}
			try { File.WriteAllText(Path.Combine(directory,"in-memory-proof.txt"),report.ToString(),new UTF8Encoding(false)); } catch { }
			// A distinctive value, so the native side can tell "the method ran and returned" from "the
			// invoke reported success but nothing happened".
			return 4242;
		}
	}
}
