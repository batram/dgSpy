using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Exceptions;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		// Conditions, hit counts and trace messages are dnSpy breakpoint *settings*, not separate objects,
		// and they are evaluated by the engine in the target. That has two consequences worth stating:
		// a condition that cannot be evaluated does not fail here, it fails at hit time and dnSpy stops
		// anyway; and a tracepoint with continue=true never produces a stop, so a caller that sets one and
		// then waits for a stop waits forever. update_breakpoint reports both back rather than letting the
		// caller discover them from silence.
		async Task<BreakpointInfo> UpdateBreakpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			var id=(int?)req.Arguments["breakpoint_id"] ?? throw new RpcException("invalid_arguments","breakpoint_id is required.");
			var enabled=(bool?)req.Arguments["enabled"];
			var condition=(string?)req.Arguments["condition"];
			var conditionKind=(string?)req.Arguments["condition_kind"];
			var hitCount=(int?)req.Arguments["hit_count"];
			var hitCountKind=(string?)req.Arguments["hit_count_kind"];
			var traceMessage=(string?)req.Arguments["trace_message"];
			var traceContinue=(bool?)req.Arguments["trace_continue"];
			if (conditionKind is not null && !BreakpointConditionKinds.IsKnown(conditionKind))
				throw new RpcException("invalid_argument",$"Unknown condition_kind '{conditionKind}'. Valid: {string.Join(", ",BreakpointConditionKinds.All)}.");
			if (hitCountKind is not null && !HitCountKinds.IsKnown(hitCountKind))
				throw new RpcException("invalid_argument",$"Unknown hit_count_kind '{hitCountKind}'. Valid: {string.Join(", ",HitCountKinds.All)}.");
			if (hitCount is not null && hitCount.Value<1) throw new RpcException("invalid_arguments","hit_count must be 1 or greater.");
			if (hitCountKind is not null && hitCount is null) throw new RpcException("invalid_arguments","hit_count_kind requires hit_count.");
			if (conditionKind is not null && string.IsNullOrEmpty(condition)) throw new RpcException("invalid_arguments","condition_kind requires condition.");
			if (traceContinue is not null && traceMessage is null) throw new RpcException("invalid_arguments","trace_continue requires trace_message.");
			if (enabled is null && condition is null && hitCount is null && traceMessage is null)
				throw new RpcException("invalid_arguments","Supply at least one of enabled, condition, hit_count or trace_message.");

			// Written on one dispatcher hop and read back on the next. DbgCodeBreakpointImpl.Settings does
			// not assign: it calls DbgCodeBreakpointsServiceImpl.Modify, which posts ModifyCore back to the
			// dispatcher even when the caller is already on it. Describing inside the same callback
			// therefore reports the *previous* settings — the write looks like it silently did nothing.
			// Dispatcher delivery is FIFO, so the queued ModifyCore runs before the second hop below.
			await OnDebuggerAsync(()=>{
				var bp=breakpoints.Breakpoints.FirstOrDefault(b=>b.Id==id)
					?? throw new RpcException("breakpoint_not_found",$"Breakpoint {id} does not exist. Refresh list_breakpoints and use an exact breakpoint_id.");
				// One Settings write, not four property writes: each property setter raises its own change
				// notification, and dnSpy persists the settings object as a unit.
				var settings=bp.Settings;
				if (enabled is not null) settings.IsEnabled=enabled.Value;
				// An empty string clears; a null argument leaves the existing value alone. That distinction
				// is the only way to remove a condition without deleting and recreating the breakpoint.
				if (condition is not null)
					settings.Condition=condition.Length==0 ? (DbgCodeBreakpointCondition?)null
						: new DbgCodeBreakpointCondition(conditionKind==BreakpointConditionKinds.WhenChanged ? DbgCodeBreakpointConditionKind.WhenChanged : DbgCodeBreakpointConditionKind.IsTrue,condition);
				if (hitCount is not null)
					settings.HitCount=new DbgCodeBreakpointHitCount(hitCountKind switch {
						HitCountKinds.MultipleOf => DbgCodeBreakpointHitCountKind.MultipleOf,
						HitCountKinds.AtLeast => DbgCodeBreakpointHitCountKind.GreaterThanOrEquals,
						_ => DbgCodeBreakpointHitCountKind.Equals,
					},hitCount.Value);
				if (traceMessage is not null)
					settings.Trace=traceMessage.Length==0 ? (DbgCodeBreakpointTrace?)null : new DbgCodeBreakpointTrace(traceMessage,traceContinue ?? true);
				bp.Settings=settings;
				return true;
			},cancellationToken).ConfigureAwait(false);

			return await OnDebuggerAsync(()=>{
				var bp=breakpoints.Breakpoints.FirstOrDefault(b=>b.Id==id)
					?? throw new RpcException("breakpoint_not_found",$"Breakpoint {id} was removed while it was being updated.");
				var info=Describe(bp);
				if (info.TraceContinue==true)
					info.Warning="This is a tracepoint that continues: it prints and keeps running, so it produces no stopped event and wait_for_stop will never see it. Pass trace_continue=false to stop as well as print.";
				return info;
			},cancellationToken).ConfigureAwait(false);
		}

		// Exception settings are dnSpy-global exactly like code breakpoints, and outlive a session for the
		// same reason. Category-level defaults and per-type entries are the same object to dnSpy, separated
		// only by whether the id carries a name.
		static DbgExceptionId ExceptionId(string category,string? name) =>
			string.IsNullOrEmpty(name) ? new DbgExceptionId(category) : new DbgExceptionId(category,name!);

		async Task<ExceptionBreakpointInfo> SetExceptionBreakpointAsync(RpcRequest req,CancellationToken cancellationToken) {
			var category=(string?)req.Arguments["category"]; if (string.IsNullOrWhiteSpace(category)) category=PredefinedExceptionCategories.DotNet;
			var name=(string?)req.Arguments["name"];
			var first=(bool?)req.Arguments["stop_first_chance"];
			var second=(bool?)req.Arguments["stop_second_chance"];
			if (first is null && second is null) throw new RpcException("invalid_arguments","Supply stop_first_chance, stop_second_chance, or both.");
			var id=ExceptionId(category!,name);
			// Same two-hop rule as update_breakpoint: dnSpy's exception service posts its own work back to
			// the dispatcher, so the result has to be read on a later hop or it reports the old settings.
			await OnDebuggerAsync(()=>{
				if (!exceptions.TryGetCategoryDefinition(category!,out _))
					throw new RpcException("unknown_exception_category",$"No exception category '{category}'. Known: {string.Join(", ",exceptions.CategoryDefinitions.Select(c=>c.Name))}.");
				var current=exceptions.TryGetSettings(id,out var found) ? found : new DbgExceptionSettings(DbgExceptionDefinitionFlags.None);
				var flags=current.Flags;
				if (first is not null) flags=first.Value ? flags|DbgExceptionDefinitionFlags.StopFirstChance : flags&~DbgExceptionDefinitionFlags.StopFirstChance;
				if (second is not null) flags=second.Value ? flags|DbgExceptionDefinitionFlags.StopSecondChance : flags&~DbgExceptionDefinitionFlags.StopSecondChance;
				var settings=new DbgExceptionSettings(flags,current.Conditions);
				// Modify only updates an exception dnSpy already knows. A type nobody has named yet — which
				// is the common case for a game's own exception types — has to be added first, or the call
				// silently does nothing and the caller waits for a stop that never comes.
				if (exceptions.TryGetDefinition(id,out _)) exceptions.Modify(new[]{new DbgExceptionIdAndSettings(id,settings)});
				else exceptions.Add(new[]{new DbgExceptionSettingsInfo(new DbgExceptionDefinition(id,DbgExceptionDefinitionFlags.None),settings)});
				return true;
			},cancellationToken).ConfigureAwait(false);

			return await OnDebuggerAsync(()=>DescribeException(id,exceptions.TryGetSettings(id,out var applied) ? applied : new DbgExceptionSettings(DbgExceptionDefinitionFlags.None)),cancellationToken).ConfigureAwait(false);
		}

		ExceptionBreakpointInfo DescribeException(DbgExceptionId id,DbgExceptionSettings settings) => new ExceptionBreakpointInfo {
			Category=id.Category ?? "",Name=id.HasName ? id.Name : null,
			StopFirstChance=(settings.Flags&DbgExceptionDefinitionFlags.StopFirstChance)!=0,
			StopSecondChance=(settings.Flags&DbgExceptionDefinitionFlags.StopSecondChance)!=0,
			StateVersion=stateVersion,
		};

		// Default to first-chance entries only, and it is not a cosmetic default. dnSpy ships thousands of
		// .NET exception definitions and every one of them stops on *second* chance out of the box, so
		// "everything that stops" is a multi-thousand-entry list that is identical on every machine and
		// says nothing about what this caller configured. First chance is the set someone chose.
		async Task<ExceptionBreakpointList> ListExceptionBreakpointsAsync(RpcRequest req,CancellationToken cancellationToken) {
			var includeSecondChance=(bool?)req.Arguments["include_second_chance"] ?? false;
			var max=Math.Min(1000,Math.Max(1,(int?)req.Arguments["max_results"] ?? 200));
			// Same category/name filters as list_exception_policies, which reports the same entries. The
			// two tools disagreeing about how you narrow one lookup is the sibling asymmetry that made
			// this pair read as two features instead of one.
			var category=(string?)req.Arguments["category"]; var name=(string?)req.Arguments["name"];
			return await OnDebuggerAsync(()=>{
				var wanted=includeSecondChance ? DbgExceptionDefinitionFlags.StopFirstChance|DbgExceptionDefinitionFlags.StopSecondChance : DbgExceptionDefinitionFlags.StopFirstChance;
				var matching=exceptions.Exceptions
					.Where(e=>(e.Settings.Flags&wanted)!=0)
					.Select(e=>DescribeException(e.Definition.Id,e.Settings))
					.Where(e=>category is null||string.Equals(e.Category,category,StringComparison.OrdinalIgnoreCase))
					.Where(e=>name is null||string.Equals(e.Name??"",name,StringComparison.Ordinal))
					.OrderBy(e=>e.Category,StringComparer.Ordinal).ThenBy(e=>e.Name ?? "",StringComparer.Ordinal).ToArray();
				return new ExceptionBreakpointList {
					Entries=matching.Take(max).ToArray(),Total=matching.Length,Truncated=matching.Length>max,
					IncludedSecondChance=includeSecondChance,StateVersion=stateVersion,
				};
			},cancellationToken).ConfigureAwait(false);
		}
	}
}
