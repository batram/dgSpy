using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HookLab.ApplyOnceTarget {
	internal static class Program {
		static int Main(string[] arguments) {
			if(arguments.Length!=3) return 2;
			var facts=Path.GetFullPath(arguments[0]); var signal=Path.GetFullPath(arguments[1]); var result=Path.GetFullPath(arguments[2]);
			try {
				WriteFacts(facts);
				var timeout=DateTime.UtcNow.AddSeconds(20);
				while(!File.Exists(signal)&&DateTime.UtcNow<timeout) Thread.Sleep(10);
				if(!File.Exists(signal)) throw new TimeoutException("Signal was not received.");
				ITarget target=new Target();
				File.WriteAllText(result,"alpha="+target.Alpha(5).ToString(CultureInfo.InvariantCulture)+"\nbeta="+target.Beta(5).ToString(CultureInfo.InvariantCulture)+"\n",new UTF8Encoding(false));
				return 0;
			}
			catch(Exception ex) { File.WriteAllText(result,"error="+ex.GetType().FullName+":"+ex.Message,new UTF8Encoding(false)); return 1; }
		}

		static void WriteFacts(string path) {
			var methods=new[]{typeof(Target).GetMethod(nameof(Target.Alpha))!,typeof(Target).GetMethod(nameof(Target.Beta))!};
			var lines=methods.SelectMany(method=>new[]{
				method.Name+".assembly="+method.Module.Assembly.GetName().Name,
				method.Name+".mvid="+method.Module.ModuleVersionId.ToString("D"),
				method.Name+".type="+method.DeclaringType!.FullName,
				method.Name+".token="+unchecked((uint)method.MetadataToken).ToString(CultureInfo.InvariantCulture),
				method.Name+".signature="+Signature(method),
				method.Name+".il_sha256="+IlSha256(method)
			});
			File.WriteAllText(path,String.Join("\n",lines)+"\n",new UTF8Encoding(false));
		}

		static string Signature(MethodInfo method)=>(method.ReturnType.FullName??method.ReturnType.Name)+" "+method.Name+"("+String.Join(",",method.GetParameters().Select(parameter=>parameter.ParameterType.FullName??parameter.ParameterType.Name))+")";
		static string IlSha256(MethodInfo method) { using(var sha=SHA256.Create()) return String.Concat(sha.ComputeHash(method.GetMethodBody()!.GetILAsByteArray()).Select(value=>value.ToString("x2",CultureInfo.InvariantCulture))); }
	}

	public interface ITarget { int Alpha(int value); int Beta(int value); }
	public sealed class Target : ITarget {
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public int Alpha(int value)=>value+1;
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public int Beta(int value)=>value*2;
	}
}
