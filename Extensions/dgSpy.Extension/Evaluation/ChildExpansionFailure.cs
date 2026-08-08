namespace dgSpy.Extension {
	// A failed child expansion is one failure, so it is reported as one error rather than as a page of
	// rows. The engine (DbgEngineValueNodeImpl) used to fill the whole requested page with identical error
	// nodes; over RPC those are fabricated members, and a caller cannot tell "this object has N broken
	// members" from "expansion failed once". Naming the parent expression keeps the error actionable when
	// the caller is paging through a large object.
	static class ChildExpansionFailure {
		public static RpcException ToRpcException(string parentExpression,string errorMessage) =>
			new RpcException("evaluation_failed",$"Expanding '{parentExpression}' failed: {errorMessage}{Advice(errorMessage)}");

		// An error thrown rather than returned skips DescribeNode, so it would otherwise be the one
		// evaluation answer that still hands back dnSpy's bare sentence with no gate named -- exactly the
		// shape the recovery text exists to eliminate. Append it here instead. get_members and its child
		// expansion cannot grant side effects, so the no-flag variant is the correct one.
		//
		// Deliberately unlabeled: the Gateway already renders every error as
		// "<code>: <message> Recovery: <guidance>", so labeling this "Recovery:" too produced one line
		// carrying the word twice with different text after each.
		public static string Advice(string errorMessage) {
			var recovery=FuncEvalDiagnostics.Recovery(errorMessage);
			return recovery is null ? "" : " "+recovery;
		}
	}
}
