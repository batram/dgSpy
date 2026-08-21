using System;
using System.Collections.Generic;
using System.Text;

namespace HookLab.Bootstrap {
	/// <summary>The stable names for where resident initialization can fail.
	///
	/// <para>Every one of these was previously recoverable only from an exception type and a message -
	/// "Could not load file or assembly" says nothing about whether the payload graph failed to resolve
	/// or the patch engine did. A stage is the one field a reader needs before any of the others make
	/// sense, so it is reported first and it never varies with the wording of a message.</para>
	///
	/// <para>Deliberately the stages this assembly can actually distinguish. Finer ones the roadmap
	/// mentions - compiler creation, compile, patch install - happen inside the probe, past the boundary
	/// where the bootstrap can still tell them apart, and naming them here would be a label rather than a
	/// fact. Patch-engine load crossed that boundary and became a real stage when the bootstrap started
	/// loading the engine itself, ahead of the first probe type.</para></summary>
	static class ResidentStages {
		/// <summary>Parsing and validating the initialization parameters.</summary>
		internal const string Parameters = "parameters";
		/// <summary>Reading the payload matrix and verifying each payload's digest as it is loaded.</summary>
		internal const string PayloadVerify = "payload_verify";
		/// <summary>Proving every payload identity binds to the verified embedded copy through the CLR
		/// binder - the closure, not just the files.</summary>
		internal const string DependencyResolution = "dependency_resolution";
		/// <summary>Loading the pinned patch engine, before any probe type is prepared. Its own stage
		/// because it is the one step the bootstrap now performs itself rather than watching the probe
		/// perform: a Mono target that cannot load the engine fails here, by name, instead of surfacing as
		/// a type-load failure inside residency commit.</summary>
		internal const string PatchEngineLoad = "patch_engine_load";
		/// <summary>Making the payload graph resident: probe initialization, backend inventory, and the
		/// control endpoint.</summary>
		internal const string ResidencyCommit = "residency_commit";
		/// <summary>Running the prepared behavior - the hook, if one was supplied.</summary>
		internal const string BehaviorCommit = "behavior_commit";
		/// <summary>Releasing the endpoint and removing this bootstrap's hooks.</summary>
		internal const string Retirement = "retirement";
		/// <summary>Refused before doing anything, because the resident is already started or a previous
		/// attempt's cleanup is still outstanding.</summary>
		internal const string Precondition = "precondition";

		/// <summary>Records the stage a resident is entering, beside its completion report, so that a
		/// resident which never returns still says how far it got.
		///
		/// <para>Every other diagnostic here is carried in the report, and a report only exists once the
		/// work has finished. That leaves exactly one failure shape unexplained - the resident that
		/// stopped somewhere and published nothing - and it is not hypothetical: arrival on Mono hung
		/// inside a debugger evaluation with no return value, no exception and no report, and the only
		/// way to learn which stage it died in was to add this.</para>
		///
		/// <para>Appended, not overwritten, so what is left behind is the route rather than the last
		/// signpost. The route is what distinguishes the cases: <see cref="ResidencyCommit"/> is entered
		/// twice - once for the target guard and once for the probe - and a file holding only the last
		/// value cannot say which of the two a resident stopped in.</para>
		///
		/// <para>Best-effort by construction. It runs on the way into each stage, in a target dgSpy does
		/// not own, and a diagnostic that could itself throw would turn a hang into a crash. It writes
		/// only beside a completion path the caller already chose; with no completion path there is
		/// nowhere it would be read from and it does nothing.</para></summary>
		internal static void Trace(string? completionPath, string stage) {
			if (completionPath == null || completionPath.Length == 0) return;
			try { System.IO.File.AppendAllText(completionPath + ".stages", stage + "\n"); }
			catch (Exception) { }
		}

		internal static readonly string[] All = {
			Parameters, PayloadVerify, DependencyResolution, PatchEngineLoad, ResidencyCommit, BehaviorCommit, Retirement, Precondition,
		};

		/// <summary>The exception chain, bounded. Three links is enough to cross the wrappers that
		/// actually occur - a reflection invoke over a loader failure, say - and an unbounded chain in a
		/// report that travels through a file and a pipe is how a diagnostic becomes a denial of
		/// service.</summary>
		internal const int MaximumInnerExceptions = 3;

		internal static IEnumerable<KeyValuePair<string, string>> InnerExceptions(Exception? exception) {
			var index = 0;
			for (var inner = exception?.InnerException; inner != null && index < MaximumInnerExceptions; inner = inner.InnerException) {
				index++;
				yield return new KeyValuePair<string, string>("inner_" + index + "_type", inner.GetType().FullName ?? "Exception");
				yield return new KeyValuePair<string, string>("inner_" + index + "_message", inner.Message);
			}
		}
	}
}
