using System.Text.Json;
using HookLab.ApplyOnce;
using Xunit;

namespace HookLab.ApplyOnce.Tests;

public sealed class HookDefinitionTests {
	[Fact]
	public void Hook_source_accepts_normal_source_file_line_breaks() { var definition=Valid(); definition.Hook!.Source="public static class H{\r\npublic static bool Prefix(){return true;}\r\n}"; definition.Validate(); }

	[Fact]
	public void Checked_in_vmconnect_definition_is_exact_and_valid() {
		var definition=HookDefinition.Load(Path.Combine(RepoRoot(),"tools","HookLab.ApplyOnce","definitions","vmconnect-fullscreen.json"));
		Assert.Equal("vmconnect-fullscreen-sync-v1",definition.Id);
		Assert.Equal("VmConnect.exe",definition.Process!.FileName);
		Assert.Equal("dc470534-4dc7-403f-a529-c2b0259fa97d",definition.Target!.ModuleMvid);
		Assert.Equal(0x06000155,definition.Target.MetadataToken);
		Assert.Equal("Prefix",definition.Hook!.Kind);
		Assert.True(definition.Hook.Enabled);
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

	[Theory]
	[InlineData("schema")]
	[InlineData("id")]
	[InlineData("process")]
	[InlineData("process_path")]
	[InlineData("target")]
	[InlineData("assembly")]
	[InlineData("mvid")]
	[InlineData("type")]
	[InlineData("method")]
	[InlineData("token")]
	[InlineData("signature")]
	[InlineData("sha")]
	[InlineData("hook")]
	[InlineData("kind")]
	[InlineData("revision")]
	[InlineData("source")]
	[InlineData("event_limit")]
	[InlineData("string_limit")]
	public void Every_required_definition_contract_fails_closed(string contract) {
		var definition=Valid();
		switch(contract) {
			case "schema": definition.SchemaVersion=2; break;
			case "id": definition.Id=""; break;
			case "process": definition.Process=null; break;
			case "process_path": definition.Process!.FileName="folder\\Target.exe"; break;
			case "target": definition.Target=null; break;
			case "assembly": definition.Target!.Assembly=""; break;
			case "mvid": definition.Target!.ModuleMvid="not-a-guid"; break;
			case "type": definition.Target!.DeclaringType=""; break;
			case "method": definition.Target!.Method=""; break;
			case "token": definition.Target!.MetadataToken=0; break;
			case "signature": definition.Target!.Signature=""; break;
			case "sha": definition.Target!.IlSha256="abc"; break;
			case "hook": definition.Hook=null; break;
			case "kind": definition.Hook!.Kind="Around"; break;
			case "revision": definition.Hook!.Revision=0; break;
			case "source": definition.Hook!.Source=""; break;
			case "event_limit": definition.Hook!.MaximumEventsPerSecond=0; break;
			case "string_limit": definition.Hook!.MaximumStringLength=0; break;
		}
		Assert.Throws<InvalidDataException>(definition.Validate);
	}

	[Theory]
	[InlineData("empty")]
	[InlineData("incomplete")]
	[InlineData("zero_pid")]
	[InlineData("unknown")]
	[InlineData("duplicate")]
	public void Invalid_command_lines_are_rejected(string scenario) {
		var values=scenario switch {
			"empty"=>Array.Empty<string>(),
			"incomplete"=>new[]{"--pid","1"},
			"zero_pid"=>new[]{"--pid","0","--definition","x.json"},
			"unknown"=>new[]{"--pid","1","--definition","x.json","--unknown","value"},
			_=>new[]{"--pid","1","--pid","2"}
		};
		Assert.False(Arguments.TryParse(values,out _));
	}

	[Fact]
	public void Command_line_order_is_not_positional() {
		Assert.True(Arguments.TryParse(new[]{"--definition","hook.json","--pid","42","--payload-dir","payload"},out var parsed));
		Assert.Equal(42,parsed.ProcessId);
		Assert.Equal("hook.json",parsed.DefinitionPath);
		Assert.Equal(Path.GetFullPath("payload"),parsed.PayloadDirectory);
	}

	[Fact]
	public void Exited_process_is_classified_as_gone_for_native_failure_reconciliation() {
		using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe","/c exit 0") { UseShellExecute=false,CreateNoWindow=true })!;
		Assert.True(process.WaitForExit(5000)); Assert.True(OneShotInjector.HasExited(process));
	}

	static HookDefinition Valid()=>new() {
		SchemaVersion=1,Id="test",Process=new ProcessDefinition { FileName="Target.exe" },
		Target=new TargetDefinition { Assembly="Target",ModuleMvid=Guid.NewGuid().ToString("D"),DeclaringType="T",Method="M",MetadataToken=0x06000001,Signature="System.Void M()",IlSha256=new string('a',64) },
		Hook=new PatchDefinition { Kind="Prefix",Revision=1,Source="public static class H{public static void Prefix(){}}",MaximumEventsPerSecond=1,MaximumStringLength=1 }
	};
	static string TemporaryDefinition(string text) { var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".json"); File.WriteAllText(path,text); return path; }
	static string RepoRoot() { var current=new DirectoryInfo(AppContext.BaseDirectory); while(current is not null && !Directory.Exists(Path.Combine(current.FullName,"HookLab"))) current=current.Parent; return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."); }
}
