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
	/// mentions - compiler creation, compile, patch-engine load, patch install - happen inside the probe,
	/// past the boundary where the bootstrap can still tell them apart, and naming them here would be a
	/// label rather than a fact.</para></summary>
	static class ResidentStages {
		/// <summary>Parsing and validating the initialization parameters.</summary>
		internal const string Parameters = "parameters";
		/// <summary>Reading the payload matrix and verifying each payload's digest as it is loaded.</summary>
		internal const string PayloadVerify = "payload_verify";
		/// <summary>Proving every payload identity binds to the verified embedded copy through the CLR
		/// binder - the closure, not just the files.</summary>
		internal const string DependencyResolution = "dependency_resolution";
		/// <summary>Making the payload graph resident: probe initialization, backend inventory, patch
		/// engine, and the control endpoint.</summary>
		internal const string ResidencyCommit = "residency_commit";
		/// <summary>Running the prepared behavior - the hook, if one was supplied.</summary>
		internal const string BehaviorCommit = "behavior_commit";
		/// <summary>Releasing the endpoint and removing this bootstrap's hooks.</summary>
		internal const string Retirement = "retirement";
		/// <summary>Refused before doing anything, because the resident is already started or a previous
		/// attempt's cleanup is still outstanding.</summary>
		internal const string Precondition = "precondition";

		internal static readonly string[] All = {
			Parameters, PayloadVerify, DependencyResolution, ResidencyCommit, BehaviorCommit, Retirement, Precondition,
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
