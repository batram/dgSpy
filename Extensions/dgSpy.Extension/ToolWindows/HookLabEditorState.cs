using System;

namespace dgSpy.Extension.ToolWindows {
	sealed class HookLabEditorState {
		readonly Func<string,string> generate;
		public HookLabEditorState(string hookId,Func<string,string> generateSource,int revision=1,string? source=null,string template="PrefixPostfix") {
			HookId=hookId ?? throw new ArgumentNullException(nameof(hookId));
			generate=generateSource ?? throw new ArgumentNullException(nameof(generateSource));
			if(revision<=0) throw new ArgumentOutOfRangeException(nameof(revision));
			Revision=revision; Template=template; Source=source ?? generate(template);
		}
		public string HookId { get; set; }
		public string Template { get; private set; }="PrefixPostfix";
		public string Source { get; set; }="";
		public string Diagnostics { get; private set; }="";
		public int Revision { get; }
		public string Kind=>Template=="Postfix" ? "Postfix" : "Prefix";
		public bool Busy { get; private set; }
		public void SelectTemplate(string template) { Template=template; Source=generate(template); Diagnostics=""; }
		public bool TryBegin(out string error) {
			if(Busy) { error="Compilation is already in progress."; return false; }
			if(String.IsNullOrWhiteSpace(HookId)) { error="Hook ID is required."; return false; }
			if(String.IsNullOrWhiteSpace(Source)) { error="Hook source is required."; return false; }
			Busy=true; Diagnostics="Compiling revision "+Revision+"..."; error=""; return true;
		}
		public void Fail(string diagnostics) { Busy=false; Diagnostics=String.IsNullOrWhiteSpace(diagnostics)?"Hook compilation failed.":diagnostics; }
		public void Complete() { Busy=false; Diagnostics="Installed revision "+Revision+"."; }
	}
}
