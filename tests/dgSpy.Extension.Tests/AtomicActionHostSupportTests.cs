using System.Text.Json.Nodes;
using dgSpy.Extension;
using dgSpy.Extension.Debugger.AtomicActions;
using dgSpy.Protocol;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>
/// The decisions the dnSpy-facing half of an atomic action makes. They used to live inlined in
/// RpcHost.AtomicActions.cs - a partial of a class no test can construct - and shipped uncovered.
/// </summary>
public sealed class AtomicActionHostSupportTests {
	static AtomicActionRequest Request(params uint[] nearby) => new() {
		ActionId="a1",ActionName="test",ProcessId=42,RuntimeId="runtime-a",AppDomainId="domain",
		Module="target.dll",MethodToken=0x06000001,IlOffset=3,NearbyOffsets=nearby,DeadlineUtc=DateTime.UtcNow.AddSeconds(5),
	};
	static AtomicActionStop Stop(uint offset,string module="target.dll",uint token=0x06000001) => new("runtime-a","domain",42,"thread",module,token,offset,AtomicActionEvaluationProbe.Clear);

	[Fact] public void The_slot_reported_is_the_declared_offset_the_stop_actually_landed_on() {
		var slot=NearbySlotSelector.Select(Request(4,9,15),Stop(9));
		Assert.Equal((uint)9,slot!.IlOffset); Assert.Equal("target.dll",slot.Module); Assert.Equal((uint)0x06000001,slot.MethodToken);
	}

	[Fact] public void A_stop_matching_no_declared_offset_is_not_attributed_to_one() =>
		Assert.Null(NearbySlotSelector.Select(Request(4,9),Stop(11)));

	[Fact] public void A_stop_in_another_method_or_module_is_never_a_nearby_slot() {
		Assert.Null(NearbySlotSelector.Select(Request(4),Stop(4,token:0x06000002)));
		Assert.Null(NearbySlotSelector.Select(Request(4),Stop(4,module:"other.dll")));
	}

	/// <summary>A stop names the module dnSpy resolved - a full path - and the request names whatever
	/// the caller typed. Comparing those as strings made an action report nearby_slot_not_found at its
	/// own exact target, which is what the live leg found first.</summary>
	[Fact] public void A_resolved_module_path_still_matches_the_name_the_caller_asked_for() {
		Assert.True(NearbySlotSelector.SameModule(@"C:\build\out\Milestone1Target.exe","Milestone1Target.exe"));
		Assert.True(NearbySlotSelector.SameModule(@"C:\build\out\Milestone1Target.exe","Milestone1Target"));
		Assert.True(NearbySlotSelector.SameModule("target.dll","target.dll"));
		Assert.False(NearbySlotSelector.SameModule(@"C:\build\out\Milestone1Target.exe","Other.exe"));
		var slot=NearbySlotSelector.Select(Request(4),new AtomicActionStop("runtime-a","domain",42,"thread",@"C:\build\out\target.dll",0x06000001,4,AtomicActionEvaluationProbe.Clear));
		Assert.Equal((uint)4,slot!.IlOffset);
	}

	[Fact] public void No_declared_offsets_and_no_stop_select_nothing() {
		Assert.Null(NearbySlotSelector.Select(Request(),Stop(3)));
		Assert.Null(NearbySlotSelector.Select(Request(4),null));
	}

	[Fact] public void An_omitted_deadline_comes_from_timeout_ms_and_is_bounded() {
		var now=new DateTime(2026,8,10,12,0,0,DateTimeKind.Utc);
		Assert.Equal(now.AddMilliseconds(10000),AtomicActionDeadline.Resolve(default,null,now));
		Assert.Equal(now.AddMilliseconds(2500),AtomicActionDeadline.Resolve(default,2500,now));
		Assert.Equal(now.AddMilliseconds(60000),AtomicActionDeadline.Resolve(default,900000,now));
	}

	/// <summary>A supplied deadline used to be honoured verbatim, blocking every UI and RPC mutation on
	/// that process for its full length while the Gateway had long abandoned the call.</summary>
	[Fact] public void A_supplied_deadline_is_clamped_to_the_same_bound() {
		var now=new DateTime(2026,8,10,12,0,0,DateTimeKind.Utc);
		Assert.Equal(now.AddMilliseconds(60000),AtomicActionDeadline.Resolve(now.AddHours(1),null,now));
		Assert.Equal(now.AddMilliseconds(30000),AtomicActionDeadline.Resolve(now.AddMilliseconds(30000),null,now));
	}

	[Fact] public void A_deadline_already_in_the_past_is_refused() {
		var now=new DateTime(2026,8,10,12,0,0,DateTimeKind.Utc);
		var error=Assert.Throws<RpcException>(()=>AtomicActionDeadline.Resolve(now.AddSeconds(-1),null,now));
		Assert.Equal("invalid_arguments",error.Code);
	}

	[Fact] public void Inconsistent_process_ids_are_refused_and_both_are_named() {
		var error=Assert.Throws<RpcException>(()=>AtomicActionRequestScope.ValidateProcessIds(4242,42));
		Assert.Equal("invalid_arguments",error.Code);
		Assert.Contains("4242",error.Message); Assert.Contains("42",error.Message);
		AtomicActionRequestScope.ValidateProcessIds(42,42);
		AtomicActionRequestScope.ValidateProcessIds(null,42);
	}

	/// <summary>The module search reads process_id and runtime_id at top level only, so the nested
	/// runtime_id scoped nothing: a multi-runtime target answered ambiguous_target advising the caller to
	/// pass the runtime id it had already passed.</summary>
	[Fact] public void The_nested_runtime_and_process_scope_the_module_search() {
		var source=new RpcRequest { Operation="run_atomic_action",Arguments=new JsonObject { ["session_id"]="s1",["process_id"]=42 } };
		var scoped=AtomicActionRequestScope.ScopeModuleSearch(source,Request());
		Assert.Equal("runtime-a",(string?)scoped.Arguments["runtime_id"]);
		Assert.Equal(42,(int?)scoped.Arguments["process_id"]);
		Assert.Equal("s1",(string?)scoped.Arguments["session_id"]);
		Assert.Null(source.Arguments["runtime_id"]);
	}

	[Fact] public void An_absent_runtime_id_leaves_the_search_unscoped_by_runtime() {
		var request=Request(); request.RuntimeId="";
		var scoped=AtomicActionRequestScope.ScopeModuleSearch(new RpcRequest { Operation="run_atomic_action",Arguments=new JsonObject() },request);
		Assert.Null(scoped.Arguments["runtime_id"]);
	}

	[Fact] public void The_nested_exact_module_identity_scopes_the_module_search() {
		var request=Request(); request.ModuleId="dm1:11859180ea4d44a99eb658e003cca953:42:cd03acdd4f3a473685914902b4dcc8c1:3:17";
		var scoped=AtomicActionRequestScope.ScopeModuleSearch(new RpcRequest { Operation="run_atomic_action",Arguments=new JsonObject() },request);
		Assert.Equal(request.ModuleId,(string?)scoped.Arguments["module_id"]);
	}

	[Fact] public void Target_exit_and_appdomain_unload_producers_filter_on_identity() {
		Assert.Equal(InterruptionReason.target_exited,AtomicActionInterruptions.ProcessExited(42,42)!.Reason);
		Assert.Null(AtomicActionInterruptions.ProcessExited(42,43));
		Assert.Equal(InterruptionReason.appdomain_unloaded,AtomicActionInterruptions.AppDomainUnloaded(true)!.Reason);
		Assert.Null(AtomicActionInterruptions.AppDomainUnloaded(false));
		Assert.Equal(InterruptionReason.target_exited,AtomicActionInterruptions.MissingThread().Reason);
	}

	[Fact] public void Ui_shutdown_replaces_only_a_disconnected_client() {
		var disconnected=new AtomicActionStatus(ActionOutcome.completed,InterruptionReason.client_disconnected,CleanupOutcome.completed,true,"audit","op");
		var remapped=AtomicActionInterruptions.RemapForShutdown(disconnected,uiShutdown:true);
		Assert.Equal(InterruptionReason.ui_shutdown,remapped.InterruptionReason);
		Assert.Equal(ActionOutcome.completed,remapped.ActionOutcome); Assert.Equal("audit",remapped.AuditId); Assert.Equal("op",remapped.ReconciliationOperation);
		Assert.Same(disconnected,AtomicActionInterruptions.RemapForShutdown(disconnected,uiShutdown:false));
		var timedOut=new AtomicActionStatus(ActionOutcome.trigger_not_reached,InterruptionReason.timeout,CleanupOutcome.completed,false,"audit");
		Assert.Same(timedOut,AtomicActionInterruptions.RemapForShutdown(timedOut,uiShutdown:true));
	}
}
