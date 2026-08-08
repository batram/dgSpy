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
		/// when dgSpy has nothing better to say than the engine did.
		///
		/// The unsafe-point message ends with "Step once or run until a breakpoint hits", which is bad
		/// advice for the case that produces it most often: an idle process whose threads are all blocked
		/// in a native wait. Stepping cannot advance a thread that is not running managed code, so the
		/// only way through is the second half of that sentence — reach a stop the target will actually
		/// arrive at. Matched on dnSpy's English resource text, which is what this host ships.</summary>
		public static string? Recovery(string? error) {
			if (error is null) return null;
			if (error.IndexOf("unsafe point",StringComparison.OrdinalIgnoreCase)<0) return null;
			return "The engine parked this thread where a func-eval cannot start; the expression itself is fine. "+
				"Do not step: on an idle process every thread is blocked in a native wait and stepping cannot advance any of them. "+
				"Call list_threads with include_evaluability=true to find a thread with can_evaluate=true and pass its thread_id. "+
				"If no thread qualifies, set_breakpoint on a method the target will actually reach, continue, drive the target to it, and evaluate at the hit.";
		}
	}
}
