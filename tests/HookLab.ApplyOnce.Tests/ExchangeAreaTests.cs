using System.Security.AccessControl;
using System.Security.Principal;
using HookLab.Injector;
using Xunit;

/// <summary>
/// The exchange area: one filesystem location derived from the controller and target identities,
/// rather than from whichever of them happened to call Path.GetTempPath().
///
/// A second Windows account is not needed to test most of this. Authoring and reading back a
/// two-principal DACL works with any real SID, so a well-known one stands in for the target. What a
/// second account WOULD add is the one thing this deliberately does not claim - that the target's
/// effective token can use the directory - and that is asserted here as not provable.
/// </summary>
public sealed class ExchangeAreaTests {
	static SecurityIdentifier Me=>WindowsIdentity.GetCurrent().User!;
	/// <summary>A real, resolvable principal that is not the current user. Well-known, so it exists on
	/// every machine and needs no account creation.</summary>
	static SecurityIdentifier Other=>new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);

	[Fact]
	public void Matching_identities_plan_the_controllers_own_temp_directory() {
		var plan=ExchangeAreaPlan.For(Me,Me,"dgspy-test");
		Assert.False(plan.CrossIdentity);
		Assert.StartsWith(Path.GetTempPath(),plan.Root,StringComparison.OrdinalIgnoreCase);
		Assert.Equal(PreconditionResult.Satisfied,plan.CreationAuthorized);
	}

	[Fact]
	public void Differing_identities_plan_a_machine_wide_directory() {
		var plan=ExchangeAreaPlan.For(Me,Other,"dgspy-test");
		Assert.True(plan.CrossIdentity);
		Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),plan.Root,StringComparison.OrdinalIgnoreCase);
		// Neither principal's profile is reachable by the other, so the controller's temp is exactly
		// the wrong answer here - which is the bug this replaces.
		Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),plan.Root,StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Planning touches nothing. The contract has to be computable before the directory
	/// exists, or the acting path cannot be gated on it.</summary>
	[Fact]
	public void Planning_creates_nothing() {
		var plan=ExchangeAreaPlan.For(Me,Other,"dgspy-test");
		Assert.False(Directory.Exists(plan.Root));
	}

	/// <summary>An unidentifiable target is reported, not assumed to match. Assuming it matched is what
	/// staged the payload into a directory the target could not read.</summary>
	[Fact]
	public void An_unreadable_target_identity_is_not_provable_rather_than_assumed_equal() {
		var plan=ExchangeAreaPlan.For(Me,null,"dgspy-test");
		Assert.Equal(PreconditionResult.NotProvablePreflight,plan.CreationAuthorized);
		Assert.Contains("could not be read",plan.CreationDetail,StringComparison.Ordinal);
	}

	/// <summary>Creation authority is honestly unprovable rather than guessed from the parent's DACL.
	/// Reading an ACL is not an access check, and this task's vocabulary section says so explicitly.</summary>
	[Fact]
	public void Creation_authority_under_an_existing_root_is_not_provable_in_advance() {
		Assert.Equal(PreconditionResult.NotProvablePreflight,ExchangeAreaPlan.For(Me,Other,"dgspy-test").CreationAuthorized);
	}

	[Fact]
	public void A_cross_identity_area_grants_exactly_the_identity_pair_and_breaks_inheritance() {
		var plan=ExchangeAreaPlan.For(Me,Other,"dgspy-test");
		using var area=plan.Materialize();
		try {
			Assert.True(Directory.Exists(area.Path));
			var security=new DirectoryInfo(area.Path).GetAccessControl();
			Assert.True(security.AreAccessRulesProtected,"ProgramData grants Users read by inheritance; an unprotected DACL would silently widen this.");
			var granted=security.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
				.Where(rule=>rule.AccessControlType==AccessControlType.Allow)
				.Select(rule=>(SecurityIdentifier)rule.IdentityReference).Distinct().ToArray();
			Assert.Contains(Me,granted);
			Assert.Contains(Other,granted);
			Assert.Equal(2,granted.Length);
		}
		finally { area.Dispose(); }
	}

	/// <summary>The target's grant has to cover the protocol the target is asked to perform, and that
	/// includes the rename in write-then-rename: the resident writes completion.txt.tmp and moves it over
	/// completion.txt so no reader ever sees a partial report. A rename deletes the source name, and Write
	/// does not grant Delete.
	///
	/// Proven live on 2026-08-20: an IIS worker initialized in its application domain, wrote
	/// status=ok into completion.txt.tmp, failed the rename with Access denied, and the caller saw a bare
	/// twenty-second timeout. This asserts the intended grant only - whether the token can use it stays
	/// not provable, as everything else here does.</summary>
	[Fact]
	public void The_target_may_perform_the_write_then_rename_its_completion_report_needs() {
		var plan=ExchangeAreaPlan.For(Me,Other,"dgspy-test");
		using var area=plan.Materialize();
		var granted=new DirectoryInfo(area.Path).GetAccessControl()
			.GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
			.Where(rule=>rule.AccessControlType==AccessControlType.Allow&&rule.IdentityReference.Equals(Other))
			.Aggregate(default(FileSystemRights),(rights,rule)=>rights|rule.FileSystemRights);
		Assert.True(granted.HasFlag(FileSystemRights.Write),"the target writes its completion report");
		Assert.True(granted.HasFlag(FileSystemRights.ReadAndExecute),"the target reads and maps the payload");
		Assert.True(granted.HasFlag(FileSystemRights.Delete),"a rename is a delete of the source name, so write-then-rename needs it");
	}

	/// <summary>The claim this type refuses to make. Everything above proves intent was recorded; none
	/// of it proves the target can act, and saying otherwise is how a road full of named refusals grows
	/// a silent pass.</summary>
	[Fact]
	public void Creating_the_area_does_not_claim_the_target_can_use_it() {
		var plan=ExchangeAreaPlan.For(Me,Other,"dgspy-test");
		using var area=plan.Materialize();
		Assert.True(area.CrossIdentity);
		Assert.Equal(PreconditionResult.NotProvablePreflight,area.TargetEffectiveAccess);
		Assert.Contains("intent, not access",area.TargetEffectiveAccessDetail,StringComparison.Ordinal);
	}

	[Fact]
	public void A_same_identity_area_is_created_and_removed_with_the_operation() {
		string path;
		using(var area=ExchangeAreaPlan.For(Me,Me,"dgspy-test").Materialize()) {
			path=area.Path;
			Assert.True(Directory.Exists(path));
			File.WriteAllText(Path.Combine(path,"payload.txt"),"staged");
		}
		Assert.False(Directory.Exists(path),"An exchange area is created for one operation and removed with it.");
	}

	/// <summary>Preserve-on-ambiguity. The extension used to delete unconditionally in a finally block,
	/// which destroyed the staged files every time initialization failed - exactly when they were the
	/// only evidence of what the target had been offered.</summary>
	[Fact]
	public void A_preserved_area_survives_disposal_so_a_failure_can_be_inspected() {
		string path;
		var area=ExchangeAreaPlan.For(Me,Me,"dgspy-test").Materialize();
		try {
			path=area.Path;
			File.WriteAllText(Path.Combine(path,"initialize.params"),"appdomain_name=example");
			area.Preserve();
			area.Dispose();
			Assert.True(Directory.Exists(path));
			Assert.True(File.Exists(Path.Combine(path,"initialize.params")));
		}
		finally { try { Directory.Delete(area.Path,true); } catch { } }
	}

	[Fact]
	public void Disposing_twice_is_harmless() {
		var area=ExchangeAreaPlan.For(Me,Me,"dgspy-test").Materialize();
		area.Dispose();
		area.Dispose();
		Assert.False(Directory.Exists(area.Path));
	}

	[Fact]
	public void The_current_process_identity_is_readable_and_is_this_user() {
		Assert.Equal(Me,ProcessIdentity.TryGetCurrentSid());
		using var self=System.Diagnostics.Process.GetCurrentProcess();
		Assert.Equal(Me,ProcessIdentity.TryGetUserSid(self.Id));
	}

	[Fact]
	public void An_unreadable_process_yields_null_rather_than_a_wrong_answer() {
		// PID 4 is System: present on every Windows machine and not openable, even elevated.
		Assert.Null(ProcessIdentity.TryGetUserSid(4));
	}
}
