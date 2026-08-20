using System.Security.Principal;
using HookLab.Contracts;
using HookLab.Injector;
using Xunit;

/// <summary>
/// The controller and the target, read once and passed explicitly.
///
/// Both the exchange area and the resident's endpoint DACL are derived from this pair. They used to
/// compute "who is the controller" separately, from two call sites, which is two chances to disagree
/// about one fact - and under impersonation they would, leaving a pipe naming one principal and a
/// staging directory naming another.
/// </summary>
public sealed class IdentityPairTests {
	static SecurityIdentifier Me=>WindowsIdentity.GetCurrent().User!;
	static SecurityIdentifier Other=>new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);

	[Fact]
	public void Reading_the_pair_for_this_process_finds_this_user_on_both_sides() {
		using var self=System.Diagnostics.Process.GetCurrentProcess();
		var pair=IdentityPair.For(self.Id);
		Assert.Equal(Me,pair.Controller);
		Assert.Equal(Me,pair.Target);
		Assert.False(pair.CrossIdentity);
	}

	/// <summary>The same-user case must produce no controller_sid at all, so the probe's DACL stays
	/// byte-for-byte what it has always been.</summary>
	[Fact]
	public void Matching_identities_name_no_endpoint_principal() {
		Assert.Null(IdentityPair.Of(Me,Me).ControllerSidForEndpoint);
	}

	[Fact]
	public void Differing_identities_name_the_controller_for_the_endpoint() {
		var pair=IdentityPair.Of(Me,Other);
		Assert.True(pair.CrossIdentity);
		Assert.Equal(Me.Value,pair.ControllerSidForEndpoint);
	}

	/// <summary>An unreadable target is unknown, not different. Treating it as different would author a
	/// grant for a principal nobody established, and treating it as equal is the assumption that failed
	/// live - so it is neither.</summary>
	[Fact]
	public void An_unknown_target_is_not_cross_identity_and_names_no_endpoint_principal() {
		var pair=IdentityPair.Of(Me,null);
		Assert.Null(pair.Target);
		Assert.False(pair.CrossIdentity);
		Assert.Null(pair.ControllerSidForEndpoint);
	}

	/// <summary>One pair, both decisions. This is the property the type exists for.</summary>
	[Fact]
	public void The_exchange_area_and_the_endpoint_derive_from_the_same_pair() {
		var pair=IdentityPair.Of(Me,Other);
		var plan=ExchangeAreaPlan.For(pair,"dgspy-test");
		Assert.Equal(pair.Controller,plan.Controller);
		Assert.Equal(pair.Target,plan.Target);
		Assert.Equal(pair.CrossIdentity,plan.CrossIdentity);
		Assert.Equal(pair.Controller.Value,pair.ControllerSidForEndpoint);
	}

	[Fact]
	public void Bootstrap_parameters_carry_the_controller_only_for_a_pipe_endpoint() {
		var definition=SampleDefinition();
		var withPipe=OneShotInjector.Parameters("C:\\Target.exe",123,456,"C:\\completion.txt",definition,"pipe",new byte[32],null,Me.Value);
		Assert.Contains("controller_sid="+Me.Value,withPipe,StringComparison.Ordinal);
		// BootstrapParameters refuses controller_sid without endpoint=pipe, so emitting it for a
		// non-pipe endpoint would produce parameters the target rejects outright.
		var withoutPipe=OneShotInjector.Parameters("C:\\Target.exe",123,456,"C:\\completion.txt",definition,"none",null,null,Me.Value);
		Assert.DoesNotContain("controller_sid=",withoutPipe,StringComparison.Ordinal);
	}

	[Fact]
	public void Bootstrap_parameters_omit_the_controller_when_none_is_supplied() {
		var text=OneShotInjector.Parameters("C:\\Target.exe",123,456,"C:\\completion.txt",SampleDefinition(),"pipe",new byte[32]);
		Assert.DoesNotContain("controller_sid=",text,StringComparison.Ordinal);
	}

	static HookDefinition SampleDefinition()=>new HookDefinition {
		SchemaVersion=1,Id="sample",Process=new ProcessDefinition { FileName="Target.exe" },
		Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="Example.Target",Method="Run",MetadataToken=0x06000001,Signature="System.Int32 Run(System.Int32)",IlSha256=new string('a',64) },
		Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static bool Prefix(){return true;}}",MaximumEventsPerSecond=9,MaximumStringLength=99 }
	};
}
