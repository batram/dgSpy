using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HookLabInteractiveTarget {
	static class Program {
		[STAThread]
		static void Main() {
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new DemoWindow());
		}

		public static int Calculate(int input) {
			Debug.WriteLine("HookLab demo Calculate("+input+")");
			return input+1;
		}

		public static int ThrowOrReturn() {
			Debug.WriteLine("HookLab demo ThrowOrReturn throwing");
			throw new InvalidOperationException("The unhooked exception path ran.");
		}
	}

	sealed class DemoWindow : Form {
		readonly NumericUpDown input=new NumericUpDown { Name="HookInput",AccessibleName="Hook input",Minimum=-100000,Maximum=100000,Value=41,Width=150,Font=new Font("Segoe UI",16) };
		readonly Label result=new Label { Name="HookResult",AccessibleName="Hook result",AutoSize=true,Font=new Font("Segoe UI Semibold",24),Text="Result: not called yet" };
		readonly Label calls=new Label { Name="CallCount",AccessibleName="Call count",AutoSize=true,Font=new Font("Segoe UI",11),Text="Calls: 0" };
		readonly Label exceptionResult=new Label { Name="ExceptionResult",AccessibleName="Exception result",AutoSize=true,Font=new Font("Segoe UI Semibold",18),Text="Exception target: not called yet" };
		int callCount;

		public DemoWindow() {
			Text="HookLab Interactive Target";
			ClientSize=new Size(680,430);
			StartPosition=FormStartPosition.CenterScreen;
			var explanation=new Label { AutoSize=false,Width=560,Height=55,Text="Change the input and call Program.Calculate(int). Without a hook, the result is input + 1. A HookLab Prefix can visibly replace it while this window keeps running.",Font=new Font("Segoe UI",11) };
			var button=new Button { Name="CallHookTarget",AccessibleName="Call hook target",AutoSize=true,Text="Call hook target",Font=new Font("Segoe UI Semibold",13),Padding=new Padding(12,6,12,6) };
			button.Click+=(sender,args)=>CallTarget();
			var exceptionButton=new Button { Name="CallExceptionTarget",AccessibleName="Call exception target",AutoSize=true,Text="Call exception target",Font=new Font("Segoe UI Semibold",13),Padding=new Padding(12,6,12,6) };
			exceptionButton.Click+=(sender,args)=>CallExceptionTarget();
			var panel=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24) };
			panel.Controls.Add(explanation);
			panel.Controls.Add(new Label { AutoSize=true,Text="Input",Font=new Font("Segoe UI",11) });
			panel.Controls.Add(input);
			panel.Controls.Add(button);
			panel.Controls.Add(result);
			panel.Controls.Add(calls);
			panel.Controls.Add(exceptionButton);
			panel.Controls.Add(exceptionResult);
			Controls.Add(panel);
			AcceptButton=button;
		}

		void CallTarget() {
			var value=(int)input.Value;
			var calculated=Program.Calculate(value);
			callCount++;
			result.Text="Result: "+calculated;
			calls.Text="Calls: "+callCount+"  |  Last input: "+value;
			Debug.WriteLine("HookLab demo visible result="+calculated+" calls="+callCount);
		}

		void CallExceptionTarget() {
			try {
				exceptionResult.Text="Exception target returned: "+Program.ThrowOrReturn();
			}
			catch(Exception ex) {
				exceptionResult.Text="Exception target threw: "+ex.GetType().Name;
			}
		}
	}
}
