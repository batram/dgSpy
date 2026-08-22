using System;
using System.Linq;
using System.Reflection;

namespace HookLab.Bootstrap {
	/// <summary>An AppDomain-wide, type-identity-independent first-writer gate. A bootstrap loaded from
	/// bytes has separate statics even when its assembly identity is identical; the AppDomain data slot
	/// and interned lock are shared across those assembly instances and prevent parallel generations.</summary>
	static class ResidentGenerationRendezvous {
		const string Slot="HookLab.Resident.GenerationOwner.v1";
		static readonly object Marker=new object();
		internal static void Claim() {
			lock(String.Intern(Slot)) {
				var existing=AppDomain.CurrentDomain.GetData(Slot);
				if(existing!=null&&!Object.ReferenceEquals(existing,Marker)) throw new InvalidOperationException("resident_generation_present: another HookLab bootstrap generation already owns this AppDomain.");
				var current=typeof(ResidentGenerationRendezvous).Assembly;
				var foreign=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value=>!Object.ReferenceEquals(value,current)&&String.Equals(value.GetName().Name,"HookLab.Bootstrap",StringComparison.Ordinal));
				if(foreign!=null) throw new InvalidOperationException("resident_generation_present: "+Describe(foreign)+" is already loaded in this AppDomain.");
				AppDomain.CurrentDomain.SetData(Slot,Marker);
			}
		}
		static string Describe(Assembly assembly) {
			try { var field=assembly.GetType("HookLab.Bootstrap.ResidentLauncher",false)?.GetField("GenerationIdentity",BindingFlags.Public|BindingFlags.Static); return field?.GetRawConstantValue() as string??assembly.FullName??"HookLab.Bootstrap"; }
			catch { return assembly.FullName??"HookLab.Bootstrap"; }
		}
	}
}
