using Xunit;

/// <summary>Locates the real HookLab payload for tests that must reason about the bytes dgSpy actually
/// injects. A composed layout is preferred because that is the shipping artifact; the freshly built
/// bootstrap is accepted so the payload matrix can be checked before a full pipeline run.</summary>
static class HookLabPayload {
	internal const string FileName="hooklab-bootstrap.net48.payload";

	internal static string Path() {
		var candidates=Candidates().ToArray();
		var found=candidates.FirstOrDefault(File.Exists);
		Assert.True(found is not null,
			"No HookLab payload to verify. Build it first: dotnet run --project Build/DgSpyTool -- pipeline"+Environment.NewLine+
			"Looked at: "+String.Join(Environment.NewLine+"  ",candidates));
		return found!;
	}

	static IEnumerable<string> Candidates() {
		var configured=Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		if(!String.IsNullOrWhiteSpace(configured)) yield return System.IO.Path.Combine(configured,"hooklab",FileName);
		var repo=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var layouts=System.IO.Path.Combine(repo,"artifacts","layouts");
		if(Directory.Exists(layouts))
			foreach(var directory in new DirectoryInfo(layouts).GetDirectories().OrderByDescending(directory=>directory.LastWriteTimeUtc))
				yield return System.IO.Path.Combine(directory.FullName,"hooklab",FileName);
		yield return System.IO.Path.Combine(repo,"HookLab","HookLab.Bootstrap","bin","Release","net48","HookLab.Bootstrap.dll");
	}
}
