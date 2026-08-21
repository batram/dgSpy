/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy and is distributed under GPLv3.
*/

using System;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Metadata;

namespace dnSpy.Contracts.Debugger.DotNet {
	/// <summary>
	/// Creates engine breakpoints owned outside dnSpy's public breakpoint collection.
	///
	/// <para>One provider per debugger engine, discovered by MEF and selected by
	/// <see cref="IsSupported(DbgRuntime)"/> rather than imported singly. It lived in the CorDebug
	/// contracts assembly while CorDebug was the only implementation, and the singleton import that went
	/// with it was the actual reason HookLab could not arrive on Mono: not that the soft debugger cannot
	/// place a breakpoint - it does so for every stepper - but that nothing could ask it to.</para>
	///
	/// <para>This narrow contract keeps consumers independent of the scanner-loaded implementation
	/// assemblies. It exposes no general access to an engine or to a raw debugger interface.</para>
	/// </summary>
	public interface IDgSpyOwnedBreakpointProvider {
		/// <summary>Returns true when <paramref name="runtime"/> is controlled by this provider's engine.
		/// Exactly one provider may answer true for a given runtime.</summary>
		bool IsSupported(DbgRuntime runtime);

		/// <summary>What refusals call this engine. Operator-visible, so it is the engine's name rather
		/// than a type name: "CorDebug", "Mono".</summary>
		string EngineName { get; }

		/// <summary>Posts creation to the engine's own thread and reports the binding verdict.
		///
		/// <para><paramref name="condition"/> is raised on the engine thread when the breakpoint is hit and
		/// returns whether the target should stay stopped. It must not block.</para></summary>
		void Create(DbgRuntime runtime, ModuleId module, uint token, uint offset,
			Func<DbgThread?, bool> condition, Action<IDgSpyOwnedBreakpointHandle?, string?> completed);

		/// <summary>Runs the engine's idempotent state reconciliation, bypassing a stale manager-level
		/// running state that would otherwise suppress the engine call.</summary>
		void ReconcileRun(DbgRuntime runtime, Action<string?> completed);
	}

	/// <summary>An opaque, owner-scoped engine breakpoint.</summary>
	public interface IDgSpyOwnedBreakpointHandle {
		/// <summary>True when the engine created and bound the engine breakpoint.</summary>
		bool IsBound { get; }
		/// <summary>The stable binding failure, or null when bound.</summary>
		string? BindError { get; }
		/// <summary>Removes the breakpoint idempotently on the engine thread.</summary>
		void Remove(Action completed);
	}
}
