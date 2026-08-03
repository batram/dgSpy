using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Extension;

namespace dgSpy.Extension {
	[ExportExtension]
	sealed class ExtensionEntryPoint : IExtension {
		readonly RpcHost host;

		[ImportingConstructor]
		ExtensionEntryPoint(AttachableProcessesService programs, DbgManager manager, DbgCodeBreakpointsService breakpoints, DbgDotNetCodeLocationFactory locations, DbgCallStackService callStack, DbgLanguageService languages) =>
			host=new RpcHost(programs,manager,breakpoints,locations,callStack,languages);

		public IEnumerable<string> MergedResourceDictionaries { get { yield break; } }
		public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription="dgSpy MCP debugger bridge 0.1.0" };
		public void OnEvent(ExtensionEvent @event,object? obj) {
			if (@event==ExtensionEvent.AppLoaded) host.Start();
			else if (@event==ExtensionEvent.AppExit) host.Dispose();
		}
	}
}
