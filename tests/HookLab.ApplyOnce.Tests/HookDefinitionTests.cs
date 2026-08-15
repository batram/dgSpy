using System.Text.Json;
using HookLab.ApplyOnce;
using Xunit;

namespace HookLab.ApplyOnce.Tests;

public sealed class HookDefinitionTests {
	[Fact]
	public void Checked_in_vmconnect_definition_is_exact_and_valid() {
		var definition=HookDefinition.Load(Path.Combine(RepoRoot(),"tools","HookLab.ApplyOnce","definitions","vmconnect-fullscreen.json"));
		Assert.Equal("vmconnect-fullscreen-sync-v1",definition.Id);
		Assert.Equal("VmConnect.exe",definition.Process!.FileName);
		Assert.Equal("dc470534-4dc7-403f-a529-c2b0259fa97d",definition.Target!.ModuleMvid);
		Assert.Equal(0x06000155,definition.Target.MetadataToken);
		Assert.Equal("Prefix",definition.Hook!.Kind);
		Assert.Contains("SyncSessionDisplaySettings",definition.Hook.Source,StringComparison.Ordinal);
	}

	[Fact]
	public void Unknown_property_is_rejected() {
		var path=TemporaryDefinition("{\"schemaVersion\":1,\"id\":\"x\",\"unknown\":true}");
		try { var error=Assert.Throws<InvalidDataException>(()=>HookDefinition.Load(path)); Assert.Contains("JSON is invalid",error.Message,StringComparison.Ordinal); }
		finally { File.Delete(path); }
	}

	[Fact]
	public void Newline_and_oversized_source_are_rejected_before_injection() {
		var definition=Valid(); definition.Id="bad\nid";
		Assert.Contains("newline",Assert.Throws<InvalidDataException>(definition.Validate).Message,StringComparison.Ordinal);
		definition=Valid(); definition.Hook!.Source=new string('x',1537);
		Assert.Contains("bootstrap value limit",Assert.Throws<InvalidDataException>(definition.Validate).Message,StringComparison.Ordinal);
	}

	static HookDefinition Valid()=>new() {
		SchemaVersion=1,Id="test",Process=new ProcessDefinition { FileName="Target.exe" },
		Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="T",Method="M",MetadataToken=0x06000001,Signature="System.Void M()",IlSha256=new string('a',64) },
		Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static void Prefix(){}}",MaximumEventsPerSecond=1,MaximumStringLength=1 }
	};
	static string TemporaryDefinition(string text) { var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".json"); File.WriteAllText(path,text); return path; }
	static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null && !Directory.Exists(Path.Combine(current.FullName,"HookLab"))) current=current.Parent; return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."); }
}
