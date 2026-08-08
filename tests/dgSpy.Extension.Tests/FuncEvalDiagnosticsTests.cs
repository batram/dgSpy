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

	/// <summary>Two blind agents hit this refusal, were told to set a breakpoint by hand, and never found
	/// the composites that already do the whole breakpoint/continue/wait/cleanup round. The tool names
	/// have to appear verbatim: an agent that has not browsed the full tool list cannot infer them.</summary>
	[Fact]
	public void The_unsafe_point_refusal_names_both_run_to_workflows_by_tool_name() {
		var recovery=FuncEvalDiagnostics.Recovery(UnsafePoint);

		Assert.NotNull(recovery);
		Assert.Contains("run_to_method",recovery,StringComparison.Ordinal);
		Assert.Contains("run_to_location",recovery,StringComparison.Ordinal);
	}

	/// <summary>The two halves that stop a run-to workflow from being read as magic. It cannot make
	/// unreachable code execute, and reaching a point that needs a stimulus means calling first and
	/// triggering second — the opposite order silently misses the stop.</summary>
	[Fact]
	public void The_unsafe_point_refusal_bounds_what_a_run_to_workflow_can_do() {
		var recovery=FuncEvalDiagnostics.Recovery(UnsafePoint);

		Assert.NotNull(recovery);
		Assert.Contains("Neither makes unreachable code execute",recovery,StringComparison.Ordinal);
		Assert.Contains("prepare it first",recovery,StringComparison.Ordinal);
		Assert.Contains("issue the run_to call first and trigger the stimulus",recovery,StringComparison.Ordinal);
	}

	/// <summary>The second failure this class grew for: `PuzzleBox.Gate.Stage` from a Thread.Sleep frame
	/// is CS0103, and the agent that saw it kept re-qualifying a name that was already fully qualified.
	/// The answer has to name the module that compiled the expression and the exact arguments that select
	/// a different one.</summary>
	[Fact]
	public void The_name_not_found_error_names_the_frames_module_and_how_to_pick_another_frame() {
		var recovery=FuncEvalDiagnostics.Recovery("error CS0103: The name 'PuzzleBox' does not exist in the current context","mscorlib.dll","C:\\Windows\\...\\mscorlib.dll");

		Assert.NotNull(recovery);
		Assert.Contains("mscorlib.dll",recovery,StringComparison.Ordinal);
		Assert.Contains("get_callstack",recovery,StringComparison.Ordinal);
		Assert.Contains("thread_id",recovery,StringComparison.Ordinal);
		Assert.Contains("frame_index",recovery,StringComparison.Ordinal);
	}

	/// <summary>Qualification is the remedy an agent reaches for on its own, and it does not work. Saying
	/// so explicitly is the difference between one retry and a budget spent on longer names.</summary>
	[Fact]
	public void The_name_not_found_error_says_a_fully_qualified_name_is_not_the_remedy() {
		var recovery=FuncEvalDiagnostics.FrameContextAdvice("error CS0103: The name 'PuzzleBox' does not exist in the current context","PuzzleBox.dll");

		Assert.NotNull(recovery);
		Assert.Contains("fully qualified name does not help",recovery,StringComparison.Ordinal);
		Assert.Contains("Namespace.Type.Member",recovery,StringComparison.Ordinal);
	}

	/// <summary>VB reports the same condition under its own number, and it has always been treated as the
	/// same condition by the addressable-names path.</summary>
	[Theory]
	[InlineData("error CS0103: The name 'x' does not exist in the current context")]
	[InlineData("error BC30451: 'x' is not declared")]
	public void Both_languages_name_not_found_errors_get_frame_context_advice(string error) =>
		Assert.NotNull(FuncEvalDiagnostics.FrameContextAdvice(error,"Milestone1Target.exe"));

	/// <summary>A frame with no module still has to produce a sentence a caller can read, not a blank or
	/// a dangling "this frame's module is .".</summary>
	[Fact]
	public void A_frame_with_no_module_name_falls_back_to_the_path_and_then_to_a_clear_label() {
		Assert.Contains("C:\\target\\App.exe",FuncEvalDiagnostics.FrameContextAdvice("error CS0103: nope","","C:\\target\\App.exe"),StringComparison.Ordinal);
		Assert.Contains("an unknown module",FuncEvalDiagnostics.FrameContextAdvice("error CS0103: nope","",""),StringComparison.Ordinal);
	}

	/// <summary>Module context explains exactly one compiler error. Attaching it to the rest would bury
	/// the real diagnostic under advice about a frame that is fine.</summary>
	[Theory]
	[InlineData("error CS0571: cannot explicitly call operator or accessor")]
	[InlineData("error CS0119: 'X' is a type, which is not valid in the given context")]
	[InlineData("error BC30456: 'Nope' is not a member")]
	[InlineData(null)]
	public void A_compiler_error_that_is_not_name_not_found_gets_no_module_context_advice(string? error) {
		Assert.Null(FuncEvalDiagnostics.FrameContextAdvice(error,"mscorlib.dll"));
		Assert.Null(FuncEvalDiagnostics.Recovery(error,"mscorlib.dll",null));
	}

	/// <summary>The composed entry point must not let the new advice displace a gate refusal, or the
	/// argument that opens the gate goes unnamed again.</summary>
	[Fact]
	public void The_composed_recovery_still_answers_a_gate_refusal_with_the_gate() {
		Assert.Equal(FuncEvalDiagnostics.Recovery(SideEffects,sideEffectsGrantable:true),FuncEvalDiagnostics.Recovery(SideEffects,"mscorlib.dll",null,sideEffectsGrantable:true));
		Assert.Equal(FuncEvalDiagnostics.Recovery(FuncEvalOff),FuncEvalDiagnostics.Recovery(FuncEvalOff,"mscorlib.dll",null));
		Assert.Equal(FuncEvalDiagnostics.Recovery(UnsafePoint),FuncEvalDiagnostics.Recovery(UnsafePoint,"mscorlib.dll",null));
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
