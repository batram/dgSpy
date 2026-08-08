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

using System;

namespace dnSpy.Contracts.Debugger {
	/// <summary>Optional diagnostics implemented by dispatcher hosts that contain asynchronous callback
	/// faults instead of terminating the debugger thread.</summary>
	public interface IDbgDispatcherDiagnostics {
		long FaultCount { get; }
		DateTime? LastFaultUtc { get; }
		string? LastFault { get; }

		/// <summary>True once the dispatcher has begun shutting down and will silently drop work.</summary>
		bool IsShutdown { get; }

		/// <summary>Queues <paramref name="callback"/> and reports whether it was actually queued.
		/// <see cref="DbgDispatcher.BeginInvoke(Action)"/> discards work on a shut-down dispatcher and
		/// cannot say so, which turns every caller that waits for a result into a hang until its
		/// deadline. Callers that need an answer must be able to fail immediately instead.</summary>
		bool TryBeginInvoke(Action callback);
	}

	/// <summary>
	/// Invokes code on another thread.
	/// </summary>
	public abstract class DbgDispatcher {
		/// <summary>
		/// Throws if the current thread isn't the dispatcher thread
		/// </summary>
		public void VerifyAccess() {
			if (!CheckAccess())
				throw new InvalidOperationException("Wrong dispatcher thread");
		}

		/// <summary>
		/// Checks whether the current thread is the dispatcher thread
		/// </summary>
		/// <returns></returns>
		public abstract bool CheckAccess();

		/// <summary>
		/// Executes code asynchronously on the dispatcher thread. This method returns immediately even if
		/// it happens to be called on the dispatcher thread.
		/// </summary>
		/// <param name="callback">Code to execute</param>
		public abstract void BeginInvoke(Action callback);
	}
}
