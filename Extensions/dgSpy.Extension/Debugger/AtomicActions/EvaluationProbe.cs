using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>
	/// Why a func-eval at an owned stop would be refused, or <see cref="none"/> when the preflight found
	/// nothing to refuse it.
	///
	/// <para><b><see cref="none"/> is a preflight, not a promise.</b> The authoritative answer is whether
	/// the func-eval actually ran, and no read-only check can supply it. In particular a <c>capture</c>
	/// probe cannot: it sets <c>allow_func_eval=false</c>, so its failure says nothing about whether a
	/// func-eval would have worked. A live sweep measured 13 optimized offsets where <c>capture</c> failed
	/// with "possibly because it has been optimized away" while a real <c>method_invocation</c> at the same
	/// stop succeeded and returned a value - so a capture-based judgement would have rejected every usable
	/// slot. Only <c>method_invocation</c> exercises the mechanism.</para>
	/// </summary>
	public enum AtomicActionEvaluationBlocker {
		/// <summary>The preflight found no blocker. Not a guarantee that a func-eval will succeed.</summary>
		none,
		/// <summary>The stack walk produced no frame at all, so there is no frame to evaluate in.</summary>
		no_frames,
		/// <summary>The topmost frame is native; CorDebug refuses to evaluate against one.</summary>
		native_frame,
		/// <summary>The thread is parked at a GC-unsafe point; ICorDebugEval fails with
		/// CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT.</summary>
		unsafe_point,
		/// <summary>The preflight itself failed, so nothing is known either way. This is deliberately not
		/// collapsed into <see cref="no_frames"/>: a catch-all that answered "no frames" for every exception
		/// during the probe reported a measurement failure as a fact about the target.</summary>
		probe_failed,
	}

	/// <summary>Which part of the preflight failed, when it did.</summary>
	public enum AtomicActionEvaluationProbeStage {
		/// <summary>No stage failed.</summary>
		none,
		/// <summary>Walking the thread's stack threw.</summary>
		stack_walk,
		/// <summary>ICorDebugThread::GetUserState did not answer, so the absence of USER_UNSAFE_POINT is not
		/// evidence that the thread is at a safe point.</summary>
		user_state,
	}

	/// <summary>
	/// One preflight verdict, with the failure localized when there was one. Deliberately not a bare string:
	/// the previous shape collapsed three distinct blockers into <c>evaluable = ... is null</c> and then
	/// printed one hardcoded message ("The target was reached at a CorDebug-unsafe point.") that was false
	/// for two of the three.
	/// </summary>
	public sealed class AtomicActionEvaluationProbe {
		public static readonly AtomicActionEvaluationProbe Clear=new AtomicActionEvaluationProbe(AtomicActionEvaluationBlocker.none);

		public AtomicActionEvaluationProbe(AtomicActionEvaluationBlocker blocker,AtomicActionEvaluationProbeStage stage=AtomicActionEvaluationProbeStage.none,string? errorCategory=null,string? error=null) {
			Blocker=blocker; Stage=stage; ErrorCategory=errorCategory; Error=error;
		}

		public AtomicActionEvaluationBlocker Blocker { get; }
		public AtomicActionEvaluationProbeStage Stage { get; }
		public string? ErrorCategory { get; }
		public string? Error { get; }
		public bool Evaluable => Blocker==AtomicActionEvaluationBlocker.none;

		/// <summary>The message the terminal result carries. Every blocker gets its own sentence, because
		/// the single hardcoded "CorDebug-unsafe point" line was wrong for <c>no_frames</c> and
		/// <c>native_frame</c> and hid a failed probe entirely.</summary>
		public string Describe() => Blocker switch {
			AtomicActionEvaluationBlocker.none=>"The target was reached at an evaluable point (preflight only; only a func-eval proves it).",
			AtomicActionEvaluationBlocker.no_frames=>"The target was reached but the thread produced no stack frame to evaluate in.",
			AtomicActionEvaluationBlocker.native_frame=>"The target was reached but the thread's topmost frame is native, and CorDebug cannot evaluate against one.",
			AtomicActionEvaluationBlocker.unsafe_point=>"The target was reached at a CorDebug-unsafe point.",
			_=>"The target was reached but the evaluability preflight failed at stage '"+Stage+"' ("+(ErrorCategory ?? "unknown")+": "+(Error ?? "no detail")+"), so nothing is known about whether a func-eval would run.",
		};

		/// <summary>The probe failed while walking the stack. Category and message are bounded and redacted
		/// by <see cref="ProbeFailure"/>.</summary>
		public static AtomicActionEvaluationProbe FromStackWalkFailure(Exception exception) =>
			new AtomicActionEvaluationProbe(AtomicActionEvaluationBlocker.probe_failed,AtomicActionEvaluationProbeStage.stack_walk,
				ProbeFailure.Category(exception),ProbeFailure.Detail(exception));

		/// <summary>GetUserState did not answer. There is no exception to describe - the HRESULT was
		/// swallowed by the CorDebug wrapper's own conversion long before this - so the stage is the whole
		/// fact, and it is reported rather than read as "no flags, therefore safe".</summary>
		public static readonly AtomicActionEvaluationProbe UserStateUnavailable=
			new AtomicActionEvaluationProbe(AtomicActionEvaluationBlocker.probe_failed,AtomicActionEvaluationProbeStage.user_state,
				"UserStateUnavailable","ICorDebugThread::GetUserState did not answer, so USER_UNSAFE_POINT could be neither confirmed nor ruled out.");
	}

	/// <summary>
	/// Turns an exception into something that can cross RPC. Three rules, and each exists because the
	/// obvious alternative leaks: no full exception serialization, no stack trace, and no message text
	/// copied out verbatim - an evaluation error message can quote an expression, a field value, or a path,
	/// and a target value or an endpoint credential must never ride out through a diagnostic field.
	/// </summary>
	public static class ProbeFailure {
		public const int MaxCategoryLength=64;
		public const int MaxDetailLength=200;

		/// <summary>A stable category: the exception's type name, unqualified and bounded. Stable across
		/// runs and localizations, which a message is not.</summary>
		public static string Category(Exception exception) {
			if(exception is null) throw new ArgumentNullException(nameof(exception));
			return Truncate(exception.GetType().Name,MaxCategoryLength);
		}

		/// <summary>
		/// The detail. A COM failure reports its HRESULT and nothing else - it is the stable, complete and
		/// non-sensitive fact, and the accompanying message is a localized restatement of it. Anything else
		/// reports a redacted, bounded message: quoted runs are replaced wholesale, because that is where an
		/// evaluator puts the expression and the value it was working on.
		/// </summary>
		public static string Detail(Exception exception) {
			if(exception is null) throw new ArgumentNullException(nameof(exception));
			if(exception is ExternalException com)
				return "HRESULT 0x"+com.ErrorCode.ToString("X8",CultureInfo.InvariantCulture);
			return Truncate(Redact(exception.Message),MaxDetailLength);
		}

		/// <summary>Replaces every single- or double-quoted run with a fixed placeholder. An unterminated
		/// quote redacts to end of text rather than falling through, so a truncated message cannot leak the
		/// tail it was going to quote.</summary>
		public static string Redact(string? message) {
			if(String.IsNullOrEmpty(message)) return "";
			var builder=new StringBuilder(message!.Length);
			var quote='\0';
			foreach(var character in message!) {
				if(quote!='\0') { if(character==quote) quote='\0'; continue; }
				if(character=='\'' || character=='"') { quote=character; builder.Append("'<redacted>'"); continue; }
				builder.Append(character=='\r' || character=='\n' || character=='\t' ? ' ' : character);
			}
			return builder.ToString().Trim();
		}

		static string Truncate(string value,int max) => value.Length<=max ? value : value.Substring(0,max-3)+"...";
	}
}
