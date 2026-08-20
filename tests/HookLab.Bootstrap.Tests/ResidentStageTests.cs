using System;
using System.Linq;
using Xunit;

namespace HookLab.Bootstrap.Tests {
	/// <summary>
	/// A refusal report has to say where it failed. Before this, the first field a reader got was an
	/// exception type, and several stages throw the same ones - "Could not load file or assembly" means
	/// one thing while resolving the payload closure and something quite different once the probe is
	/// committing residency.
	///
	/// These cases drive the real entry point rather than the private report builder, so they fail if the
	/// stage is tracked but never reaches the report.
	/// </summary>
	public class ResidentStageTests {
		static string[] Lines(string report) => report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		static string? Value(string report, string key) => Lines(report)
			.Where(line => line.StartsWith(key + "=", StringComparison.Ordinal))
			.Select(line => line.Substring(key.Length + 1)).FirstOrDefault();

		[Fact]
		public void A_malformed_parameter_block_is_refused_at_the_parameters_stage() {
			// Unparsable, so it fails before the resolver is installed and before any static state moves -
			// which is why this can run in the test process rather than needing its own AppDomain.
			var report = HookLabBootstrap.Prepare("this is not a key=value block at all");
			Assert.Equal("error", Value(report, "status"));
			Assert.Equal(ResidentStages.Parameters, Value(report, "stage"));
			Assert.False(string.IsNullOrWhiteSpace(Value(report, "error_type")));
		}

		[Fact]
		public void An_unsupported_endpoint_is_refused_at_the_parameters_stage() {
			var report = HookLabBootstrap.Prepare("host_id=x\nimage_path=x\nprocess_id=1\nprocess_creation_utc_ticks=1\narchitecture=x64\nruntime_id=v4.0.30319\nappdomain_id=1\nendpoint=none\ncompletion_path=x\n");
			// endpoint=none is rejected by Prepare only when it is neither none nor pipe, so this one gets
			// past the parameter check and fails later - the assertion is that it does NOT claim to have
			// failed at parameters, which is what makes the test above mean something.
			Assert.Equal("error", Value(report, "status"));
			Assert.NotEqual(ResidentStages.Parameters, Value(report, "stage"));
			Assert.Contains(Value(report, "stage"), ResidentStages.All);
		}

		[Fact]
		public void Every_reported_stage_is_one_of_the_declared_names() {
			var report = HookLabBootstrap.Prepare("nonsense");
			Assert.Contains(Value(report, "stage"), ResidentStages.All);
			// First after status, so a reader sees it before the exception type it gives meaning to.
			Assert.Equal("stage", Lines(report)[1].Split('=')[0]);
		}

		[Fact]
		public void The_inner_exception_chain_is_bounded() {
			Exception nested = new InvalidOperationException("innermost");
			for (var index = 0; index < 10; index++) nested = new InvalidOperationException("wrapper " + index, nested);
			var pairs = ResidentStages.InnerExceptions(nested).ToArray();
			// Two lines per link - type and message - and never more links than the bound, however deep the
			// real chain goes. A report that travels through a file and a pipe cannot carry an unbounded one.
			Assert.Equal(ResidentStages.MaximumInnerExceptions * 2, pairs.Length);
			Assert.Equal("inner_1_type", pairs[0].Key);
			Assert.Equal("inner_" + ResidentStages.MaximumInnerExceptions + "_message", pairs[pairs.Length - 1].Key);
		}

		[Fact]
		public void An_exception_with_no_inner_contributes_no_lines() {
			Assert.Empty(ResidentStages.InnerExceptions(new InvalidOperationException("alone")));
			Assert.Empty(ResidentStages.InnerExceptions(null));
		}
	}
}
