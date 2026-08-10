using System;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Debugger;

namespace dgSpy.Extension.Debugger {
	[Export(typeof(DbgActionGuard))]
	sealed class ActionLeaseGuard : DbgActionGuard {
		readonly DbgManager manager;
		readonly ActionLeaseCoordinator coordinator;
		readonly ActionLeaseAuthorization authorization;

		[ImportingConstructor]
		ActionLeaseGuard(DbgManager manager) {
			this.manager=manager;
			coordinator=ActionLeaseCoordinator.Shared;
			authorization=new ActionLeaseAuthorization(coordinator);
			coordinator.LeaseChanged+=Coordinator_LeaseChanged;
			coordinator.ExternalDebuggerAction+=Coordinator_ExternalDebuggerAction;
		}

		public override object? CaptureAuthorization() => authorization.Capture();

		/// <summary>Advisory overload for a caller that decides and mutates on one thread. Anything that
		/// marshals must capture first and use the overload below, on the thread that mutates.</summary>
		public override bool TryGetBlock(DbgProcess? process,string operation,out DbgActionBlockInfo info) =>
			TryGetBlock(process,operation,CaptureAuthorization(),out info);

		public override bool TryGetBlock(DbgProcess? process,string operation,object? capturedAuthorization,out DbgActionBlockInfo info) {
			if(authorization.TryGetBlock(process?.Id,operation,capturedAuthorization,out var owner)) {
				info=new DbgActionBlockInfo(owner.ProcessId,owner.ActionName,owner.ActionId,owner.DeadlineUtc,owner.StatusOperation,owner.CancelOperation,owner.FormatBlockReason(operation));
				return true;
			}
			info=default;
			return false;
		}

		public override void ReportBlocked(in DbgActionBlockInfo info) => manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,info.Message);

		void Coordinator_LeaseChanged(ActionLeaseInfo info,bool acquired) {
			var message=acquired
				? $"dgSpy atomic action '{info.ActionName}' ({info.ActionId}) owns process {info.ProcessId} until {info.DeadlineUtc:O}; status '{info.StatusOperation}', cancel '{info.CancelOperation}'."
				: $"dgSpy atomic action '{info.ActionName}' ({info.ActionId}) released process {info.ProcessId}.";
			manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,message);
		}

		void Coordinator_ExternalDebuggerAction(ActionLeaseInfo info,string operation) =>
			manager.WriteMessage(PredefinedDbgManagerMessageKinds.Output,$"external_debugger_action: Atomic action '{info.ActionName}' ({info.ActionId}) was interrupted by '{operation}' for process {info.ProcessId}; cleanup may proceed, but debugger state will not be changed or resumed.");
	}
}
