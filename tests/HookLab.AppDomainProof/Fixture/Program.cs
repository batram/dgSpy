using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AppDomainProof.Fixture {
	/// <summary>
	/// A disposable multi-AppDomain target. A plain console application has one domain and would let a
	/// default-domain payload pass while proving nothing, which is exactly the gap the live net48
	/// HookLab smoke has.
	///
	/// Structure mirrors an IIS worker: hosting code in the default domain, application code in a second
	/// one, and mscorlib in both.
	///
	/// This project deliberately does NOT reference AppDomainProof.App, and nothing here calls a method
	/// or reads a type from the returned proxy. Either would load the app assembly into the default
	/// domain and quietly destroy the only discriminator the proof has.
	/// </summary>
	internal static class Program {
		internal const string ApplicationDomainName="dgspy-appdomain-proof";

		static int Main(string[] arguments) {
			var directory=arguments.Length>0?Path.GetFullPath(arguments[0]):AppDomain.CurrentDomain.BaseDirectory;
			Directory.CreateDirectory(directory);
			var setup=new AppDomainSetup { ApplicationBase=AppDomain.CurrentDomain.BaseDirectory };
			var application=AppDomain.CreateDomain(ApplicationDomainName,null,setup);

			// Loads AppDomainProof.App into the new domain only. The constructor argument is how the
			// marker reports where it ran, so the default domain never has to ask it anything.
			var marker=application.CreateInstanceFromAndUnwrap(
				Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"AppDomainProof.App.dll"),
				"AppDomainProof.App.AppMarker",
				false,BindingFlags.Default,null,new object[]{directory},null,null);

			var facts=new StringBuilder();
			facts.Append("pid=").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			facts.Append("default_domain_id=").Append(AppDomain.CurrentDomain.Id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
			facts.Append("default_domain_name=").Append(AppDomain.CurrentDomain.FriendlyName).Append("\r\n");
			facts.Append("application_domain_name=").Append(ApplicationDomainName).Append("\r\n");
			// The control. If this is True the fixture is broken and no later result means anything.
			facts.Append("default_domain_sees_app_assembly=")
				.Append(AppDomain.CurrentDomain.GetAssemblies().Any(assembly=>assembly.GetName().Name=="AppDomainProof.App").ToString())
				.Append("\r\n");
			File.WriteAllText(Path.Combine(directory,"fixture-facts.txt"),facts.ToString(),new UTF8Encoding(false));
			Console.WriteLine(facts.ToString());
			Console.Out.Flush();

			// Stay alive so the injection has something to enter, but never forever: a spike fixture that
			// outlives its run is a process someone has to hunt down later.
			var deadline=DateTime.UtcNow.AddMinutes(3);
			var stop=Path.Combine(directory,"stop.txt");
			while(DateTime.UtcNow<deadline&&!File.Exists(stop)) Thread.Sleep(100);
			GC.KeepAlive(marker);
			return 0;
		}
	}
}
