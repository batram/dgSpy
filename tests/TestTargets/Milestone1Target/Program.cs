using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace Milestone1Target {
	interface IWorker { int Run(int value); }
	class BaseWorker { public virtual int Run(int value) => value; }
	sealed class Worker : BaseWorker, IWorker { public override int Run(int value) => value + 7; }

	static class Program {
		static volatile bool keepRunning = true;

		static void Main(string[] commandLine) {
			Console.WriteLine("PID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
			Console.WriteLine("TOKEN=" + typeof(Program).GetMethod(nameof(Tick), BindingFlags.Static | BindingFlags.NonPublic).MetadataToken);
			Console.Out.Flush();
			var exitAfterMs=ReadInt(commandLine,"--exit-after-ms");
			var exitCode=ReadInt(commandLine,"--exit-code");
			if (exitAfterMs.HasValue) new Thread(()=>{ Thread.Sleep(exitAfterMs.Value); Environment.Exit(exitCode ?? 0); }) { IsBackground=true }.Start();
			// Two modules with no file on disk, because the only real one anyone had seen was on a live
			// UCH stack and the CorDebug fixture had none: one loaded from bytes, one emitted at runtime.
			// Both call back into this assembly, so a breakpoint in the callee leaves a path-less module's
			// frame on the stack — the case set_breakpoint has to refuse rather than never bind.
			var inMemory=LoadInMemoryTrampoline();
			var emitted=EmitDynamicTrampoline();
			while (keepRunning) {
				Tick(41);
				// Both round trips are microseconds against Tick's 100ms sleep, so a bare pause still
				// lands in Tick essentially always; the other checks select their frame with a breakpoint.
				inMemory(ViaInMemory,41);
				emitted(ViaDynamic,41);
			}
		}

		/// <summary>Assembly.Load(byte[]) leaves Location empty, so the runtime reports a module with no
		/// file at all. The image is a normal compiler output carried as a resource rather than something
		/// generated here, which is what a packed or mod-loaded assembly actually looks like.</summary>
		static Func<Func<int,int>,int,int> LoadInMemoryTrampoline() {
			byte[] image;
			using (var stream=typeof(Program).Assembly.GetManifestResourceStream("InMemoryPayload.dll")) {
				image=new byte[stream.Length];
				for (var read=0;read<image.Length;) read+=stream.Read(image,read,image.Length-read);
			}
			var method=Assembly.Load(image).GetType("InMemoryPayload.Trampoline").GetMethod("Call");
			return (Func<Func<int,int>,int,int>)Delegate.CreateDelegate(typeof(Func<Func<int,int>,int,int>),method);
		}

		/// <summary>A genuinely dynamic module: Reflection.Emit with AssemblyBuilderAccess.Run, whose
		/// metadata the runtime publishes to the debugger incrementally rather than from any image.</summary>
		static Func<Func<int,int>,int,int> EmitDynamicTrampoline() {
			var assembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("DgSpyDynamicPayload"),AssemblyBuilderAccess.Run);
			var module=assembly.DefineDynamicModule("DgSpyDynamicPayload");
			var type=module.DefineType("DgSpyDynamicPayload.Trampoline",TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
			var method=type.DefineMethod("Call",MethodAttributes.Public | MethodAttributes.Static,typeof(int),new[]{ typeof(Func<int,int>),typeof(int) });
			// A dynamic assembly carries no DebuggableAttribute, so the JIT optimizes it and inlines a
			// body this small straight into the caller — leaving no frame for the module at all, which
			// is the one thing this fixture exists to produce.
			method.SetImplementationFlags(MethodImplAttributes.NoInlining);
			var il=method.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Callvirt,typeof(Func<int,int>).GetMethod("Invoke"));
			il.Emit(OpCodes.Ret);
			return (Func<Func<int,int>,int,int>)Delegate.CreateDelegate(typeof(Func<Func<int,int>,int,int>),type.CreateType().GetMethod("Call"));
		}

		static int? ReadInt(string[] commandLine,string name) {
			for (var index=0;index+1<commandLine.Length;index++)
				if (commandLine[index]==name && int.TryParse(commandLine[index+1],out var value)) return value;
			return null;
		}

		static int Tick(int input) {
			int answer = input + 1;
			string label = "dgSpy-milestone-1";
			answer = UseWorker(new Worker(),answer);
			Thread.Sleep(100);
			return answer + label.Length;
		}

		static int UseWorker(IWorker worker,int value) {
			string searchableText = "phase-six-text-search-fixture";
			return worker.Run(value) + searchableText.Length;
		}

		// Called only through the in-memory module, so a breakpoint here puts that module's frame at
		// frame index 1. Same for ViaDynamic and the emitted module: one call site each, no ambiguity.
		static int ViaInMemory(int input) {
			int reachedThroughInMemoryModule = input + 2;
			return reachedThroughInMemoryModule;
		}

		static int ViaDynamic(int input) {
			int reachedThroughDynamicModule = input + 3;
			return reachedThroughDynamicModule;
		}
	}
}
