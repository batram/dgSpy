/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy and is distributed under GPLv3.
*/

using System;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Metadata;

namespace dnSpy.Contracts.Debugger.DotNet.CorDebug {
	/// <summary>
	/// Creates CorDebug engine breakpoints owned outside dnSpy's public breakpoint collection.
	/// This narrow contract keeps consumers independent of the scanner-loaded implementation assembly.
	/// </summary>
	public interface IDgSpyOwnedBreakpointService {
		/// <summary>Returns true when <paramref name="runtime"/> is controlled by CorDebug.</summary>
		bool IsSupported(DbgRuntime runtime);
		/// <summary>Posts creation to the CorDebug thread and reports the binding verdict.</summary>
		void Create(DbgRuntime runtime, ModuleId module, uint token, uint offset,
			Func<DbgThread?, bool> condition, Action<IDgSpyOwnedBreakpointHandle?, string?> completed);
	}

	/// <summary>An opaque, owner-scoped engine breakpoint.</summary>
	public interface IDgSpyOwnedBreakpointHandle {
		/// <summary>True when CorDebug created and bound the engine breakpoint.</summary>
		bool IsBound { get; }
		/// <summary>The stable binding failure, or null when bound.</summary>
		string? BindError { get; }
		/// <summary>Removes the breakpoint idempotently on the CorDebug thread.</summary>
		void Remove(Action completed);
	}
}
