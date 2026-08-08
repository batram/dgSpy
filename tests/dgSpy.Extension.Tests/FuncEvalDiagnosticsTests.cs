using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>Verified live against a CorDebug target on 2026-08-08, which is what these strings encode.
/// Against <c>DgSpyBlindTest.Telemetry.ComputeReading(5)</c>, a pure static method over ints:
/// <list type="bullet">
/// <item>no flags — "This expression causes side effects and will not be evaluated"</item>
/// <item>allow_func_eval only — byte-for-byte the same message, because the side-effects gate is checked
/// first. This is the step that made an agent conclude the override flag was ignored.</item>
/// <item>allow_side_effects only — "Implicit function evaluation is turned off by the user"</item>
/// <item>both — 696, evaluated correctly</item>
/// </list>
/// And <c>System.DateTime.Now.Ticks</c>, a property getter, evaluates under allow_func_eval alone with
/// <c>causes_side_effects: false</c> — which is the proof that the two gates are genuinely independent
/// and that neither flag should be made to imply the other.</summary>
public sealed class FuncEvalDiagnosticsTests {
	const string SideEffects="This expression causes side effects and will not be evaluated";
	const string FuncEvalOff="Implicit function evaluation is turned off by the user";
	const string UnsafePoint="Can't func-eval when the thread is at an unsafe point. Step once or run until a breakpoint hits";

	[Fact]
	public void The_side_effects_refusal_names_allow_side_effects_and_denies_that_func_eval_covers_it() {
		var recovery=FuncEvalDiagnostics.Recovery(SideEffects,sideEffectsGrantable:true);

		Assert.NotNull(recovery);
		Assert.Contains("allow_side_effects",recovery,StringComparison.Ordinal);
		// The whole point: it must say out loud that the flag the caller already tried is the wrong one,
		// rather than leaving them to infer that overrides do not work here.
		Assert.Contains("allow_func_eval does not imply it",recovery,StringComparison.Ordinal);
		Assert.Contains("invoke_method",recovery,StringComparison.Ordinal);
	}

	/// <summary>The reconciliation. A second agent reported the opposite result for "the same" call, and
	/// both reports were accurate: dnSpy's "ac" format specifier opens the side-effects gate on its own,
	/// before dgSpy's options are consulted, so `Telemetry.ComputeReading(700), ac` with allow_func_eval
	/// alone returns a value while the identical call without ", ac" is refused. Verified live on
	/// 2026-08-08 against one paused frame, four calls apart. A caller who has seen a peer succeed needs
	/// the refusal to explain that, or they will again conclude the flag is broken.</summary>
	[Fact]
	public void The_side_effects_refusal_explains_why_the_same_call_can_succeed_for_someone_else() {
		var recovery=FuncEvalDiagnostics.Recovery(SideEffects,sideEffectsGrantable:true);

		Assert.NotNull(recovery);
		Assert.Contains("ac",recovery,StringComparison.Ordinal);
		Assert.Contains("succeed for another caller with allow_func_eval alone",recovery,StringComparison.Ordinal);
	}

	/// <summary>get_members, get_frame, get_autos, list_watches and get_exception evaluate with side
	/// effects permanently off and expose no allow_side_effects argument. Telling their callers to pass it
	/// would be a second wrong remedy, so they get pointed at a tool that can.</summary>
	[Fact]
	public void A_tool_without_the_flag_is_not_told_to_pass_it() {
		var recovery=FuncEvalDiagnostics.Recovery(SideEffects,sideEffectsGrantable:false);

		Assert.NotNull(recovery);
		Assert.Contains("no allow_side_effects argument",recovery,StringComparison.Ordinal);
		Assert.Contains("evaluate",recovery,StringComparison.Ordinal);
		Assert.Contains("invoke_method",recovery,StringComparison.Ordinal);
	}

	[Fact]
	public void The_func_eval_refusal_names_allow_func_eval_and_warns_about_the_second_gate() {
		var recovery=FuncEvalDiagnostics.Recovery(FuncEvalOff);

		Assert.NotNull(recovery);
		Assert.Contains("allow_func_eval:true",recovery,StringComparison.Ordinal);
		Assert.Contains("allow_side_effects:true",recovery,StringComparison.Ordinal);
	}

	/// <summary>The refusal this class was originally written for still overrides dnSpy's "step once"
	/// advice, which cannot work on an idle process.</summary>
	[Fact]
	public void The_unsafe_point_refusal_still_overrides_the_engines_step_advice() {
		var recovery=FuncEvalDiagnostics.Recovery(UnsafePoint);

		Assert.NotNull(recovery);
		Assert.Contains("Do not step",recovery,StringComparison.Ordinal);
		Assert.Contains("include_evaluability=true",recovery,StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("error CS0103: The name 'nope' does not exist in the current context")]
	[InlineData("Internal debugger error")]
	public void An_error_dgSpy_has_nothing_better_to_say_about_gets_no_invented_advice(string? error) =>
		Assert.Null(FuncEvalDiagnostics.Recovery(error));

	[Theory]
	[InlineData("error CS0571: cannot explicitly call operator or accessor",true)]
	[InlineData("error BC30456: 'Nope' is not a member",true)]
	[InlineData(SideEffects,false)]
	[InlineData(FuncEvalOff,false)]
	[InlineData(null,false)]
	public void A_gate_refusal_is_never_reported_as_a_compiler_error(string? error,bool expected) =>
		Assert.Equal(expected,FuncEvalDiagnostics.IsCompilerError(error));
}
