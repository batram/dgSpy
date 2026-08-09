/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy and is distributed under GPLv3.
*/

using System;
using System.ComponentModel.Composition;
using dndbg.Engine;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Metadata;

namespace dnSpy.Debugger.DotNet.CorDebug.Impl {
	abstract partial class DbgEngineImpl {
		internal DnILCodeBreakpoint CreateDgSpyOwnedBreakpoint(ModuleId module, uint token, uint offset, Func<DbgThread?, bool> condition) {
			VerifyCorDebugThread();
			return dnDebugger.CreateBreakpoint(module.ToDnModuleId(), token, offset,
				context => condition(TryGetThread(context.E.CorThread)));
		}

		internal void RemoveDgSpyOwnedBreakpoint(DnILCodeBreakpoint breakpoint) {
			VerifyCorDebugThread();
			dnDebugger.RemoveBreakpoint(breakpoint);
		}
	}

	/// <summary>
	/// Narrow bridge used by dgSpy to own an engine breakpoint independently of dnSpy's public
	/// breakpoint collection. It intentionally exposes no general access to <see cref="DbgEngineImpl"/>
	/// or <see cref="DnDebugger"/>.
	/// </summary>
	[Export(typeof(IDgSpyOwnedBreakpointService))]
	[PartCreationPolicy(CreationPolicy.Shared)]
	sealed class DgSpyOwnedBreakpointService : IDgSpyOwnedBreakpointService {
		public bool IsSupported(DbgRuntime runtime) => DbgEngineImpl.TryGetEngine(runtime) is not null;

		void IDgSpyOwnedBreakpointService.Create(DbgRuntime runtime, ModuleId module, uint token, uint offset,
			Func<DbgThread?, bool> condition, Action<IDgSpyOwnedBreakpointHandle?, string?> completed) {
			if (runtime is null) throw new ArgumentNullException(nameof(runtime));
			if (condition is null) throw new ArgumentNullException(nameof(condition));
			if (completed is null) throw new ArgumentNullException(nameof(completed));
			var engine = DbgEngineImpl.TryGetEngine(runtime);
			if (engine is null) {
				completed(null, "The selected runtime is not controlled by the CorDebug engine.");
				return;
			}
			engine.CorDebugThread(() => {
				try {
					var breakpoint = engine.CreateDgSpyOwnedBreakpoint(module, token, offset, condition);
					completed(new DgSpyOwnedBreakpointHandle(engine, breakpoint), null);
				}
				catch (Exception ex) { completed(null, ex.Message); }
			});
		}
	}

	/// <summary>An opaque, owner-scoped engine breakpoint. Removal is idempotent and marshalled.</summary>
	sealed class DgSpyOwnedBreakpointHandle : IDgSpyOwnedBreakpointHandle {
		readonly DbgEngineImpl engine;
		readonly bool isBound;
		readonly string? bindError;
		DnILCodeBreakpoint? breakpoint;

		internal DgSpyOwnedBreakpointHandle(DbgEngineImpl engine, DnILCodeBreakpoint breakpoint) {
			this.engine = engine;
			this.breakpoint = breakpoint;
			isBound = breakpoint.Error == DnCodeBreakpointError.None;
			bindError = breakpoint.Error == DnCodeBreakpointError.FunctionNotFound ? "The method token was not found." :
				breakpoint.Error == DnCodeBreakpointError.CouldNotCreateBreakpoint ? "The engine could not create the function breakpoint." :
				breakpoint.Error == DnCodeBreakpointError.OtherError ? "The module is not loaded or the engine has not produced a binding verdict." : null;
		}

		public bool IsBound => isBound;
		public string? BindError => bindError;

		public void Remove(Action completed) {
			if (completed is null) throw new ArgumentNullException(nameof(completed));
			engine.CorDebugThread(() => {
				var value = breakpoint;
				breakpoint = null;
				if (value is not null)
					engine.RemoveDgSpyOwnedBreakpoint(value);
				completed();
			});
		}
	}
}
