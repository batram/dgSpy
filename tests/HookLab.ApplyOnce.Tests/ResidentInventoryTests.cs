using HookLab.Contracts;
using HookLab.Host.Transport.Discovery;
using HookLab.Injector;
using Xunit;

namespace HookLab.ApplyOnce.Tests;

public sealed class ResidentInventoryTests {
	[Fact]
	public void Unauthenticated_legacy_resident_evidence_refuses_a_second_injection() {
		var root=Path.Combine(Path.GetTempPath(),"hooklab-legacy-recovery-"+Guid.NewGuid().ToString("N"));
		try {
			new ResidentPayloadStore(root).Create(123,456);
			var target=new TargetIdentity("hooklab-resident",@"C:\Target.exe",123,new DateTime(456,DateTimeKind.Utc),"x64","v4.0.30319","1");
			var error=Assert.Throws<UnregisteredResidentException>(()=>new LegacyResidentRecovery(root).FindOrRecover(target,new ExactTarget()));
			Assert.Contains("Refusing a second injection",error.Message,StringComparison.Ordinal);
		}
		finally { try { Directory.Delete(root,true); } catch { } }
	}
	[Fact]
	public void ResidentInventoryHasOneStrictSharedInterpretation() {
		const string payload="""
			{"probe_instance_id":"probe","hooks_version":7,"compiled_hooks":[{"patch_id":"probe:dgspy:alpha","assembly_simple_name":"Target","kind":"Postfix","module_mvid":"11111111-2222-3333-4444-555555555555","metadata_token":100663297,"declaring_type":"Target.Type","signature":"System.Int32 Work()","il_sha256":"abc","source_sha256":"def","revision":3,"enabled":true}],"shadowed_hooks":[{"patch_id":"probe:dgspy:alpha","declaring_type":"Target.Type","shadowing_assembly":"Target.v2"}]}
			""";
		var inventory=ResidentInventoryParser.Parse(payload,"probe");
		Assert.Equal(7,inventory.HooksVersion);
		var hook=Assert.Single(inventory.Hooks);
		Assert.Equal("dgspy",hook.Controller); Assert.Equal("alpha",hook.HookId); Assert.Equal(HookKind.Postfix,hook.Kind);
		Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"),hook.Target.ModuleMvid);
		Assert.Equal((uint)100663297,hook.Target.MetadataToken); Assert.True(hook.Enabled);
		var shadowed=Assert.Single(inventory.ShadowedHooks); Assert.Equal("Target.v2",shadowed.ShadowingAssembly);
	}

	[Fact]
	public void ResidentInventoryRefusesAStatusFromAnotherAuthenticatedResident() {
		const string payload="{\"probe_instance_id\":\"other\",\"hooks_version\":0,\"compiled_hooks\":[]}";
		var error=Assert.Throws<ResidentIdentityMismatchException>(()=>ResidentInventoryParser.Parse(payload,"expected"));
		Assert.Contains("other",error.Message,StringComparison.Ordinal); Assert.Contains("expected",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void ResidentInventoryRefusesMalformedHookIdentityInsteadOfPartiallyAdoptingIt() {
		const string payload="{\"probe_instance_id\":\"probe\",\"hooks_version\":0,\"compiled_hooks\":[{\"patch_id\":\"probe:dgspy:alpha\",\"assembly_simple_name\":\"Target\",\"kind\":\"Postfix\",\"module_mvid\":\"not-a-guid\",\"metadata_token\":1,\"declaring_type\":\"T\",\"signature\":\"S\",\"il_sha256\":\"a\",\"source_sha256\":\"b\",\"revision\":1,\"enabled\":true}]}";
		Assert.Throws<InvalidDataException>(()=>ResidentInventoryParser.Parse(payload,"probe"));
	}

	sealed class ExactTarget : ILiveTargetIdentity { public bool IsCurrent(TargetIdentity identity)=>true; }
}
