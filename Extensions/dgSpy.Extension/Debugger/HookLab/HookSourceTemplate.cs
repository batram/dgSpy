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
			var finalizer=template=="Finalizer";
			if(!prefix&&!postfix&&!finalizer) throw new ArgumentException("template must be Prefix, Postfix, PrefixPostfix, or Finalizer.",nameof(template));
			var builder=new StringBuilder("public static class DgSpyGeneratedHook\n{\n");
			if(prefix) AppendMethod(builder,"Prefix",target,includeResult:false,includeState:template=="PrefixPostfix",stateOutput:true);
			if(prefix&&postfix) builder.AppendLine();
			if(postfix) AppendMethod(builder,"Postfix",target,includeResult:target.ReturnType!="System.Void",includeState:template=="PrefixPostfix",stateOutput:false);
			if(finalizer) AppendFinalizer(builder,target);
			return builder.Append("}\n").ToString();
		}
		static void AppendFinalizer(StringBuilder builder,HookTemplateTarget target) {
			var parameters=new List<string>();
			if(!target.IsStatic) parameters.Add(target.DeclaringType+" __instance");
			parameters.AddRange(target.Parameters.Select((parameter,index)=>(String.IsNullOrEmpty(parameter.Modifier)?"":parameter.Modifier+" ")+parameter.TypeName+" @"+ParameterName(parameter.Name,index)));
			parameters.Add("System.Exception __exception");
			builder.Append("    public static System.Exception Finalizer(").Append(String.Join(", ",parameters)).AppendLine(")");
			builder.AppendLine("    {"); builder.AppendLine("        return __exception;"); builder.AppendLine("    }");
		}

		static void AppendMethod(StringBuilder builder,string name,HookTemplateTarget target,bool includeResult,bool includeState,bool stateOutput) {
			var parameters=new List<string>();
			if(!target.IsStatic) parameters.Add(target.DeclaringType+" __instance");
			parameters.AddRange(target.Parameters.Select((parameter,index)=>(String.IsNullOrEmpty(parameter.Modifier)?"":parameter.Modifier+" ")+parameter.TypeName+" @"+ParameterName(parameter.Name,index)));
			if(includeResult) parameters.Add("ref "+target.ReturnType+" __result");
			if(includeState) parameters.Add((stateOutput?"out ":"")+"object __state");
			builder.Append("    public static void ").Append(name).Append('(').Append(String.Join(", ",parameters)).AppendLine(")");
			builder.AppendLine("    {");
			if(includeState&&stateOutput) builder.AppendLine("        __state = null;");
			builder.AppendLine("    }");
		}

		static string ParameterName(string name,int index) {
			if(String.IsNullOrEmpty(name)||!IsIdentifierStart(name[0])||name.Skip(1).Any(character=>!IsIdentifierPart(character))) return "__"+index;
			return name;
		}
		static bool IsIdentifierStart(char value)=>value=='_'||Char.IsLetter(value);
		static bool IsIdentifierPart(char value)=>value=='_'||Char.IsLetterOrDigit(value);
	}
}
