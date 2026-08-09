using System;
using HookLab.Contracts;
using HookLab.Probe.CorDebug.Patching;

namespace HookLab.Probe.CorDebug {
	public static class ProbeInitializer {
		/// <summary>Contract called by T09 after byte loading: identity-check, inventory, then backend resolution.</summary>
		public static ProbeRuntime Initialize(ProbeInitialization initialization) {
			if (initialization == null) throw new ArgumentNullException(nameof(initialization));
			MethodGuards.ValidateTarget(initialization.ExpectedTarget, initialization.IdentityProvider.GetCurrentIdentity());
			// Keep this call above CreateAfterInventory: that method's signature is the first point that resolves Harmony types.
			return BackendInventory.SelectBeforeResolve(BackendInventory.InspectLoadedAssemblies,
				inventory => { PinnedBackendLoader.EnsureLoaded(inventory); return ProbeRuntime.CreateAfterInventory(initialization, inventory); });
		}
	}
}
