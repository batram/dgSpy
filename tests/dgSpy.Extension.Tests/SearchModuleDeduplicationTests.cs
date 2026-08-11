using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class SearchModuleDeduplicationTests {
	[Fact]
	public void Same_metadata_instance_is_walked_once() {
		var policy=new SearchModuleDeduplication();
		var instance=new object();
		Assert.True(policy.TryAdd(instance,null,null,null));
		Assert.False(policy.TryAdd(instance,null,null,null));
	}

	[Fact]
	public void Same_mvid_from_two_runtime_views_is_walked_once() {
		var policy=new SearchModuleDeduplication();
		var mvid=Guid.NewGuid();
		Assert.True(policy.TryAdd(new object(),mvid,@"C:\one\System.dll",null));
		Assert.False(policy.TryAdd(new object(),mvid,@"C:\two\System.dll",null));
	}

	[Fact]
	public void Same_path_from_debugger_and_document_views_is_walked_once() {
		var policy=new SearchModuleDeduplication();
		Assert.True(policy.TryAdd(new object(),Guid.NewGuid(),@"C:\player\Managed\Assembly-CSharp.dll",null));
		Assert.False(policy.TryAdd(new object(),Guid.NewGuid(),@"c:\player\Managed\.\Assembly-CSharp.dll",null));
	}

	[Fact]
	public void Same_full_assembly_identity_from_different_views_is_walked_once() {
		var policy=new SearchModuleDeduplication();
		const string identity="System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=abc";
		Assert.True(policy.TryAdd(new object(),Guid.NewGuid(),@"C:\player\Managed\System.dll",identity));
		Assert.False(policy.TryAdd(new object(),Guid.NewGuid(),@"C:\reference\System.dll",identity));
	}

	[Fact]
	public void Unrelated_modules_are_not_collapsed_by_display_name() {
		var policy=new SearchModuleDeduplication();
		Assert.True(policy.TryAdd(new object(),Guid.NewGuid(),@"C:\one\Helpers.dll","Helpers, Version=1.0.0.0"));
		Assert.True(policy.TryAdd(new object(),Guid.NewGuid(),@"C:\two\Helpers.dll","Helpers, Version=2.0.0.0"));
	}
}
