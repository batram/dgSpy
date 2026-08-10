using System;
using System.Globalization;
using System.Text.Json.Nodes;
using dgSpy.Protocol;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>
	/// The dnSpy-facing half of an atomic action, minus dnSpy. Every decision here was originally inlined
	/// in <c>RpcHost.AtomicActions.cs</c>, which is a partial of a class no test can construct - so none of
	/// it was covered, and four of T08's nine defects lived in exactly these few lines.
	/// </summary>
	public static class NearbySlotSelector {
		/// <summary>
		/// Reports which declared slot execution actually bound, matching the observed stop against the
		/// declared offsets. A stop that matches none returns null: the caller declared where it was
		/// willing to act, and reporting an offset it did not declare - or the first one it did, which is
		/// what the original code returned unconditionally - would be fabricating the used slot.
		/// </summary>
		public static AtomicActionSlot? Select(AtomicActionRequest request,AtomicActionStop? stop) {
			if(request is null) throw new ArgumentNullException(nameof(request));
			if(stop is null || request.NearbyOffsets is null || request.NearbyOffsets.Length==0) return null;
			// A stop in another module or another method is not a nearby slot of this target at all.
			if(stop.MethodToken!=request.MethodToken || !SameModule(stop.Module,request.Module)) return null;
			foreach(var offset in request.NearbyOffsets)
				if(offset==stop.IlOffset) return new AtomicActionSlot(request.Module,request.MethodToken,offset);
			return null;
		}

		/// <summary>
		/// A stop reports the module dnSpy resolved - a full path - while the request carries whatever the
		/// caller typed, which the whole module family deliberately accepts as a name, a leaf or a stem.
		/// Comparing those two as strings made every action whose request named "Milestone1Target.exe"
		/// decide it had landed somewhere else and answer nearby_slot_not_found at its own exact target.
		/// One rule for the family, so an action resolves what get_csharp and set_il_breakpoint resolve.
		/// </summary>
		public static bool SameModule(string? stopModule,string? requestedModule) =>
			String.Equals(stopModule,requestedModule,StringComparison.OrdinalIgnoreCase) || ModuleNameMatch.Matches(null,stopModule,requestedModule);
	}

	/// <summary>The bound an atomic action's deadline is held to, and the one place that applies it.</summary>
	public static class AtomicActionDeadline {
		/// <summary>The tool schema caps <c>timeout_ms</c> here and the capability catalog advertises
		/// 65000 ms for the whole operation, so a longer deadline is one the Gateway abandons anyway -
		/// while the extension keeps every UI and RPC mutation on that process blocked for its full length.</summary>
		public const int MaxBoundMs=60000;
		public const int DefaultBoundMs=10000;

		/// <summary>
		/// Resolves the deadline actually enforced. A caller-supplied deadline is clamped rather than
		/// rejected - the ceiling is a property of this host, not an error in the request, and a caller
		/// whose clock is a few seconds fast would otherwise get a hard failure for asking for the maximum.
		/// The clamp is never silent: the resolved value is reported as <c>effective_deadline_utc</c>.
		/// A deadline already in the past is refused, because there is no shorter run to fall back to.
		/// </summary>
		public static DateTime Resolve(DateTime suppliedDeadline,int? timeoutMs,DateTime nowUtc) {
			var boundMs=Math.Min(MaxBoundMs,Math.Max(1,timeoutMs ?? DefaultBoundMs));
			var ceiling=nowUtc.AddMilliseconds(MaxBoundMs);
			if(suppliedDeadline==default) return nowUtc.AddMilliseconds(boundMs);
			var supplied=suppliedDeadline.ToUniversalTime();
			if(supplied<=nowUtc) throw new RpcException("invalid_arguments","request.deadline_utc is in the past; supply a future deadline or omit it and pass timeout_ms.");
			return supplied>ceiling ? ceiling : supplied;
		}
	}

	/// <summary>Cross-validates and re-scopes the two levels of target identity a run_atomic_action call
	/// carries: a top-level <c>process_id</c> that the shared operation plumbing reads, and a nested
	/// <c>process_id</c>/<c>runtime_id</c> inside the action request.</summary>
	public static class AtomicActionRequestScope {
		/// <summary>Refuses a request whose two process ids disagree. Selecting by one and validating
		/// against the other silently picks a winner; naming both says which pair was inconsistent.</summary>
		public static void ValidateProcessIds(int? topLevelProcessId,int nestedProcessId) {
			if(topLevelProcessId.HasValue && topLevelProcessId.Value!=nestedProcessId)
				throw new RpcException("invalid_arguments",String.Format(CultureInfo.InvariantCulture,
					"process_id {0} does not match request.process_id {1}; the atomic action must name one process.",topLevelProcessId.Value,nestedProcessId));
		}

		/// <summary>
		/// Refuses <c>disconnect_policy</c> on the asynchronous start, and reports the resolved policy.
		///
		/// <para>The state machine takes the request/client lifetime token. Once <c>start_atomic_action</c>
		/// deliberately returns, that request lifetime ends <em>normally</em> - so an asynchronous action
		/// cannot read the successful completion of its own start request as a client disconnect, and
		/// <c>cancel_on_disconnect</c>/<c>complete_on_disconnect</c> describe nothing there. The policy
		/// therefore stays on blocking <c>run_atomic_action</c> only, and the asynchronous path is
		/// host-owned after acceptance, requiring an explicit cancel.</para>
		///
		/// <para>Refused rather than ignored: controller loss silently cancelling a target mutation is a
		/// policy that has not been designed or tested, and a caller who asked for it and was quietly given
		/// the opposite would find out only when a mutation they expected to be cancelled had applied.</para>
		/// </summary>
		public static AtomicActionInterruptionPolicy ResolveAsyncDisconnectPolicy(JsonNode? suppliedRequest) {
			if(suppliedRequest is JsonObject supplied && supplied["disconnect_policy"] is not null)
				throw new RpcException("invalid_arguments","disconnect_policy applies to run_atomic_action only. An action started asynchronously is owned by the host after acceptance and ends only on its deadline or an explicit "+AtomicActionStateMachine.CancelOperation+".");
			return AtomicActionInterruptionPolicy.complete_on_disconnect;
		}

		/// <summary>
		/// Produces the request the shared module search sees, carrying the action's own process and
		/// runtime. The search reads those two arguments at top level only, so without this the nested
		/// <c>runtime_id</c> scoped nothing and a multi-runtime target answered <c>ambiguous_target</c>
		/// advising the caller to pass the very runtime id it had already passed.
		/// </summary>
		public static RpcRequest ScopeModuleSearch(RpcRequest source,AtomicActionRequest request) {
			if(source is null) throw new ArgumentNullException(nameof(source));
			if(request is null) throw new ArgumentNullException(nameof(request));
			var scoped=new RpcRequest { Operation=source.Operation,RequestId=source.RequestId,Arguments=(JsonObject)source.Arguments.DeepClone(),DeadlineUtc=source.DeadlineUtc };
			scoped.Arguments["process_id"]=request.ProcessId;
			if(!String.IsNullOrWhiteSpace(request.RuntimeId)) scoped.Arguments["runtime_id"]=request.RuntimeId;
			if(!String.IsNullOrWhiteSpace(request.ModuleId)) scoped.Arguments["module_id"]=request.ModuleId;
			return scoped;
		}
	}

	/// <summary>The producers behind the interruption reasons the host raises. They are conditions, not
	/// exceptions thrown by a fake, so they are kept where a test can reach them.</summary>
	public static class AtomicActionInterruptions {
		public static AtomicActionInterruptedException? ProcessExited(int watchedProcessId,int exitedProcessId) =>
			watchedProcessId==exitedProcessId ? new AtomicActionInterruptedException(InterruptionReason.target_exited,"The target exited before reaching the action slot.") : null;

		public static AtomicActionInterruptedException? AppDomainUnloaded(bool isTargetAppDomain) =>
			isTargetAppDomain ? new AtomicActionInterruptedException(InterruptionReason.appdomain_unloaded,"The target AppDomain unloaded before reaching the action slot.") : null;

		public static AtomicActionInterruptedException MissingThread() =>
			new AtomicActionInterruptedException(InterruptionReason.target_exited,"The target exited before the owned stop supplied a thread.");

		/// <summary>
		/// A client that vanished because the host itself is closing is not a disconnected client. The
		/// remap applies only to <c>client_disconnected</c>: a timeout under complete_on_disconnect, or an
		/// external debugger action, is what it says it is even during shutdown.
		/// </summary>
		public static AtomicActionStatus RemapForShutdown(AtomicActionStatus status,bool uiShutdown) {
			if(status is null) throw new ArgumentNullException(nameof(status));
			if(!uiShutdown || status.InterruptionReason!=InterruptionReason.client_disconnected) return status;
			return new AtomicActionStatus(status.ActionOutcome,InterruptionReason.ui_shutdown,status.CleanupOutcome,status.ActionMayHaveExecuted,status.AuditId,status.ReconciliationOperation);
		}
	}
}
