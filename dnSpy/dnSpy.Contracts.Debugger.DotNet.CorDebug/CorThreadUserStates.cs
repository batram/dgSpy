/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

namespace dnSpy.Contracts.Debugger.DotNet.CorDebug {
	/// <summary>
	/// <see cref="DbgThread.State"/> values
	/// </summary>
	public static class CorThreadUserStates {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
		public static readonly string StopRequested = nameof(StopRequested);
		public static readonly string SuspendRequested = nameof(SuspendRequested);
		public static readonly string Background = nameof(Background);
		public static readonly string Unstarted = nameof(Unstarted);
		public static readonly string Stopped = nameof(Stopped);
		public static readonly string WaitSleepJoin = nameof(WaitSleepJoin);
		public static readonly string Suspended = nameof(Suspended);
		public static readonly string UnsafePoint = nameof(UnsafePoint);
		public static readonly string ThreadPool = nameof(ThreadPool);
		/// <summary>
		/// ICorDebugThread::GetUserState failed, so no conclusion may be drawn from the absence of any other
		/// state. Present instead of - not alongside - the real flags, because there are none to report.
		/// Without it a failed probe produced an empty state collection, which reads exactly like a thread
		/// that is running normally at a safe point.
		/// </summary>
		public static readonly string UserStateUnavailable = nameof(UserStateUnavailable);
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
	}
}
