/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy and is distributed under GPLv3.
*/

using System;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet;
using dnSpy.Contracts.Metadata;
using Mono.Debugger.Soft;

namespace dnSpy.Debugger.DotNet.Mono.Impl {
	/// <summary>
	/// The Mono soft debugger's owned-breakpoint provider: the counterpart of the CorDebug one, and the
	/// piece that was missing for HookLab arrival on Mono.
	///
	/// <para>Nothing about the soft debugger blocked this. It places engine breakpoints for every
	/// stepper, through the same <c>Tag</c>-carried <c>Func&lt;DbgThread?, bool&gt;</c> callback this
	/// uses, where the return value is "stay stopped". What was missing was a way to ask it: the
	/// contract lived in the CorDebug contracts assembly and was imported as a singleton, so there was
	/// exactly one implementation by construction.</para>
	///
	/// <para>Like the CorDebug bridge, it exposes no general access to <see cref="DbgEngineImpl"/> or to
	/// the raw <see cref="VirtualMachine"/>.</para>
	/// </summary>
	[Export(typeof(IDgSpyOwnedBreakpointProvider))]
	[PartCreationPolicy(CreationPolicy.Shared)]
	sealed class DgSpyMonoOwnedBreakpointService : IDgSpyOwnedBreakpointProvider {
		public bool IsSupported(DbgRuntime runtime) => DbgEngineImpl.TryGetEngine(runtime) is not null;
		public string EngineName => "Mono";

		void IDgSpyOwnedBreakpointProvider.Create(DbgRuntime runtime, ModuleId module, uint token, uint offset,
			Func<DbgThread?, bool> condition, Action<IDgSpyOwnedBreakpointHandle?, string?> completed) {
			if (runtime is null) throw new ArgumentNullException(nameof(runtime));
			if (condition is null) throw new ArgumentNullException(nameof(condition));
			if (completed is null) throw new ArgumentNullException(nameof(completed));
			var engine = DbgEngineImpl.TryGetEngine(runtime);
			if (engine is null) {
				completed(null, "The selected runtime is not controlled by the Mono engine.");
				return;
			}
			// CorDebug takes a ModuleId straight to DnDebugger, which can hold an unbound breakpoint until
			// the module arrives. The soft debugger has no such pending form for an owned breakpoint, so
			// the module has to be one the runtime has already loaded - which is exactly what a caller who
			// picked the slot out of the live module list is holding. Resolving it here, rather than
			// widening the contract, keeps CorDebug's lazy binding untouched.
			var target = runtime.Modules.FirstOrDefault(value => DbgEngineImpl.TryGetModuleId(value) == module);
			if (target is null) {
				completed(null, "The Mono runtime has not loaded a module matching " + module.ModuleName +
					", and the soft debugger cannot hold an owned breakpoint for a module that is not there yet.");
				return;
			}
			engine.MonoDebugThread(() => {
				try {
					var breakpoint = engine.CreateDgSpyOwnedBreakpoint(target, token, offset, condition);
					completed(new DgSpyMonoOwnedBreakpointHandle(engine, breakpoint), null);
				}
				catch (Exception ex) { completed(null, ex.Message); }
			});
		}

		void IDgSpyOwnedBreakpointProvider.ReconcileRun(DbgRuntime runtime, Action<string?> completed) {
			if (runtime is null) throw new ArgumentNullException(nameof(runtime));
			if (completed is null) throw new ArgumentNullException(nameof(completed));
			var engine = DbgEngineImpl.TryGetEngine(runtime);
			if (engine is null) { completed("The selected runtime is not controlled by the Mono engine."); return; }
			engine.MonoDebugThread(() => {
				try { engine.RunCore(); completed(null); }
				catch (Exception ex) { completed(ex.Message); }
			});
		}
	}

	/// <summary>An opaque, owner-scoped engine breakpoint. Removal is idempotent and marshalled.</summary>
	sealed class DgSpyMonoOwnedBreakpointHandle : IDgSpyOwnedBreakpointHandle {
		readonly DbgEngineImpl engine;
		BreakpointEventRequest? breakpoint;

		internal DgSpyMonoOwnedBreakpointHandle(DbgEngineImpl engine, BreakpointEventRequest breakpoint) {
			this.engine = engine;
			this.breakpoint = breakpoint;
		}

		/// <summary>Always true here, and that is not a stub. The soft debugger has no unbound state to
		/// report: <c>CreateBreakpointRequest</c> either enables a request against a resolved
		/// <c>MethodMirror</c> or throws, and the throw is turned into a create failure with a reason
		/// before a handle exists. CorDebug's three-way verdict exists because CorDebug really can hand
		/// back a breakpoint object that never bound.</summary>
		public bool IsBound => true;
		public string? BindError => null;

		public void Remove(Action completed) {
			if (completed is null) throw new ArgumentNullException(nameof(completed));
			engine.MonoDebugThread(() => {
				var value = breakpoint;
				breakpoint = null;
				if (value is not null) {
					// Never allowed to escape: removal runs on teardown paths, and a disconnected VM
					// throwing here would strand the caller's completion and, with it, the release of the
					// owner that is waiting on it.
					try { engine.RemoveDgSpyOwnedBreakpoint(value); }
					catch (VMDisconnectedException) { }
					catch (ObjectDisposedException) { }
				}
				completed();
			});
		}
	}
}
