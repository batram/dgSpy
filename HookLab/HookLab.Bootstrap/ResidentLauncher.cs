using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace HookLab.Bootstrap {
	/// <summary>Stable host-discoverable identity for the one resident bootstrap generation.</summary>
	public static class ResidentLauncher {
		public const string GenerationIdentity = "HookLab.Bootstrap.Stage1.v1";
		static readonly object Gate = new object();
		static BootstrapParameters? parameters;
		static Thread? worker;
		static int workerStarts;
		[ThreadStatic] static bool committing;
		internal static int CommitFileIoCount;
		internal static int CommitAssemblyLoadCount;
		internal static int CommitPatchInstallCount;

		internal static void Prepare(BootstrapParameters value) {
			lock (Gate) {
				if (parameters != null) throw new InvalidOperationException("This resident generation is already prepared.");
				parameters = value;
				AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
				RuntimeHelpers.PrepareMethod(typeof(ResidentLauncher).GetMethod(nameof(Commit), BindingFlags.Public | BindingFlags.Static)!.MethodHandle);
			}
		}
		static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs eventArgs) { NoteAssemblyLoad(); }

		/// <summary>Starts the prepared worker once. This method intentionally does no payload work.</summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Commit() {
			var stopwatch = Stopwatch.StartNew();
			committing = true;
			try {
				lock (Gate) {
					if (parameters == null) return "status=error\nerror_type=not_prepared\nresidency_commit=not_started\nbehavior_commit=not_started\nprototype_compromises=endpoint_none,identity_partly_self_asserted,no_residency_rollback\n";
					if (worker != null) return "status=ok\nworker_started=false\nresidency_commit=completed\nbehavior_commit=asynchronous_pending_or_completed\nprototype_compromises=endpoint_none,identity_partly_self_asserted,no_residency_rollback\ncommit_elapsed_ticks=" + stopwatch.ElapsedTicks + "\n";
					worker = new Thread(Work) { IsBackground = true, Name = "HookLab bootstrap worker" };
					workerStarts++;
					worker.Start(parameters);
					return "status=ok\nworker_started=true\nresidency_commit=completed\nbehavior_commit=asynchronous_pending_or_completed\nprototype_compromises=endpoint_none,identity_partly_self_asserted,no_residency_rollback\ncommit_elapsed_ticks=" + stopwatch.ElapsedTicks + "\n";
				}
			}
			finally { committing = false; }
		}

		static void Work(object? state) {
			var value = (BootstrapParameters)state!;
			string report;
			try { report = HookLabBootstrap.RunPrepared(value); }
			catch (Exception ex) { report = HookLabBootstrap.WorkerError(ex); }
			WriteCompletion(value.CompletionPath!, report);
		}

		internal static void NoteAssemblyLoad() { if (committing) CommitAssemblyLoadCount++; }
		internal static void NotePatchInstall() { if (committing) CommitPatchInstallCount++; }
		static void WriteCompletion(string path, string report) {
			if (committing) CommitFileIoCount++;
			var temporary = path + ".tmp";
			File.WriteAllText(temporary, report);
			if (File.Exists(path)) File.Delete(path);
			File.Move(temporary, path);
		}

		internal static int WorkerStarts { get { lock (Gate) return workerStarts; } }
	}
}
