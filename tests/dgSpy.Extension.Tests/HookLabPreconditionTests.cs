using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using dgSpy.Extension;

using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>The target environment contract's precondition matrix, as a contract rather than as a sequence of throws discovered
/// one deployment at a time. Every row has to be reachable as a named failure, and no row may degrade to
/// satisfied because it could not be evaluated.</summary>
public sealed class HookLabPreconditionTests {
	static readonly Guid DesktopClr=new Guid("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");

	/// <summary>A target that satisfies everything a preflight can decide. Individual tests spoil one
	/// fact each, so a failure names the fact rather than the fixture.</summary>
	static HookLabPreconditions.Facts Healthy() =>
		new HookLabPreconditions.Facts {
			ProcessId=4242,
			Bitness=64,
			Architecture="X64",
			Runtimes=new[] { new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319") },
			ApplicationDomains=new[] { (1,"DefaultDomain") },
			TargetIdentityKnown=true,
			CrossIdentity=false,
			ExchangePlanned=true,
			ExchangeCreation=PreconditionOutcome.NotProvablePreflight,
			ExchangeCreationDetail="Creation authority under an existing root is not decidable by reading its DACL.",
			PayloadVerified=true,
		};

	static HookLabPreconditions.Precondition Row(HookLabPreconditions.Report report,string name) =>
		report.Preconditions.Single(precondition=>precondition.Name==name);

	[Fact]
	public void A_supported_target_refuses_nothing_and_still_promises_nothing_about_loading() {
		var report=HookLabPreconditions.Evaluate(Healthy());
		Assert.False(report.Refuses);
		Assert.Null(report.FirstFailure);
		// The exact wording the road demands. "ready", "will load" and "loadable" are all promises this
		// computation is not entitled to make, and the summary is where such a promise would creep in.
		Assert.Contains("no known incompatibility",report.Summary,StringComparison.Ordinal);
		Assert.DoesNotContain("loadable",report.Summary,StringComparison.OrdinalIgnoreCase);
		Assert.NotEmpty(report.NotProvable);
	}

	[Fact]
	public void An_unreadable_target_identity_refuses_by_name() {
		var facts=Healthy();
		facts.TargetIdentityKnown=false;
		facts.ExchangePlanned=false;
		facts.IdentityFailureDetail="The target process identity could not be read: access denied.";
		var report=HookLabPreconditions.Evaluate(facts);
		var row=Row(report,HookLabPreconditions.IdentityTargetReadable);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("hooklab_identity_unavailable",row.RefusalCode);
		Assert.Same(row,report.FirstFailure);
		// And the two rows that depend on it report that they could not be decided, rather than passing.
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.ExchangeCreationAuthorized).Result);
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.ExchangeTargetEffectiveAccess).Result);
	}

	[Theory]
	[InlineData(32,"X86")]
	[InlineData(64,"ARM64")]
	public void An_unsupported_architecture_refuses_by_name(int bitness,string architecture) {
		var facts=Healthy();
		facts.Bitness=bitness;
		facts.Architecture=architecture;
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.ArchitectureSupported);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Contains(architecture,row.Detail,StringComparison.Ordinal);
	}

	/// <summary>Architecture and runtime are answered independently. Reporting only the first failure
	/// would rebuild the serial hunt this contract replaces - five defects, each found only after the one
	/// before it was fixed.</summary>
	[Fact]
	public void A_target_that_fails_two_preconditions_reports_both() {
		var facts=Healthy();
		facts.Bitness=32;
		facts.Architecture="X86";
		facts.Runtimes=Array.Empty<HookLabRuntimeIdentity>();
		var report=HookLabPreconditions.Evaluate(facts);
		Assert.Equal(PreconditionOutcome.Failed,Row(report,HookLabPreconditions.ArchitectureSupported).Result);
		Assert.Equal(PreconditionOutcome.Failed,Row(report,HookLabPreconditions.RuntimeSupported).Result);
	}

	[Fact]
	public void A_process_with_no_supported_runtime_refuses_by_name() {
		var facts=Healthy();
		facts.Runtimes=new[] { new HookLabRuntimeIdentity(Guid.NewGuid(),"Mono") };
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.RuntimeSupported);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("unsupported_hooklab_target",row.RefusalCode);
		Assert.Contains("Mono",row.Detail,StringComparison.Ordinal);
	}

	/// <summary>The three ways the application-domain row fails, each proven live or refused live during
	/// the target environment contract, subslice 6.</summary>
	[Fact]
	public void Several_domains_and_none_named_refuses_and_lists_them() {
		var facts=Healthy();
		facts.ApplicationDomains=new[] { (1,"DefaultDomain"),(2,"/LM/W3SVC/1/ROOT-1-134316874119104946") };
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.ApplicationDomain);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("hooklab_application_domain_required",row.RefusalCode);
		Assert.Contains("2=/LM/W3SVC/1/ROOT-1-134316874119104946",row.Detail,StringComparison.Ordinal);
	}

	[Fact]
	public void A_named_domain_that_is_absent_refuses_by_name() {
		var facts=Healthy();
		facts.ApplicationDomains=new[] { (1,"DefaultDomain"),(2,"/LM/W3SVC/1/ROOT-1") };
		facts.RequestedApplicationDomain=7;
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.ApplicationDomain);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("hooklab_application_domain_not_found",row.RefusalCode);
	}

	[Fact]
	public void Two_domains_sharing_a_name_refuse_because_the_resident_selects_by_name() {
		var facts=Healthy();
		facts.ApplicationDomains=new[] { (1,"DefaultDomain"),(2,"SameName"),(3,"SameName") };
		facts.RequestedApplicationDomain=2;
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.ApplicationDomain);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("hooklab_application_domain_ambiguous",row.RefusalCode);
	}

	[Fact]
	public void A_named_domain_that_is_loaded_is_satisfied() {
		var facts=Healthy();
		facts.ApplicationDomains=new[] { (1,"DefaultDomain"),(2,"/LM/W3SVC/1/ROOT-1") };
		facts.RequestedApplicationDomain=2;
		var row=Row(HookLabPreconditions.Evaluate(facts),HookLabPreconditions.ApplicationDomain);
		Assert.Equal(PreconditionOutcome.Satisfied,row.Result);
	}

	[Fact]
	public void An_unverifiable_payload_refuses_by_name() {
		var facts=Healthy();
		facts.PayloadVerified=false;
		facts.PayloadFailureDetail="records 99cea2f0 while the layout records c7ff37b5, so the payload and its own manifest were replaced together.";
		var report=HookLabPreconditions.Evaluate(facts);
		var row=Row(report,HookLabPreconditions.PayloadReadableByTarget);
		Assert.Equal(PreconditionOutcome.Failed,row.Result);
		Assert.Equal("hooklab_payload_unavailable",row.RefusalCode);
		// Compatibility of a payload that was never verified is unexamined, not satisfied.
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.PayloadCompatibility).Result);
	}

	/// <summary>The rows that must never claim more than they know. A cross-identity grant is intent, not
	/// access; a DACL is not a round trip; and a reviewed import surface is not a loader outcome.</summary>
	[Fact]
	public void The_unprovable_rows_stay_unprovable_for_a_cross_identity_target() {
		var facts=Healthy();
		facts.CrossIdentity=true;
		var report=HookLabPreconditions.Evaluate(facts);
		Assert.False(report.Refuses);
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.ExchangeTargetEffectiveAccess).Result);
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.EndpointControllerReachable).Result);
		Assert.Equal(PreconditionOutcome.NotProvablePreflight,Row(report,HookLabPreconditions.PayloadCompatibility).Result);
		Assert.Contains("proved by the target reading the payload",Row(report,HookLabPreconditions.ExchangeTargetEffectiveAccess).Detail,StringComparison.Ordinal);
		Assert.Contains("round trip",Row(report,HookLabPreconditions.EndpointControllerReachable).Detail,StringComparison.Ordinal);
	}

	/// <summary>Same-user targets keep the behaviour they always had: the target's access to a directory
	/// the controller owns is the controller's own, and saying "not provable" there would be false
	/// caution rather than honesty.</summary>
	[Fact]
	public void A_same_user_target_has_provable_exchange_access() {
		Assert.Equal(PreconditionOutcome.Satisfied,Row(HookLabPreconditions.Evaluate(Healthy()),HookLabPreconditions.ExchangeTargetEffectiveAccess).Result);
	}

	/// <summary>The target environment contract asks subslice 7 to fix, per precondition, which unprovable results are hard
	/// refusals. All three answer "no", and this asserts that the answer is stated rather than inferred
	/// from the absence of a refusal: each is decided by the attempt itself and reported with a named
	/// failure when it goes wrong, so refusing up front would trade a diagnosable failure for a guess that
	/// no target ever works. A future row that does refuse changes this test, which is the point.</summary>
	[Fact]
	public void Every_unprovable_precondition_states_whether_it_refuses() {
		var report=HookLabPreconditions.Evaluate(Healthy());
		Assert.Equal(3,report.NotProvable.Count);
		Assert.All(report.NotProvable,precondition=>Assert.False(precondition.RefusesWhenUnprovable,precondition.Name+" refuses on an unprovable result."));
		Assert.False(report.Refuses);
	}

	/// <summary>Every row in the road's matrix is present and reachable. A contract that quietly stopped
	/// evaluating a precondition would otherwise report a clean result for a question nobody asked.</summary>
	[Fact]
	public void Every_precondition_in_the_matrix_is_reported() {
		var reported=HookLabPreconditions.Evaluate(Healthy()).Preconditions.Select(precondition=>precondition.Name).ToArray();
		Assert.Equal(new[] {
			HookLabPreconditions.IdentityTargetReadable,
			HookLabPreconditions.ArchitectureSupported,
			HookLabPreconditions.RuntimeSupported,
			HookLabPreconditions.ApplicationDomain,
			HookLabPreconditions.ExchangeCreationAuthorized,
			HookLabPreconditions.ExchangeTargetEffectiveAccess,
			HookLabPreconditions.PayloadReadableByTarget,
			HookLabPreconditions.PayloadCompatibility,
			HookLabPreconditions.EndpointControllerReachable,
		},reported);
		Assert.Equal(reported.Length,reported.Distinct(StringComparer.Ordinal).Count());
	}
}
