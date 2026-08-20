using System;
using System.Security.Principal;

namespace HookLab.Injector {
	/// <summary>
	/// The controller and the target, read once and passed explicitly.
	///
	/// <para>These two principals determine every authority decision HookLab initialization makes: where
	/// the exchange area goes and who its DACL names, and who the resident's control endpoint admits.
	/// Before this existed each of those asked <see cref="WindowsIdentity.GetCurrent"/> separately, so
	/// the same fact was computed twice from two different call sites - and two computations of one fact
	/// are two chances to disagree. Under impersonation they genuinely would, and the failure would be a
	/// pipe naming one principal while the staging directory named another.</para>
	///
	/// <para>They determine the <em>intended</em> grants and nothing more. Whether those grants are
	/// usable depends on the effective token, which SID equality does not establish: see
	/// <see cref="ExchangeArea.TargetEffectiveAccess"/>.</para>
	/// </summary>
	public sealed class IdentityPair {
		IdentityPair(SecurityIdentifier controller,SecurityIdentifier? target) { Controller=controller; Target=target; }

		public SecurityIdentifier Controller { get; }

		/// <summary>Null when the target's identity could not be read - a protected process, or one owned
		/// by an account this host cannot open. That is reported rather than assumed to match, because
		/// assuming it matched is what staged a payload where the target could not read it.</summary>
		public SecurityIdentifier? Target { get; }

		/// <summary>True only when both are known and they differ. An unknown target is not "different";
		/// it is unknown, and callers that need to act on the distinction check <see cref="Target"/>.</summary>
		public bool CrossIdentity=>Target is not null&&!Controller.Equals(Target);

		/// <summary>Reads both identities. Throws only when this process cannot identify itself, which
		/// leaves no basis for any authority decision at all.</summary>
		public static IdentityPair For(int targetProcessId) {
			var controller=ProcessIdentity.TryGetCurrentSid()
				??throw new InvalidOperationException("This process could not read its own Windows identity, so no authority relationship can be derived.");
			return new IdentityPair(controller,ProcessIdentity.TryGetUserSid(targetProcessId));
		}

		/// <summary>For callers that already hold both, and for tests.</summary>
		public static IdentityPair Of(SecurityIdentifier controller,SecurityIdentifier? target)=>
			new IdentityPair(controller??throw new ArgumentNullException(nameof(controller)),target);

		/// <summary>The controller SID as the resident's endpoint expects it in <c>initialize.params</c>,
		/// or null when the two principals match and the probe would ignore it anyway.
		///
		/// <para>The endpoint DACL is defence in depth rather than the authentication boundary: the
		/// channel is already authenticated by a 32-byte secret injected with the payload, and whoever
		/// writes <c>initialize.params</c> has already chosen the bytes the target will execute, so they
		/// are strictly more privileged than anything this DACL could grant. It exists so that a target
		/// running as another account does not build an endpoint its own controller cannot open.</para></summary>
		public string? ControllerSidForEndpoint=>CrossIdentity?Controller.Value:null;
	}
}
