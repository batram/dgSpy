using System;
using System.IO;
using dgSpy.Extension.Policy;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class CapabilityPolicyTests {
	[Fact] public void Absent_configuration_denies_direct_rpc_stub() { var policy=CapabilityPolicySnapshot.Load("host-a",Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"),"full-control"); Assert.Equal("permission_denied",Assert.Throws<dgSpy.Extension.RpcException>(()=>DirectRpcStub(policy)).Code); }
	[Fact] public void Upgrade_defaults_do_not_infer_grants_from_full_control() { var path=Path.GetTempFileName(); try { File.WriteAllText(path,"{\"format_version\":1,\"defaults\":{\"runtime_hooks\":false,\"custom_hook_code\":false,\"hook_export\":false},\"entries\":[]}"); var policy=CapabilityPolicySnapshot.Load("host-a",path,"full-control"); foreach(CapabilityPermission permission in Enum.GetValues(typeof(CapabilityPermission))) Assert.False(policy.CaptureDecision("hooklab","hook_create",permission).Granted); } finally { File.Delete(path); } }
	[Fact] public void Permissions_are_independent_and_host_and_operation_scoped() { var path=WritePolicy(true,false,false); try { var policy=CapabilityPolicySnapshot.Load("host-a",path,"full-control"); Assert.True(policy.CaptureDecision("hooklab","hook_create",CapabilityPermission.RuntimeHooks).Granted); Assert.False(policy.CaptureDecision("hooklab","hook_create",CapabilityPermission.CustomHookCode).Granted); Assert.False(policy.CaptureDecision("hooklab","hook_compile",CapabilityPermission.RuntimeHooks).Granted); Assert.False(CapabilityPolicySnapshot.Load("host-b",path,"full-control").CaptureDecision("hooklab","hook_create",CapabilityPermission.RuntimeHooks).Granted); } finally { File.Delete(path); } }
	[Fact] public void Inspect_only_overrides_explicit_grants() { var path=WritePolicy(true,true,true); try { var policy=CapabilityPolicySnapshot.Load("host-a",path,"inspect-only"); foreach(CapabilityPermission permission in Enum.GetValues(typeof(CapabilityPermission))) Assert.False(policy.CaptureDecision("hooklab","hook_create",permission).Granted); } finally { File.Delete(path); } }
	[Fact] public void Captured_decision_cannot_be_widened_by_reload() { var path=WritePolicy(false,false,false); try { var old=CapabilityPolicySnapshot.Load("host-a",path,"full-control").CaptureDecision("hooklab","hook_create",CapabilityPermission.RuntimeHooks); File.WriteAllText(path,Policy(true,true,true)); var reloaded=CapabilityPolicySnapshot.Load("host-a",path,"full-control"); Assert.False(old.Granted); Assert.True(reloaded.CaptureDecision("hooklab","hook_create",CapabilityPermission.RuntimeHooks).Granted); } finally { File.Delete(path); } }
	static void DirectRpcStub(CapabilityPolicySnapshot snapshot) { snapshot.CaptureDecision("hooklab","hook_create",CapabilityPermission.RuntimeHooks).Demand(); }
	static string WritePolicy(bool runtime,bool custom,bool export) { var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"); File.WriteAllText(path,Policy(runtime,custom,export)); return path; }
	static string Policy(bool runtime,bool custom,bool export)=>$"{{\"format_version\":1,\"entries\":[{{\"host_id\":\"host-a\",\"provider\":\"hooklab\",\"operation\":\"hook_create\",\"permissions\":{{\"runtime_hooks\":{runtime.ToString().ToLowerInvariant()},\"custom_hook_code\":{custom.ToString().ToLowerInvariant()},\"hook_export\":{export.ToString().ToLowerInvariant()}}}}}]}}";
}
