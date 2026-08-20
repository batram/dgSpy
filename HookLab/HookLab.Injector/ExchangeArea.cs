using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HookLab.Injector {
	/// <summary>Whether a precondition was proved, disproved, or could not honestly be decided before
	/// the operation was attempted. The third is a real answer and never silently becomes the first.</summary>
	public enum PreconditionResult { Satisfied, Failed, NotProvablePreflight }

	/// <summary>
	/// A filesystem location both the controller and the target can use.
	///
	/// <para>Every staging path on the initialization path used to come from <c>Path.GetTempPath()</c>,
	/// which answers "the debugger's temp directory" and says nothing about the target. Against a live
	/// IIS worker running as a domain service account that directory was unreadable, the target's
	/// loader returned NULL, and the failure was indistinguishable from a missing dependency. The fix
	/// is not a different path; it is making the two identities explicit and deriving the location from
	/// both of them.</para>
	///
	/// <para>Two phases, one object, because the contract has to be computable before the directory
	/// exists and the acting path has to use the directory the contract reasoned about:</para>
	/// <list type="bullet">
	/// <item><see cref="ExchangeAreaPlan"/> selects the root and computes the intended DACL. No mutation.</item>
	/// <item><see cref="ExchangeArea"/> creates it once, applies that DACL, and reads it back.</item>
	/// </list>
	///
	/// <para><b>Creating it successfully does not establish that the target can use it.</b> An authored
	/// DACL is a statement of intent; whether the target's effective token can act on it is a different
	/// question, answered by the target acting. See <see cref="TargetEffectiveAccess"/>.</para>
	/// </summary>
	public sealed class ExchangeAreaPlan {
		internal ExchangeAreaPlan(string root,bool crossIdentity,SecurityIdentifier controller,SecurityIdentifier? target,PreconditionResult creationAuthorized,string detail) {
			Root=root; CrossIdentity=crossIdentity; Controller=controller; Target=target; CreationAuthorized=creationAuthorized; CreationDetail=detail;
		}

		/// <summary>The directory that would be created. It does not exist yet.</summary>
		public string Root { get; }
		/// <summary>True when the controller and the target are different principals, which is the only
		/// case that needs a shared location rather than a private one.</summary>
		public bool CrossIdentity { get; }
		public SecurityIdentifier Controller { get; }
		/// <summary>Null when the target's identity could not be read, which is itself a reason the
		/// contract cannot be completed rather than a licence to assume it matches.</summary>
		public SecurityIdentifier? Target { get; }
		public PreconditionResult CreationAuthorized { get; }
		public string CreationDetail { get; }

		/// <summary>Plans an exchange area from the contract's identity pair. Preferred over the two-SID
		/// overload, so the same pair that decides the endpoint DACL decides this.</summary>
		public static ExchangeAreaPlan For(IdentityPair identities,string purpose)=>
			For((identities??throw new ArgumentNullException(nameof(identities))).Controller,identities.Target,purpose);

		/// <summary>Plans an exchange area for one operation, without touching the filesystem.</summary>
		public static ExchangeAreaPlan For(SecurityIdentifier controller,SecurityIdentifier? target,string purpose) {
			if(controller is null) throw new ArgumentNullException(nameof(controller));
			if(String.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A purpose is required.",nameof(purpose));
			var leaf=purpose+"-"+Guid.NewGuid().ToString("N");

			// Identities match: the debugger's own temp directory, which is exactly today's behaviour.
			// This is the overwhelmingly common case and it must not change.
			if(target is not null&&controller.Equals(target))
				return new ExchangeAreaPlan(Path.Combine(Path.GetTempPath(),leaf),false,controller,target,
					Directory.Exists(Path.GetTempPath())?PreconditionResult.Satisfied:PreconditionResult.Failed,
					"Controller and target are the same principal, so the controller's temp directory is shared by construction.");

			// The target could not be identified. Falling back to the controller's temp is precisely the
			// assumption that failed, so this is reported rather than assumed away.
			if(target is null)
				return new ExchangeAreaPlan(Path.Combine(Path.GetTempPath(),leaf),false,controller,null,
					PreconditionResult.NotProvablePreflight,
					"The target's identity could not be read, so whether it shares the controller's temp directory is unknown.");

			// Identities differ: a machine-wide root, because neither principal's profile is reachable
			// by the other. ProgramData is the standard location for exactly this and exists on every
			// Windows installation.
			var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"dgSpy","exchange",leaf);
			return new ExchangeAreaPlan(root,true,controller,target,CreationAuthority(root),
				// Prospective tense on purpose. This detail is read out of a preflight that has created
				// nothing, and a plan described in the present tense reads as an accomplished fact - which
				// is the confusion this whole contract exists to remove.
				"Controller and target differ, so a machine-wide directory would be created for this operation and removed with it.");
		}

		/// <summary>Whether creating the root would be authorized.
		///
		/// <para>This deliberately answers <see cref="PreconditionResult.NotProvablePreflight"/> in the
		/// normal case. Windows decides creation authority from the effective token against the parent's
		/// DACL, and reading that DACL and reasoning about it is the mistake the vocabulary section of
		/// this task warns about: inspecting an ACL is not an access check. The honest preflight answer
		/// is that only the attempt settles it, and the attempt is a mutation preflight may not make.</para>
		///
		/// <para>What can be decided without mutating is the narrower question of whether an existing
		/// ancestor is visible at all, which rules out a missing or unreachable root.</para></summary>
		static PreconditionResult CreationAuthority(string root) {
			var ancestor=Path.GetDirectoryName(Path.GetFullPath(root));
			while(!String.IsNullOrEmpty(ancestor)&&!Directory.Exists(ancestor)) ancestor=Path.GetDirectoryName(ancestor);
			if(String.IsNullOrEmpty(ancestor)) return PreconditionResult.Failed;
			try { Directory.GetDirectories(ancestor,"*",SearchOption.TopDirectoryOnly); }
			catch(UnauthorizedAccessException) { return PreconditionResult.Failed; }
			catch(IOException) { return PreconditionResult.Failed; }
			return PreconditionResult.NotProvablePreflight;
		}

		/// <summary>Creates the directory this plan describes and applies the intended DACL.</summary>
		public ExchangeArea Materialize()=>ExchangeArea.Create(this);
	}

	public sealed class ExchangeArea : IDisposable {
		bool preserved;

		ExchangeArea(string path,bool crossIdentity,PreconditionResult targetEffectiveAccess,string targetEffectiveAccessDetail) {
			Path=path; CrossIdentity=crossIdentity; TargetEffectiveAccess=targetEffectiveAccess; TargetEffectiveAccessDetail=targetEffectiveAccessDetail;
		}

		public string Path { get; }
		public bool CrossIdentity { get; }

		/// <summary>Whether the target can actually use this directory.
		///
		/// <para>Always <see cref="PreconditionResult.NotProvablePreflight"/> for a cross-identity area,
		/// and that is not a gap to paper over. SID equality does not establish effective access, and
		/// neither does reading back a DACL we just wrote: restricted SIDs, deny-only groups, deny ACEs,
		/// integrity level and impersonation state all sit between an authored grant and a usable one.
		/// Effective access is queried against the target's token or it is not established, and the
		/// proof that actually matters is the target reading the payload.</para></summary>
		public PreconditionResult TargetEffectiveAccess { get; }
		public string TargetEffectiveAccessDetail { get; }

		internal static ExchangeArea Create(ExchangeAreaPlan plan) {
			if(plan is null) throw new ArgumentNullException(nameof(plan));
			Directory.CreateDirectory(plan.Root);
			if(!plan.CrossIdentity||plan.Target is null)
				return new ExchangeArea(plan.Root,false,PreconditionResult.Satisfied,"Controller and target are the same principal.");

			var security=new DirectorySecurity();
			// Protected and fully enumerated: the grants are exactly the identity pair and nothing is
			// inherited from ProgramData, which grants Users read by default.
			security.SetAccessRuleProtection(true,false);
			security.SetOwner(plan.Controller);
			security.AddAccessRule(new FileSystemAccessRule(plan.Controller,FileSystemRights.FullControl,
				InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
			// The target reads the payload, which needs execute as well as read because a DLL is mapped,
			// and writes its completion report back into the same directory.
			//
			// Delete is part of writing that report, not an extra authority: the resident writes
			// completion.txt.tmp and renames it over completion.txt, so that a reader never sees a partial
			// report. A rename is a delete of the source name, and Write alone does not grant it. Without
			// it the resident initializes perfectly and then cannot publish - proven live on 2026-08-20,
			// where an IIS worker left completion.txt.tmp holding status=ok beside an Access-denied and the
			// caller saw only a twenty-second timeout. The grant stays inside one per-operation directory
			// whose DACL is protected and names only the identity pair.
			security.AddAccessRule(new FileSystemAccessRule(plan.Target,FileSystemRights.ReadAndExecute|FileSystemRights.Write|FileSystemRights.Delete,
				InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
			new DirectoryInfo(plan.Root).SetAccessControl(security);

			var applied=ReadBack(plan);
			return new ExchangeArea(plan.Root,true,PreconditionResult.NotProvablePreflight,
				applied
					?"The intended DACL was applied and read back. That is intent, not access: whether the target's effective token can use it is proved by the target reading the payload."
					:"The DACL read back does not match what was applied, so this directory is not known to grant what was intended.");
		}

		/// <summary>Reads the DACL back rather than trusting the write, so a directory whose grants were
		/// overridden is detected here instead of at the target's loader.</summary>
		static bool ReadBack(ExchangeAreaPlan plan) {
			try {
				var rules=new DirectoryInfo(plan.Root).GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier))
					.Cast<FileSystemAccessRule>().ToArray();
				var grantsController=rules.Any(rule=>rule.AccessControlType==AccessControlType.Allow&&rule.IdentityReference.Equals(plan.Controller));
				var grantsTarget=rules.Any(rule=>rule.AccessControlType==AccessControlType.Allow&&rule.IdentityReference.Equals(plan.Target));
				return grantsController&&grantsTarget;
			}
			catch(Exception) { return false; }
		}

		/// <summary>Keeps the directory when this is disposed, for a failure whose cause is not yet known.
		///
		/// <para>The extension used to delete its staging directory unconditionally in a finally block,
		/// which destroyed the evidence every single time this failed - including on the live worker,
		/// where the staged files were the only way to see what the target had been offered.</para></summary>
		public void Preserve()=>preserved=true;

		public void Dispose() {
			if(preserved) return;
			try { if(Directory.Exists(Path)) Directory.Delete(Path,true); } catch { }
		}
	}
}
