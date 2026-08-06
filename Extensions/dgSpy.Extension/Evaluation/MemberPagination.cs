using System;

namespace dgSpy.Extension {
	static class MemberPagination {
		public static int Count(ulong total,int offset,int requested) {
			if (offset<0) throw new ArgumentOutOfRangeException(nameof(offset));
			if (requested<0) throw new ArgumentOutOfRangeException(nameof(requested));
			var start=(ulong)offset;
			if (start>=total) return 0;
			return (int)Math.Min((ulong)requested,total-start);
		}
	}
}
