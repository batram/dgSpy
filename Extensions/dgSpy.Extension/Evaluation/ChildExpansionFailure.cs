namespace dgSpy.Extension {
	// A failed child expansion is one failure, so it is reported as one error rather than as a page of
	// rows. The engine (DbgEngineValueNodeImpl) used to fill the whole requested page with identical error
	// nodes; over RPC those are fabricated members, and a caller cannot tell "this object has N broken
	// members" from "expansion failed once". Naming the parent expression keeps the error actionable when
	// the caller is paging through a large object.
	static class ChildExpansionFailure {
		public static RpcException ToRpcException(string parentExpression,string errorMessage) =>
			new RpcException("evaluation_failed",$"Expanding '{parentExpression}' failed: {errorMessage}");
	}
}
