using System;
using System.IO;
using Xunit;

namespace dgSpy.Composition.Tests;

public sealed class PublishedHostLocationTests {
	[Fact]
	public void Completed_pipeline_layout_is_discovered_without_an_environment_override() {
		var root=Path.Combine(Path.GetTempPath(),"dgspy-composition-location-"+Guid.NewGuid().ToString("N"));
		try {
			var bin=Path.Combine(root,"artifacts","layouts","local","bin");
			Directory.CreateDirectory(bin);
			File.WriteAllText(Path.Combine(root,"artifacts","layouts","local","dgspy-layout.json"),"{}");
			File.WriteAllText(Path.Combine(bin,"dnSpy.Contracts.DnSpy.dll"),String.Empty);
			var start=Path.Combine(root,"tests","dgSpy.Composition.Tests","bin","Release","net10.0-windows");
			Directory.CreateDirectory(start);

			Assert.Equal(bin,PublishedHost.LocateBinDirectory(null,start));
		}
		finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
	}

	[Fact]
	public void Explicit_invalid_override_does_not_silently_select_another_host() {
		var root=Path.Combine(Path.GetTempPath(),"dgspy-composition-location-"+Guid.NewGuid().ToString("N"));
		try {
			Directory.CreateDirectory(root);
			Assert.Null(PublishedHost.LocateBinDirectory(Path.Combine(root,"missing"),root));
		}
		finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
	}
}
