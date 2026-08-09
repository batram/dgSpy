using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.Breakpoints.Modules;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;
using dgSpy.Extension.Debugger.OwnedBreakpoints;

namespace dgSpy.Extension {
	[ExportExtension]
	sealed class ExtensionEntryPoint : IExtension {
		readonly RpcHost host;
		readonly IAppWindow appWindow;
		string? connectionStateInfo;
		bool shuttingDown;

		[ImportingConstructor]
		ExtensionEntryPoint(AttachableProcessesService programs, DbgManager manager, DebuggerSettings debuggerSettings, DbgCodeBreakpointsService breakpoints, DbgModuleBreakpointsService moduleBreakpoints, DbgObjectIdService objectIds, DbgDotNetCodeLocationFactory locations, DbgCallStackService callStack, DbgLanguageService languages, DbgExceptionSettingsService exceptions, DbgMetadataService metadataService, IDsDocumentService documentService, [ImportMany] IEnumerable<Lazy<DbgModuleIdProvider>> moduleIdProviders, IDecompilerService decompilers, IAppWindow appWindow, OwnedBreakpointService ownedBreakpoints) {
			host=new RpcHost(programs,manager,debuggerSettings,breakpoints,moduleBreakpoints,objectIds,locations,callStack,languages,exceptions,metadataService,documentService,moduleIdProviders,decompilers,ownedBreakpoints);
			this.appWindow=appWindow;
		}

		public IEnumerable<string> MergedResourceDictionaries { get { yield break; } }
		// One version source. dnSpy's extension list is often the only place a human looks before filing a
		// bug, so it must name the build that is loaded rather than a literal that outlives it.
		public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription="dgSpy MCP debugger bridge "+RpcHost.Version };
		public void OnEvent(ExtensionEvent @event,object? obj) {
			if (@event==ExtensionEvent.AppLoaded) {
				host.ConnectionStateChanged += Host_ConnectionStateChanged;
				host.Start();
				Host_ConnectionStateChanged(host.ConnectionState);
			}
			else if (@event==ExtensionEvent.AppExit) {
				shuttingDown=true;
				host.ConnectionStateChanged -= Host_ConnectionStateChanged;
				if (connectionStateInfo is not null) appWindow.RemoveTitleInfo(connectionStateInfo);
				// Before anything else: an attached target dies with this process, so detaching is the
				// last useful thing this extension can do. Disposing first would tear down the RPC host
				// while the target is still attached and lose it.
				host.DetachTargetsBeforeExit(TimeSpan.FromSeconds(10));
				host.Dispose();
			}
		}
		void Host_ConnectionStateChanged(string state) {
			// The build stamp rides along with the connection state because dnSpy only exposes title text
			// as opaque entries: two entries would be reordered independently and could not be replaced
			// as a unit when the state changes.
			var info=$"dgSpy {RpcHost.BuildLabel} [{state}]";
			appWindow.MainWindow.Dispatcher.BeginInvoke(() => {
				// A queued debugger notification can outlive AppExit. Never put title text back
				// after the extension has started shutting down.
				if (shuttingDown)
					return;
				if (connectionStateInfo is not null)
					appWindow.RemoveTitleInfo(connectionStateInfo);
				connectionStateInfo=info;
				appWindow.AddTitleInfo(info);
			});
		}
	}
}
