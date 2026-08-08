using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dgSpy.Protocol;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Documents;

namespace dgSpy.Extension {
	sealed partial class RpcHost {
		Task<WaitResult> WaitForStopAsync(RpcRequest req,CancellationToken cancellationToken) => WaitForEventsAsync(req,new[]{EventKinds.Stopped},cancellationToken);
		Task<WaitResult> WaitForEventAsync(RpcRequest req,CancellationToken cancellationToken) => WaitForEventsAsync(req,ReadKinds(req),cancellationToken);

		async Task<WaitResult> WaitForEventsAsync(RpcRequest req,IReadOnlyCollection<string>? kinds,CancellationToken cancellationToken) {
			CheckSession(req);
			long after=ReadCursor(req);
			int timeout=Math.Min(10000,Math.Max(1,(int?)req.Arguments["timeout_ms"] ?? 5000));
			using var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			wait.CancelAfter(timeout);
			while(true) {
				var snapshot=events.Snapshot(after,kinds);
				if (snapshot.Events.Length!=0 || snapshot.Truncated) return WaitResult(snapshot,false);
				try { await events.WaitForChangeAsync(snapshot.LastEventId,wait.Token).ConfigureAwait(false); }
				catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) {
					var finalSnapshot=events.Snapshot(after,kinds);
					// The event can arrive concurrently with the wait deadline. Never report a timeout
					// alongside the event that satisfied the wait.
					return WaitResult(finalSnapshot,!finalSnapshot.SatisfiesWait);
				}
			}
		}

		EventResult GetEvents(RpcRequest req) {
			CheckSession(req);
			return EventResult(events.Snapshot(ReadCursor(req),ReadKinds(req)));
		}

		/// <summary>Reads <c>after_event_id</c>, rejecting a cursor beyond the newest event. Such a cursor
		/// can never be satisfied — the next events take the ids it would skip — so waiting on it is a
		/// silent forever-timeout. The known way to produce one is feeding a version counter (state_version
		/// or a `versions` field) where an event cursor belongs; say so instead of timing out.</summary>
		long ReadCursor(RpcRequest req) {
			long after=(long?)req.Arguments["after_event_id"] ?? 0;
			var last=events.LastEventId;
			if (after>last) throw new RpcException("cursor_ahead_of_stream",$"after_event_id {after} is beyond the newest event {last} and can never be satisfied. Event cursors come from cursor_event_id, event_id, or last_event_id — a versions counter or state_version is not a cursor.");
			return after;
		}

		DebugEvent GetStopReason(RpcRequest req) {
			CheckSession(req);
			var eventId=(long?)req.Arguments["event_id"];
			if (!eventId.HasValue) return events.Latest(EventKinds.Stopped) ?? throw new RpcException("stop_not_found","The session has no retained stop event.");
			var snapshot=events.Snapshot(0,new[]{EventKinds.Stopped});
			var found=snapshot.Events.FirstOrDefault(e=>e.EventId==eventId.Value);
			if (found is not null) return found;
			if (eventId.Value<snapshot.OldestEventId) throw new RpcException("event_truncated",$"Event {eventId.Value} is no longer retained. Resume from cursor {snapshot.OldestAvailableCursor}.");
			throw new RpcException("stop_not_found",$"Event {eventId.Value} is not a retained stop event.");
		}

		/// <summary>Reject an unrecognized kind instead of filtering on it. A typo used to produce a clean
		/// empty result that is indistinguishable from "the event never happened" — the same false-negative
		/// shape as reading the event cursor too late. The valid set is served by get_capabilities.</summary>
		static string[]? ReadKinds(RpcRequest req) {
			var kinds=ProtocolJson.FromNode<string[]>(req.Arguments["kinds"]);
			if (kinds is null) return null;
			var unknown=kinds.Where(kind=>!EventKinds.IsKnown(kind)).ToArray();
			if (unknown.Length!=0) throw new RpcException("invalid_argument",$"Unknown event kind(s) {string.Join(", ",unknown)}. Valid kinds: {string.Join(", ",EventKinds.All)}.");
			return kinds;
		}
		static EventResult EventResult(EventBufferSnapshot snapshot) => new EventResult { Events=snapshot.Events,OldestEventId=snapshot.OldestEventId,OldestAvailableCursor=snapshot.OldestAvailableCursor,LastEventId=snapshot.LastEventId,Truncated=snapshot.Truncated };
		static WaitResult WaitResult(EventBufferSnapshot snapshot,bool timedOut) => new WaitResult { Events=snapshot.Events,OldestEventId=snapshot.OldestEventId,OldestAvailableCursor=snapshot.OldestAvailableCursor,LastEventId=snapshot.LastEventId,Truncated=snapshot.Truncated,TimedOut=timedOut };

		// dnSpy caches loaded assemblies in IDsDocumentService under a FilenameKey, whose entire identity is
		// the file path compared case-insensitively. Nothing invalidates that entry, so rebuilding a target
		// between two sessions of one dnSpy leaves the debugger resolving metadata for the previous build:
		// method tokens come fresh from the live process but debug info comes from the stale document, and
		// since the new tokens do not exist there, every frame reports zero locals and evaluation fails with
		// "Internal debugger error". The dnSpy GUI hides this because clicking a call stack frame navigates
		// the active tab, which decompiles the live module; nothing in a headless host ever does that.
		//
		// Drop the entry when it demonstrably describes a different image, by comparing the PE
		// TimeDateStamp the cached document was loaded with against the one in the file now on disk.
		//
		// Deliberately not DbgModule.Timestamp. Roslyn sets bit 31 of TimeDateStamp under /deterministic
		// and fills the rest with a content hash rather than a time, so ModuleCreator treats the whole
		// field as unusable and reports null. Every modern build is deterministic, so that property is
		// null for exactly the assemblies this has to work on - while the raw field it discards is a
		// content hash, which is a better staleness discriminator than a timestamp ever was.
		//
		// Matching modules are left alone: evicting every module on every load would re-read the whole
		// framework on the next request for no benefit.
		void DropStaleModuleDocument(DbgModule module) {
			try {
				// In-memory and dynamic modules never reach the document cache: DbgMetadataService reads
				// them straight out of the debuggee, so they cannot go stale and have no file to key on.
				if (module.IsDynamic || module.IsInMemory) return;
				var filename=module.Filename;
				if (string.IsNullOrEmpty(filename)) return;
				var key=new FilenameKey(filename);
				var cached=documentService.Find(key);
				if (cached?.PEImage is not { } image) return;
				var onDisk=FileTimeDateStamp(filename);
				if (onDisk is null || onDisk.Value==image.ImageNTHeaders.FileHeader.TimeDateStamp) return;
				documentService.Remove(key);
				staleModuleDocumentsDropped++;
				manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,$"dgSpy: dropped a stale cached assembly for '{filename}'; it described a different build than the one now running.");
			}
			// Never let cache maintenance break module-load handling: a missed eviction degrades symbols,
			// an exception here would lose the event entirely.
			catch (Exception) { }
		}
		/// <summary>The PE header's TimeDateStamp, read straight from the file. Only the COFF header is
		/// touched, and the file is opened with full sharing because the debuggee has it mapped.</summary>
		static uint? FileTimeDateStamp(string path) {
			try {
				using var stream=new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.ReadWrite|System.IO.FileShare.Delete);
				using var reader=new System.IO.BinaryReader(stream);
				if (stream.Length<0x40) return null;
				stream.Position=0x3C;
				var peOffset=reader.ReadUInt32();
				if (peOffset+8>stream.Length) return null;
				stream.Position=peOffset;
				if (reader.ReadUInt32()!=0x00004550) return null; // "PE\0\0"
				reader.ReadUInt16(); // Machine
				reader.ReadUInt16(); // NumberOfSections
				return reader.ReadUInt32(); // TimeDateStamp
			}
			catch (Exception) { return null; }
		}
		long staleModuleDocumentsDropped;
		public long StaleModuleDocumentsDropped { get { lock(sync) return staleModuleDocumentsDropped; } }

		void OnDebuggerMessage(DbgMessageEventArgs message) {
			lock(sync) if (sessionId is null) return;
			switch(message) {
			case DbgMessageProcessCreatedEventArgs e: Record(new DebugEvent { Kind=EventKinds.ProcessCreated,ProcessId=e.Process.Id }); break;
			case DbgMessageProcessExitedEventArgs e: OnProcessExited(e); break;
			case DbgMessageRuntimeCreatedEventArgs e: Record(RuntimeEvent(EventKinds.RuntimeCreated,e.Runtime)); break;
			case DbgMessageRuntimeExitedEventArgs e: Record(RuntimeEvent(EventKinds.RuntimeExited,e.Runtime)); break;
			case DbgMessageModuleLoadedEventArgs e: DropStaleModuleDocument(e.Module); Record(ModuleEvent(EventKinds.ModuleLoaded,e.Module)); break;
			case DbgMessageModuleUnloadedEventArgs e: Record(ModuleEvent(EventKinds.ModuleUnloaded,e.Module)); break;
			case DbgMessageThreadCreatedEventArgs e: Record(ThreadEvent(EventKinds.ThreadCreated,e.Thread)); break;
			case DbgMessageThreadExitedEventArgs e: var thread=ThreadEvent(EventKinds.ThreadExited,e.Thread); thread.ExitCode=e.ExitCode; Record(thread); break;
			case DbgMessageExceptionThrownEventArgs e: Record(ExceptionEvent(EventKinds.ExceptionThrown,e.Exception)); break;
			case DbgMessageBoundBreakpointEventArgs e: Record(BreakpointEvent(EventKinds.BreakpointHit,e)); break;
			case DbgMessageStepCompleteEventArgs e: Record(ThreadEvent(EventKinds.StepCompleted,e.Thread,error:e.Error)); break;
			case DbgMessageEntryPointBreakEventArgs e: Record(ThreadEvent(EventKinds.EntryPoint,e.Thread)); break;
			case DbgMessageProgramBreakEventArgs e: Record(ThreadEvent(EventKinds.ProgramBreak,e.Thread,runtime:e.Runtime)); break;
			case DbgMessageBreakEventArgs e: Record(ThreadEvent(EventKinds.Break,e.Thread,runtime:e.Runtime)); break;
			}
		}

		void OnProcessPaused(ProcessPausedEventArgs paused) {
			lock(sync) if (sessionId is null) return;
			var messages=paused.Process.Runtimes.SelectMany(runtime=>runtime.BreakInfos)
				.Where(info=>info.Kind==DbgBreakInfoKind.Message).Select(info=>info.Data as DbgMessageEventArgs).Where(message=>message is not null).ToArray();
			var cause=messages.FirstOrDefault(message=>MessageThread(message!)==paused.Thread) ?? messages.FirstOrDefault();
			var value=StopEvent(cause,paused.Process,paused.Thread);
			Record(value);
		}

		DebugEvent StopEvent(DbgMessageEventArgs? cause,DbgProcess process,DbgThread? thread) {
			DebugEvent value;
			switch(cause) {
			case DbgMessageBoundBreakpointEventArgs e: value=BreakpointEvent(EventKinds.Stopped,e); value.StopReason=StopReasons.Breakpoint; break;
			case DbgMessageExceptionThrownEventArgs e: value=ExceptionEvent(EventKinds.Stopped,e.Exception); value.StopReason=StopReasons.Exception; break;
			case DbgMessageStepCompleteEventArgs e: value=ThreadEvent(EventKinds.Stopped,e.Thread,error:e.Error); value.StopReason=StopReasons.Step; break;
			case DbgMessageEntryPointBreakEventArgs e: value=ThreadEvent(EventKinds.Stopped,e.Thread); value.StopReason=StopReasons.EntryPoint; break;
			case DbgMessageProgramBreakEventArgs e: value=ThreadEvent(EventKinds.Stopped,e.Thread,runtime:e.Runtime); value.StopReason=StopReasons.ProgramBreak; break;
			case DbgMessageBreakEventArgs e: value=ThreadEvent(EventKinds.Stopped,e.Thread,runtime:e.Runtime); value.StopReason=StopReasons.Pause; break;
			default: value=ThreadEvent(EventKinds.Stopped,thread,process:process); value.StopReason=StopReasons.Unknown; break;
			}
			value.ProcessId=process.Id;
			if (value.ThreadId is null && thread is not null) value.ThreadId=ThreadId(thread);
			return value;
		}

		DebugEvent BreakpointEvent(string kind,DbgMessageBoundBreakpointEventArgs e) {
			var value=ThreadEvent(kind,e.Thread,runtime:e.BoundBreakpoint.Runtime);
			value.BreakpointId=e.BoundBreakpoint.Breakpoint.Id;
			var location=e.BoundBreakpoint.Breakpoint.Location as DbgDotNetCodeLocation;
			if (location is not null) { value.Module=location.Module.ModuleName; value.MethodToken=location.Token; value.IlOffset=location.Offset; }
			else if (e.BoundBreakpoint.Module is not null) value.Module=e.BoundBreakpoint.Module.Filename;
			return value;
		}

		static DebugEvent ExceptionEvent(string kind,dnSpy.Contracts.Debugger.Exceptions.DbgException exception) {
			var value=ThreadEvent(kind,exception.Thread,runtime:exception.Runtime);
			value.ExceptionId=exception.Id.ToString(); value.ExceptionMessage=exception.Message;
			value.ExceptionFirstChance=exception.IsFirstChance; value.ExceptionUnhandled=exception.IsUnhandled;
			value.Module=exception.Module?.Filename;
			return value;
		}

		static DebugEvent RuntimeEvent(string kind,DbgRuntime runtime) => new DebugEvent { Kind=kind,ProcessId=runtime.Process.Id,RuntimeGuid=runtime.Guid.ToString("D"),RuntimeName=runtime.Name };
		static DebugEvent ModuleEvent(string kind,DbgModule module) { var value=RuntimeEvent(kind,module.Runtime); value.Module=module.Filename; return value; }
		static DebugEvent ThreadEvent(string kind,DbgThread? thread,DbgRuntime? runtime=null,DbgProcess? process=null,string? error=null) {
			runtime=thread?.Runtime ?? runtime;
			process=thread?.Process ?? runtime?.Process ?? process;
			return new DebugEvent { Kind=kind,ProcessId=process?.Id,RuntimeGuid=runtime?.Guid.ToString("D"),RuntimeName=runtime?.Name,ThreadId=thread is null ? null : ThreadId(thread),Error=error };
		}

		static DbgThread? MessageThread(DbgMessageEventArgs message) => message switch {
			DbgMessageBoundBreakpointEventArgs e=>e.Thread,
			DbgMessageExceptionThrownEventArgs e=>e.Exception.Thread,
			DbgMessageEntryPointBreakEventArgs e=>e.Thread,
			DbgMessageProgramBreakEventArgs e=>e.Thread,
			DbgMessageStepCompleteEventArgs e=>e.Thread,
			DbgMessageBreakEventArgs e=>e.Thread,
			_=>null,
		};

		void Record(string kind) => Record(new DebugEvent { Kind=kind });
		void Record(string kind,bool terminal,int? processId,int? exitCode,string? reason) => Record(new DebugEvent { Kind=kind,Terminal=terminal,ProcessId=processId,ExitCode=exitCode,Reason=reason });
		DebugEvent Record(DebugEvent value) { lock(sync) {
			stateVersion++;
			if(StateRevisionKinds.ChangesLifecycle(value.Kind)) { lifecycleVersion++; if(value.Terminal || value.Kind==EventKinds.Detached || value.Kind==EventKinds.SessionEnded) stopId=null; }
			if(value.Kind==EventKinds.Continued) { executionVersion++; stopId=null; }
			else if(value.Kind==EventKinds.Stopped) { executionVersion++; stopId=Guid.NewGuid().ToString("N"); }
			value.LifecycleVersion=lifecycleVersion; value.ExecutionVersion=executionVersion; value.BreakpointsVersion=breakpointsVersion; value.StopId=stopId;
			return events.Add(value,stateVersion);
		} }
		void IncrementBreakpointsVersion() { lock(sync) breakpointsVersion++; }
		void CheckSession(RpcRequest req) { var id=(string?)req.Arguments["session_id"]; if(sessionId is null || id!=sessionId) throw new RpcException("session_not_found","The session_id is not active."); }
		void CheckVersion(RpcRequest req) => CheckExecutionVersion(req);
		void CheckLifecycleVersion(RpcRequest req) => CheckScopedVersion(req,"expected_lifecycle_version",lifecycleVersion,"lifecycle");
		void CheckExecutionVersion(RpcRequest req) { CheckScopedVersion(req,"expected_execution_version",executionVersion,"execution"); var expectedStop=(string?)req.Arguments["expected_stop_id"]; if(expectedStop is not null && expectedStop!=stopId) throw new RpcException("stale_stop",$"Expected stop '{expectedStop}', current stop is '{stopId ?? "none"}'."); }
		void CheckBreakpointsVersion(RpcRequest req) => CheckScopedVersion(req,"expected_breakpoints_version",breakpointsVersion,"breakpoints");
		void CheckScopedVersion(RpcRequest req,string name,long current,string scope) {
			var expected=(long?)req.Arguments[name];
			if(expected.HasValue && expected.Value!=current) throw new RpcException("stale_"+scope,$"Expected {scope} version {expected.Value}, current {scope} version is {current}.");
			var legacy=(long?)req.Arguments["expected_state_version"];
			if(!expected.HasValue && legacy.HasValue && legacy.Value!=stateVersion) throw new RpcException("stale_state",$"Expected state {legacy.Value}, current state is {stateVersion}.");
		}
		void CheckOperationVersion(RpcRequest req) {
			if(req.Arguments["session_id"] is not null) CheckSession(req);
			switch(req.Operation) {
			case "detach": case "terminate": case "restart": CheckLifecycleVersion(req); break;
			case "pause": case "continue": case "step_into": case "step_over": case "step_out": case "set_value": case "invoke_method": case "create_object": case "write_memory": case "set_instruction_pointer": case "create_object_id": case "release_object_id": case "write_value_export": CheckExecutionVersion(req); break;
			case "set_il_breakpoint": case "set_breakpoint": case "remove_breakpoint": case "clear_breakpoints": case "update_breakpoint": case "set_exception_breakpoint": case "set_module_breakpoint": case "update_module_breakpoint": case "remove_module_breakpoint": case "import_breakpoints": case "set_exception_policy": case "remove_exception_policy": case "restore_exception_defaults": CheckBreakpointsVersion(req); break;
			}
		}
	}
}
