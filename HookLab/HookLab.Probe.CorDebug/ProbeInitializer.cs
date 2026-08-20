using System;
using System.Runtime.CompilerServices;
using HookLab.Contracts;
using HookLab.Probe.CorDebug.Patching;

namespace HookLab.Probe.CorDebug {
	public static class ProbeInitializer {
		static readonly object Gate = new object();
		static BackendInventoryResult? preloaded;

		/// <summary>Takes the backend inventory and makes the pinned patch engine resolvable, without
		/// naming a single probe type.
		///
		/// <para>That restriction is the whole point. Mono resolves a type's field types when it prepares
		/// any method that names the type - in its body <em>or in its own signature</em> - while CLR v4
		/// waits until the type is used. <see cref="Initialize"/> returns a <c>ProbeRuntime</c>, whose
		/// <c>harmony</c> field is typed from the pinned engine, so on Mono merely preparing
		/// <see cref="Initialize"/> demands the engine before its first line runs and the load fails
		/// before it can succeed. Measured on Unity 2021.3's Mono 6.13, 2026-08-20; the same shape passes
		/// on CLR v4, which is why the original in-body ordering held for as long as it did.</para>
		///
		/// <para>So the caller loads the engine through this method first, from a frame that mentions no
		/// probe type, and only then calls <see cref="Initialize"/>.</para></summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void PreloadPatchEngine() {
			lock (Gate) {
				if (preloaded != null) return;
				// Inventory before the load, and kept: inspecting again afterwards would see the copy this
				// method just loaded and report a resident patch engine that was never there.
				var inventory = BackendInventory.InspectLoadedAssemblies();
				PinnedBackendLoader.EnsureLoaded(inventory);
				preloaded = inventory;
			}
		}

		/// <summary>Contract called by T09 after byte loading: identity-check, inventory, then backend resolution.</summary>
		public static ProbeRuntime Initialize(ProbeInitialization initialization) {
			if (initialization == null) throw new ArgumentNullException(nameof(initialization));
			MethodGuards.ValidateTarget(initialization.ExpectedTarget, initialization.IdentityProvider.GetCurrentIdentity());
			// Keep this call above CreateAfterInventory: that method's signature is the first point that resolves Harmony types.
			return BackendInventory.SelectBeforeResolve(Inventory,
				inventory => { PinnedBackendLoader.EnsureLoaded(inventory); return ProbeRuntime.CreateAfterInventory(initialization, inventory); });
		}

		/// <summary>The inventory <see cref="PreloadPatchEngine"/> took, or a fresh one when nothing
		/// preloaded. Reusing it keeps <c>UseResident</c> answering the question it was asked - what was
		/// already in the process - rather than what this resident has since loaded.</summary>
		static BackendInventoryResult Inventory() {
			lock (Gate) { if (preloaded != null) return preloaded; }
			return BackendInventory.InspectLoadedAssemblies();
		}
	}
}
