/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

using System;

namespace dnSpy.Contracts.Debugger {
	/// <summary>Describes the action which currently excludes a debugger mutation.</summary>
	public readonly struct DbgActionBlockInfo {
		public int ProcessId { get; }
		public string ActionName { get; }
		public string ActionId { get; }
		public DateTime DeadlineUtc { get; }
		public string StatusOperation { get; }
		public string CancelOperation { get; }
		public string Message { get; }

		public DbgActionBlockInfo(int processId, string actionName, string actionId, DateTime deadlineUtc, string statusOperation, string cancelOperation, string message) {
			ProcessId = processId;
			ActionName = actionName ?? throw new ArgumentNullException(nameof(actionName));
			ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
			DeadlineUtc = deadlineUtc;
			StatusOperation = statusOperation ?? throw new ArgumentNullException(nameof(statusOperation));
			CancelOperation = cancelOperation ?? throw new ArgumentNullException(nameof(cancelOperation));
			Message = message ?? throw new ArgumentNullException(nameof(message));
		}
	}

	/// <summary>
	/// Optional debugger mutation guard. Hosts import zero or more guards, so stock dnSpy retains its
	/// normal behavior when no extension exports one. When several guards are exported, the host queries
	/// every guard and refuses the mutation if any guard blocks it; guards are never first-wins.
	/// Implementations must make <see cref="TryGetBlock"/> a fast, non-reentrant snapshot read. It is
	/// called from mutation entry points and must not marshal threads or call back into debugger services.
	/// </summary>
	public abstract class DbgActionGuard {
		/// <summary>Returns true when <paramref name="operation"/> must not mutate the process.</summary>
		/// <param name="process">Affected process, or null for a mutation whose process is not known.</param>
		public abstract bool TryGetBlock(DbgProcess? process, string operation, out DbgActionBlockInfo info);

		/// <summary>Surfaces a refused unattributed mutation through the host's existing user-error path.</summary>
		public abstract void ReportBlocked(in DbgActionBlockInfo info);
	}

	/// <summary>Stable operation names passed to <see cref="DbgActionGuard"/>.</summary>
	public static class PredefinedDbgActionOperations {
		public const string Continue = "continue";
		public const string Pause = "pause";
		public const string Step = "step";
		public const string Detach = "detach";
		public const string Terminate = "terminate";
		public const string Restart = "restart";
		public const string SetInstructionPointer = "set_instruction_pointer";
		public const string BreakpointMutation = "breakpoint_mutation";
	}
}
