using System;
using dgSpy.Extension;
using HookLab.Contracts;
using Xunit;

/// <summary>
/// The rule that decides whether a recorded HookLab resident is "ours".
///
/// It is tested because getting one half of it wrong deletes residents. <c>DiscoverCore</c> responds
/// to "not current" by deleting the record or quarantining it, so if the process-identity check ever
/// starts considering the application domain, a perfectly valid resident living in another domain is
/// destroyed - silently, and only on machines that have more than one domain, which are exactly the
/// machines nobody tests on.
/// </summary>
public sealed class HookLabTargetMatchTests {
	const string HostId="test-resident-host";
	static readonly DateTime Created=new DateTime(2026,8,19,12,0,0,DateTimeKind.Utc);

	static TargetIdentity Identity(string appDomainId="1",int processId=4242,string hostId=HostId,string runtimeId="v4.0.30319",string image=@"C:\targets\worker.exe")=>
		new TargetIdentity(hostId,image,processId,Created,"x64",runtimeId,appDomainId);

	[Fact]
	public void The_same_resident_matches_itself_on_both_questions() {
		Assert.True(HookLabTargetMatch.SameProcess(Identity(),Identity(),HostId));
		Assert.True(HookLabTargetMatch.SameTarget(Identity(),Identity(),HostId));
	}

	/// <summary>The property the whole split exists for. A resident in another application domain is
	/// still a resident in this process, and discovery must not treat it as garbage.</summary>
	[Fact]
	public void A_resident_in_another_application_domain_is_still_the_same_process() {
		var recorded=Identity(appDomainId:"2");
		var live=Identity(appDomainId:"1");
		Assert.True(HookLabTargetMatch.SameProcess(recorded,live,HostId),
			"A record for another application domain must remain a valid record for this process, or DiscoverCore deletes or quarantines it.");
	}

	/// <summary>The other half. Selection must not hand back a resident from a different domain.</summary>
	[Fact]
	public void A_resident_in_another_application_domain_is_not_the_same_target() {
		Assert.False(HookLabTargetMatch.SameTarget(Identity(appDomainId:"2"),Identity(appDomainId:"1"),HostId));
	}

	[Theory]
	[InlineData(9999,"1","v4.0.30319",@"C:\targets\worker.exe")]                 // different process
	[InlineData(4242,"1","v4.0.30320",@"C:\targets\worker.exe")]                 // different runtime
	[InlineData(4242,"1","v4.0.30319",@"C:\targets\other.exe")]                  // different image
	public void Anything_else_that_differs_makes_it_a_different_process(int processId,string appDomainId,string runtimeId,string image) {
		var live=Identity(processId:processId,appDomainId:appDomainId,runtimeId:runtimeId,image:image);
		Assert.False(HookLabTargetMatch.SameProcess(Identity(),live,HostId));
		Assert.False(HookLabTargetMatch.SameTarget(Identity(),live,HostId));
	}

	[Fact]
	public void A_different_process_creation_time_is_a_different_process() {
		var recycled=new TargetIdentity(HostId,@"C:\targets\worker.exe",4242,Created.AddSeconds(1),"x64","v4.0.30319","1");
		Assert.False(HookLabTargetMatch.SameProcess(Identity(),recycled,HostId));
	}

	[Fact]
	public void The_image_path_comparison_ignores_case_and_path_form() {
		var live=Identity(image:@"C:\targets\..\targets\WORKER.EXE");
		Assert.True(HookLabTargetMatch.SameProcess(Identity(),live,HostId));
	}

	[Fact]
	public void Only_records_written_by_a_recognised_owner_match() {
		Assert.False(HookLabTargetMatch.SameProcess(Identity(hostId:"someone-elses-host"),Identity(),HostId));
		// ApplyOnce writes residents this host is allowed to adopt.
		Assert.True(HookLabTargetMatch.SameProcess(Identity(hostId:"apply-once"),Identity(),HostId));
	}

	/// <summary>Ownership is a property of the record, not of the live target we compare it against.</summary>
	[Fact]
	public void Ownership_is_read_from_the_record() {
		Assert.True(HookLabTargetMatch.OwnedBy(Identity(hostId:HostId),HostId));
		Assert.True(HookLabTargetMatch.OwnedBy(Identity(hostId:"apply-once"),HostId));
		Assert.False(HookLabTargetMatch.OwnedBy(Identity(hostId:"unknown"),HostId));
	}
}
