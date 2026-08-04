using System.Collections.Generic;
using System.ComponentModel.Composition;
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
using dnSpy.Contracts.Extension;

namespace dgSpy.Extension {
	[ExportExtension]
	sealed class ExtensionEntryPoint : IExtension {
		readonly RpcHost host;

		[ImportingConstructor]
		ExtensionEntryPoint(AttachableProcessesService programs, DbgManager manager, DebuggerSettings debuggerSettings, DbgCodeBreakpointsService breakpoints, DbgModuleBreakpointsService moduleBreakpoints, DbgObjectIdService objectIds, DbgDotNetCodeLocationFactory locations, DbgCallStackService callStack, DbgLanguageService languages, DbgExceptionSettingsService exceptions, DbgMetadataService metadataService, IDecompilerService decompilers) =>
			host=new RpcHost(programs,manager,debuggerSettings,breakpoints,moduleBreakpoints,objectIds,locations,callStack,languages,exceptions,metadataService,decompilers);

		public IEnumerable<string> MergedResourceDictionaries { get { yield break; } }
		public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription="dgSpy MCP debugger bridge 0.1.0" };
		public void OnEvent(ExtensionEvent @event,object? obj) {
			if (@event==ExtensionEvent.AppLoaded) host.Start();
			else if (@event==ExtensionEvent.AppExit) host.Dispose();
		}
	}
}
