namespace dgSpy.Extension {
	/// <summary>Centralizes the stale-frame contract so the otherwise timing-dependent race has a
	/// deterministic executable check. Callers still pass dnSpy's live IsClosed value immediately
	/// before reading or evaluating a captured frame.</summary>
	internal static class FrameSnapshotGuard {
		internal static void EnsureOpen(bool isClosed,string message) {
			if (isClosed) throw new RpcException("stale_handle",message);
		}
	}
}
