using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace dgSpy.Gateway;

public sealed record WorkflowTopic(
	[property: JsonPropertyName("topic")] string Topic,
	[property: JsonPropertyName("guidance")] string Guidance,
	[property: JsonPropertyName("tools")] IReadOnlyList<string> Tools);

public sealed record WorkflowCatalogView(
	[property: JsonPropertyName("topics")] IReadOnlyList<WorkflowTopic> Topics);

/// <summary>The authoritative workflow grouping used by focused help and resource discovery.</summary>
public static class WorkflowCatalog {
	static readonly IReadOnlyList<WorkflowTopic> topics=Array.AsReadOnly(new[] {
		Topic("setup","Run dgspy mcp for client-spawned startup, or dgspy start for URL clients. Begin with get_started, then use doctor only when startup or connectivity needs diagnosis.","get_started","doctor"),
		Topic("local_deployment","Call launch_local_host once. It installs the bundled host when needed, starts dnSpy, and waits briefly for registration. It never starts a second host beside a running one: a host already running the installed payload is adopted, and a host running a different build fails with host_already_running. Use get_local_deployment to inspect the managed install. Pass replace=true only when you mean to end that host's debugging sessions, because replacement detaches its targets and closes it. A host runs at the Gateway's own integrity level, so a target owned by another user or by a service is missing from list_programs rather than merely unattachable; pass elevated=true to launch an elevated host instead, which prompts for User Account Control and is refused rather than adopted when the host already running is not itself elevated.","get_local_deployment","launch_local_host"),
		Topic("remote_deployment","Ask for host_id and the Gateway address reachable from that host, then call create_remote_host_package once. The call persists and activates the listener and host registration without a Gateway restart; give only the ZIP and SHA-256 to the user, and do not transfer or run it automatically. Use get_remote_host_readiness after the extracted host launches, and revoke_remote_host only for deliberate cleanup; revocation closes its live connection immediately.","create_remote_host_package","get_remote_host_readiness","revoke_remote_host"),
		Topic("attach","Use list_programs with narrow filters then attach, or attach_endpoint for a Mono/Unity server=y endpoint. Never TCP-probe a single-use Unity endpoint. Use launch only when dnSpy should create and own the process.","list_programs","attach","attach_endpoint","launch"),
		Topic("discovery","Start with search. It accepts a name, including a qualified path such as Type.Member, and returns module plus metadata token. Drill in with list_members, list_types, get_csharp, get_il, find_references, or analyze_symbol using returned identifiers; they round-trip without parsing. Pass module on Unity. kinds:[\"literal\"] finds string and number constants from IL; search_text is the last resort because it decompiles.","search","list_members","list_types","get_csharp","get_il","find_references","analyze_symbol","search_text"),
		Topic("stepping","Use current scoped versions and stop_id. step_and_inspect is the shortest path. A breakpoint outranks a step: stepping out of a method that still holds an active breakpoint can hit it first, so remove_breakpoint first when necessary. trace_calls is bounded best-effort and cannot observe optimized, native, runtime, async, or missing-sequence-point calls. The caller owns the event cursor: wait_for_stop, wait_for_event, and get_events read one shared buffer non-destructively, so omitting after_event_id replays retained history. Seed it from set_il_breakpoint's cursor_event_id, then pass each response's last_event_id forward.","step_and_inspect","step_into","step_over","step_out","remove_breakpoint","trace_calls","wait_for_stop","wait_for_event","get_events","set_il_breakpoint"),
		Topic("safe_evaluation","Prepare a stopped frame with list_threads and get_callstack, then use evaluate for expressions or invoke_method for an explicit call. allow_func_eval runs target code and allow_side_effects permits a side-effecting expression; invoke_method enables the method-call gates deliberately. run_to_method and run_to_location drive execution to code that the target can naturally reach; they do not make unreachable code execute.","list_threads","get_callstack","evaluate","invoke_method","run_to_method","run_to_location"),
		Topic("raw_memory","Start from address metadata returned with a debugger value, then use read_memory for inspection and write_memory only for an intended mutation. Addresses are stop-bound: do not reuse them after continue, stepping, restart, detach, or any other resume; reacquire value metadata at the current stop first.","evaluate","get_frame","get_members","read_memory","write_memory"),
		Topic("object_ids","Create a stable debugger reference with create_object_id, inspect ownership with list_object_ids, and read it with evaluate_object_id. Object IDs consume debugger resources: the caller that creates one is responsible for release_object_id as soon as it is no longer needed.","create_object_id","list_object_ids","evaluate_object_id","release_object_id"),
		Topic("static_analysis","Start with search, then use its returned module and token to drill into get_il, get_csharp, find_references, or analyze_symbol without parsing display text. Use search_text only when cheaper metadata and IL searches cannot answer the question.","search","get_il","get_csharp","find_references","analyze_symbol","search_text"),
		Topic("breakpoint_configuration","Create source/method breakpoints with set_breakpoint or exact IL breakpoints with set_il_breakpoint; the engine snaps to a valid sequence point where required. Use update_breakpoint for conditions and hit counts, capture the creation cursor before resuming, then wait_for_stop or wait_for_event from that cursor. Inspect with list_breakpoints, remove with remove_breakpoint or clear_breakpoints, and use export_breakpoints only when persistence is wanted.","set_breakpoint","set_il_breakpoint","update_breakpoint","list_breakpoints","wait_for_stop","wait_for_event","remove_breakpoint","clear_breakpoints","export_breakpoints"),
		Topic("recovery","Call doctor and list_hosts. Recover sessions with list_sessions, inspect get_session_controller, and use claim_session only when ownership permits; refresh with get_session_state after stale-version errors. A disconnect never implies resume or detach.","doctor","list_hosts","list_sessions","get_session_controller","claim_session","get_session_state"),
		Topic("cleanup","Remove temporary breakpoints with remove_breakpoint or clear_breakpoints and release every created object ID with release_object_id. Use detach to preserve the target and end debugging safely; release_session only releases the controller lease. terminate destroys the target and is not a substitute for detach.","remove_breakpoint","clear_breakpoints","list_object_ids","release_object_id","detach","release_session","terminate"),
		Topic("shutdown","Detach before the host goes down. A host holding an ICorDebug attachment takes its target with it when it exits, whether by crash, close, or redeploy, so an undetached target dies with the host. The host detaches everything on a normal dnSpy exit and launch_local_host with replace=true detaches before closing, but neither can save a target from a host killed outright. A detach_timed_out result means the target remains attached and the session is preserved; inspect get_session_state and retry safe detach without closing dnSpy.","detach","get_session_state","launch_local_host")
	});
	static readonly IReadOnlyDictionary<string,WorkflowTopic> byTopic=new ReadOnlyDictionary<string,WorkflowTopic>(topics.ToDictionary(topic=>topic.Topic,StringComparer.Ordinal));

	public static IReadOnlyList<WorkflowTopic> Topics => topics;

	/// <summary>Returns focused help; omitted and unknown keys deliberately use the setup workflow.</summary>
	public static WorkflowTopic Lookup(string? topic) => topic is not null && byTopic.TryGetValue(topic,out var found) ? found : byTopic["setup"];

	/// <summary>Returns the compact all-topic view for serialization as an MCP resource.</summary>
	public static WorkflowCatalogView RenderCompactCatalog() => new(topics);

	static WorkflowTopic Topic(string topic,string guidance,params string[] tools) => new(topic,guidance,Array.AsReadOnly(tools));
}
