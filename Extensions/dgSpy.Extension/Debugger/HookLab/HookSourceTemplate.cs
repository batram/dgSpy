using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace dgSpy.Extension {
	sealed class HookTemplateParameter {
		public HookTemplateParameter(string name,string typeName,string modifier) { Name=name; TypeName=typeName; Modifier=modifier; }
		public string Name { get; }
		public string TypeName { get; }
		public string Modifier { get; }
	}

	sealed class HookTemplateTarget {
		public HookTemplateTarget(string declaringType,bool isStatic,string returnType,IReadOnlyList<HookTemplateParameter> parameters) { DeclaringType=declaringType; IsStatic=isStatic; ReturnType=returnType; Parameters=parameters; }
		public string DeclaringType { get; }
		public bool IsStatic { get; }
		public string ReturnType { get; }
		public IReadOnlyList<HookTemplateParameter> Parameters { get; }
	}

	static class HookSourceTemplate {
		public static string Generate(HookTemplateTarget target,string template) {
			if(target is null) throw new ArgumentNullException(nameof(target));
			var prefix=template=="Prefix"||template=="PrefixPostfix";
			var postfix=template=="Postfix"||template=="PrefixPostfix";
			if(!prefix&&!postfix) throw new ArgumentException("template must be Prefix, Postfix, or PrefixPostfix.",nameof(template));
			var builder=new StringBuilder("public static class DgSpyGeneratedHook\n{\n");
			if(prefix) AppendMethod(builder,"Prefix",target,includeResult:false,includeState:template=="PrefixPostfix",stateOutput:true);
			if(prefix&&postfix) builder.AppendLine();
			if(postfix) AppendMethod(builder,"Postfix",target,includeResult:target.ReturnType!="System.Void",includeState:template=="PrefixPostfix",stateOutput:false);
			return builder.Append("}\n").ToString();
		}

		static void AppendMethod(StringBuilder builder,string name,HookTemplateTarget target,bool includeResult,bool includeState,bool stateOutput) {
			var parameters=new List<string>();
			if(!target.IsStatic) parameters.Add(target.DeclaringType+" __instance");
			parameters.AddRange(target.Parameters.Select(parameter=>(String.IsNullOrEmpty(parameter.Modifier)?"":parameter.Modifier+" ")+parameter.TypeName+" @"+parameter.Name));
			if(includeResult) parameters.Add("ref "+target.ReturnType+" __result");
			if(includeState) parameters.Add((stateOutput?"out ":"")+"object __state");
			builder.Append("    public static void ").Append(name).Append('(').Append(String.Join(", ",parameters)).AppendLine(")");
			builder.AppendLine("    {");
			if(includeState&&stateOutput) builder.AppendLine("        __state = null;");
			builder.AppendLine("    }");
		}
	}
}
