using System;

namespace dgSpy.Extension {
	/// <summary>Classifies the error text dnSpy returns from an evaluation, so a caller can tell a mistake
	/// it must fix from a refusal it should retry elsewhere.
	///
	/// dnSpy's public evaluation contracts carry a compiler-error flag on assignment only
	/// (<c>DbgEEAssignmentResult.IsCompilerError</c>); the value-node path that invoke_method and
	/// create_object take reports an error string and nothing else. The string is still decisive: a
	/// compilation failure is a Roslyn diagnostic, which is always rendered with its culture-invariant
	/// <c>error CS1234:</c> / <c>error BC1234:</c> prefix, while every execution refusal comes from
	/// <c>PredefinedEvaluationErrorMessages</c> and is a plain localized sentence.</summary>
	static class FuncEvalDiagnostics {
		/// <summary>True for a Roslyn diagnostic, ie. the expression never compiled and nothing ran in the
		/// target. Matches "error CS0571: ..." and its Visual Basic equivalent.</summary>
		public static bool IsCompilerError(string? error) {
			if (error is null) return false;
			const string prefix="error ";
			var text=error.TrimStart();
			if (!text.StartsWith(prefix,StringComparison.Ordinal)) return false;
			var index=prefix.Length;
			var letters=0; while (index<text.Length && char.IsLetter(text[index])) { index++; letters++; }
			var digits=0; while (index<text.Length && char.IsDigit(text[index])) { index++; digits++; }
			return letters==2 && digits!=0 && index<text.Length && text[index]==':';
		}

		/// <summary>Recovery advice for the refusals whose stock dnSpy text names the wrong remedy, or null
		/// when dgSpy has nothing better to say than the engine did. Matched on dnSpy's English resource
		/// text, which is what this host ships.
		///
		/// Two of the three are gate refusals dgSpy asked for: it sets NoSideEffects and NoFuncEval when the
		/// caller did not opt in, and dnSpy's answer then describes the gate without naming the argument
		/// that opens it. That is the failure mode this exists for. An agent that reads "This expression causes side
		/// effects and will not be evaluated" after passing allow_func_eval:true concludes the override
		/// does not work — it does, it is simply the wrong override for that gate — and falls back to
		/// computing the answer by hand, which is exactly the unverified result a debugger is there to
		/// prevent. The two gates are independent and are checked side-effects-first, so a method call
		/// trips the side-effects gate whether or not func-eval was allowed:
		///
		/// <list type="bullet">
		/// <item>A property getter, eg. <c>DateTime.Now.Ticks</c>, is a func-eval that Roslyn does not
		/// class as side-effecting: <c>allow_func_eval</c> alone runs it.</item>
		/// <item>An explicit method call, eg. <c>Telemetry.ComputeReading(5)</c>, is both, and needs
		/// <c>allow_func_eval</c> and <c>allow_side_effects</c> together — or invoke_method, whose tool
		/// boundary sets both and audits the call.</item>
		/// <item>The same call written <c>Telemetry.ComputeReading(5), ac</c> succeeds with
		/// <c>allow_func_eval</c> alone. dnSpy's "always calculate" format specifier opens the side-effects
		/// gate by itself — see the <c>HasAllowFuncEval</c> term in
		/// <c>DbgEngineExpressionEvaluatorImpl.EvaluateImpl</c>. This is why two callers can honestly
		/// report opposite results for what looks like the same call, and it is the reason the text below
		/// must name the gate that blocked this expression rather than describe a flag as broken.</item>
		/// </list>
		///
		/// The unsafe-point message ends with "Step once or run until a breakpoint hits", which is bad
		/// advice for the case that produces it most often: an idle process whose threads are all blocked
		/// in a native wait. Stepping cannot advance a thread that is not running managed code, so the
		/// only way through is the second half of that sentence — reach a stop the target will actually
		/// arrive at.</summary>
		/// <param name="sideEffectsGrantable">Whether the tool that produced this error exposes an
		/// <c>allow_side_effects</c> argument. Only evaluate does; get_members, get_frame, get_autos,
		/// list_watches and get_exception evaluate with side effects permanently off, and telling their
		/// callers to pass a flag those tools do not accept would be a second wrong remedy.</param>
		public static string? Recovery(string? error,bool sideEffectsGrantable=false) {
			if (error is null) return null;
			if (error.IndexOf("causes side effects",StringComparison.OrdinalIgnoreCase)>=0)
				return sideEffectsGrantable
					? "Blocked by the side-effects gate, not the func-eval gate: allow_side_effects was not true. "+
						"allow_func_eval does not imply it and cannot substitute for it, so passing allow_func_eval alone leaves this message unchanged. "+
						"An explicit method call trips both gates — retry with allow_func_eval:true AND allow_side_effects:true. "+
						"A property getter trips only the func-eval gate. To run a method under an audit record instead, use invoke_method, which sets both. "+
						"dnSpy's \"ac\" format specifier (append \", ac\" to the expression) also opens this gate on its own, which is why the same call can succeed for another caller with allow_func_eval alone."
					: "Blocked by the side-effects gate, which this tool holds shut: it evaluates with side effects permanently off and has no allow_side_effects argument. "+
						"allow_func_eval does not open this gate. Evaluate the expression with `evaluate` passing allow_func_eval:true and allow_side_effects:true, "+
						"or use invoke_method, which sets both and audits the call.";
			if (error.IndexOf("function evaluation is turned off",StringComparison.OrdinalIgnoreCase)>=0)
				return "Blocked by the func-eval gate: allow_func_eval was not true. Retry with allow_func_eval:true, which permits running target code such as a property getter. "+
					"If the expression is an explicit method call it also trips the side-effects gate, so it needs allow_side_effects:true as well, or invoke_method.";
			if (error.IndexOf("unsafe point",StringComparison.OrdinalIgnoreCase)<0) return null;
			return "The engine parked this thread where a func-eval cannot start; the expression itself is fine. "+
				"Do not step: on an idle process every thread is blocked in a native wait and stepping cannot advance any of them. "+
				"Call list_threads with include_evaluability=true to find a thread with can_evaluate=true and pass its thread_id. "+
				"If no thread qualifies, set_breakpoint on a method the target will actually reach, continue, drive the target to it, and evaluate at the hit.";
		}
	}
}
