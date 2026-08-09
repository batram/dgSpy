using System;
using System.IO;
using Xunit;

namespace dgSpy.Gateway.Tests;

public sealed class CapabilityPolicyTests {
	[Fact] public void Full_control_without_entries_grants_nothing() { var access=new GatewayAccessPolicy("full-control"); var decision=access.CaptureCapability("host-a","hooklab","hook_create",CapabilityPermission.RuntimeHooks,Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json")); Assert.False(decision.Granted); Assert.Equal("permission_denied",Assert.Throws<GatewayControlException>(()=>decision.Demand("hooklab","hook_create",CapabilityPermission.RuntimeHooks)).Code); }
	[Fact] public void Inspect_only_is_a_ceiling() { var path=Path.GetTempFileName(); try { File.WriteAllText(path,"{\"format_version\":1,\"entries\":[{\"host_id\":\"host-a\",\"provider\":\"hooklab\",\"operation\":\"hook_create\",\"permissions\":{\"runtime_hooks\":true}}]}"); Assert.False(new GatewayAccessPolicy("inspect-only").CaptureCapability("host-a","hooklab","hook_create",CapabilityPermission.RuntimeHooks,path).Granted); } finally { File.Delete(path); } }
}
