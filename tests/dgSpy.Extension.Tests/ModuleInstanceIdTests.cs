using System;
using dgSpy.Extension;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class ModuleInstanceIdTests {
	static readonly Guid Runtime=new("cd03acdd-4f3a-4736-8591-4902b4dcc8c1");
	const string Session="11859180ea4d44a99eb658e003cca953";

	[Fact] public void Identity_round_trips_every_runtime_discriminator() {
		var id=ModuleInstanceId.Create(Session,19424,Runtime,3,17);
		Assert.True(ModuleInstanceId.TryParse(id,out var session,out var process,out var runtime,out var appDomain,out var order));
		Assert.Equal(Session,session);
		Assert.Equal(19424,process); Assert.Equal(Runtime,runtime); Assert.Equal(3,appDomain); Assert.Equal(17,order);
	}

	[Fact] public void The_same_file_in_two_app_domains_has_two_identities() =>
		Assert.NotEqual(ModuleInstanceId.Create(Session,19424,Runtime,2,17),ModuleInstanceId.Create(Session,19424,Runtime,3,17));

	[Fact] public void Reattaching_invalidates_every_previous_module_identity() =>
		Assert.NotEqual(ModuleInstanceId.Create(Session,19424,Runtime,3,17),ModuleInstanceId.Create("21859180ea4d44a99eb658e003cca953",19424,Runtime,3,17));

	[Fact] public void A_process_module_without_an_app_domain_round_trips() {
		var id=ModuleInstanceId.Create(Session,42,Runtime,null,1);
		Assert.True(ModuleInstanceId.TryParse(id,out _,out _,out _,out var appDomain,out _));
		Assert.Null(appDomain);
	}

	[Theory]
	[InlineData(null)] [InlineData("")] [InlineData("System.dll")] [InlineData("dm1:not-session:1:cd03acdd4f3a473685914902b4dcc8c1:-1:1")]
	[InlineData("dm1:11859180ea4d44a99eb658e003cca953:1:not-a-guid:-1:1")] [InlineData("dm1:11859180ea4d44a99eb658e003cca953:1:cd03acdd4f3a473685914902b4dcc8c1:-2:1")]
	public void Malformed_or_impossible_identities_are_rejected(string? value) =>
		Assert.False(ModuleInstanceId.TryParse(value,out _,out _,out _,out _,out _));
}
