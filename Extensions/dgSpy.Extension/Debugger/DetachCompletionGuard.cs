namespace dgSpy.Extension {
	/// <summary>Turns dnSpy's asynchronous detach completion into a truthful RPC contract. A request
	/// timing out must never be reported as detached while the manager still owns the target.</summary>
	internal static class DetachCompletionGuard {
		internal static void EnsureRemoved(bool stillActive,int? processId=null) {
			if(!stillActive) return;
			var target=processId.HasValue ? $" process {processId.Value}" : " the session";
			throw new RpcException("detach_timed_out",$"dnSpy did not remove{target} within 10 seconds. The session remains active; retry detach or restart the debugger without assuming the target was detached.");
		}
	}
}
