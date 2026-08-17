using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>
/// Enforcement lives in upstream dnSpy types this assembly cannot construct - <c>DbgManagerImpl</c> and
/// friends are internal to a strong-named assembly with a closed InternalsVisibleTo list - so the ordering
/// they must obey is asserted against their source. The behaviour that ordering buys is measured in
/// <see cref="ActionLeaseAuthorizationTests"/>; this file is what makes one site's regression name that
/// site rather than showing up as a general failure somewhere else.
/// </summary>
public sealed class ActionGuardSiteOrderingTests {
	const string manager = @"Extensions\dnSpy.Debugger\dnSpy.Debugger\Impl\DbgManagerImpl.cs";
	const string steppers = @"Extensions\dnSpy.Debugger\dnSpy.Debugger\Impl\DbgManagerImpl.Steppers.cs";
	const string runtime = @"Extensions\dnSpy.Debugger\dnSpy.Debugger\Impl\DbgRuntimeImpl.cs";
	const string breakpoints = @"Extensions\dnSpy.Debugger\dnSpy.Debugger\Breakpoints\Code\DbgCodeBreakpointsServiceImpl.cs";

	public static TheoryData<string, string, string> Sites => new TheoryData<string, string, string> {
		{ "run all", manager, "public override void RunAll() { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutateAll(PredefinedDbgActionOperations.Continue, authorization)) return; RunAll_DbgThread(); }); }" },
		{ "run one process", manager, "internal void Run(DbgProcessImpl process) { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutate(process, PredefinedDbgActionOperations.Continue, authorization, out _)) return; Run_DbgThread(process); }); }" },
		{ "pause one process", manager, "internal void Break(DbgProcessImpl process) { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutate(process, PredefinedDbgActionOperations.Pause, authorization, out _)) return; Break_DbgThread(process); }); }" },
		{ "detach one process", manager, "internal void Detach(DbgProcessImpl process) { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutate(process, PredefinedDbgActionOperations.Detach, authorization, out _)) return; Detach_DbgThread(process); }); }" },
		{ "terminate one process", manager, "internal void Terminate(DbgProcessImpl process) { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutate(process, PredefinedDbgActionOperations.Terminate, authorization, out _)) return; Terminate_DbgThread(process); }); }" },
		{ "stop debugging all", manager, "public override void StopDebuggingAll() { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutateAll(PredefinedDbgActionOperations.Terminate, authorization)) return; StopDebuggingAll_DbgThread(); }); }" },
		{ "terminate all", manager, "public override void TerminateAll() { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutateAll(PredefinedDbgActionOperations.Terminate, authorization)) return; TerminateAll_DbgThread(); }); }" },
		{ "detach all", manager, "public override void DetachAll() { var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutateAll(PredefinedDbgActionOperations.Detach, authorization)) return; DetachAll_DbgThread(); }); }" },
		{ "stepping", steppers, "var authorization = CaptureActionAuthorization(); DbgThread(() => { if (!CanMutate(stepper.Process, PredefinedDbgActionOperations.Step, authorization, out var actionError)) { RaiseStepperError_DbgThread(stepper, actionError!); return; } Step_DbgThread(stepper, stepperTag, step, singleProcess); });" },
		{ "set instruction pointer", runtime, "var authorization = owner.CaptureActionAuthorization(); Dispatcher.BeginInvoke(() => { if (!owner.CanMutate(Process, PredefinedDbgActionOperations.SetInstructionPointer, authorization, out _)) return; SetIP_DbgThread(thread, location); });" },
		{ "breakpoint modify", breakpoints, "var authorization = CaptureAuthorization(); Dbg(() => { if (!CanMutate(authorization)) return; ModifyCore(settings); });" },
		{ "breakpoint remove", breakpoints, "var authorization = CaptureAuthorization(); Dbg(() => { if (!CanMutate(authorization)) return; RemoveCore(breakpoints); });" },
		{ "breakpoint clear", breakpoints, "public override void Clear() { var authorization = CaptureAuthorization(); Dbg(() => { if (!CanMutate(authorization)) return; RemoveCore(VisibleBreakpoints.ToArray()); }); }" },
		{ "breakpoint add", breakpoints, "void AddCore(List<DbgCodeBreakpointImpl> breakpoints, List<DbgObject>? objsToClose, object?[]? authorization) { dbgDispatcherProvider.VerifyAccess(); if (!CanMutate(authorization)) {" },
	};

	[Theory]
	[MemberData(nameof(Sites))]
	public void Every_guarded_site_re_checks_inside_the_callback_that_mutates(string site, string file, string expected) {
		Assert.False(String.IsNullOrEmpty(site));
		Assert.Contains(expected, Normalize(File.ReadAllText(Path.Combine(RepositoryRoot, file))), StringComparison.Ordinal);
	}

	public static TheoryData<string, string, string> PreFixShapes => new TheoryData<string, string, string> {
		{ "run all", manager, "DbgThread(() => RunAll_DbgThread());" },
		{ "run one process", manager, "DbgThread(() => Run_DbgThread(process));" },
		{ "pause one process", manager, "DbgThread(() => Break_DbgThread(process));" },
		{ "detach one process", manager, "DbgThread(() => Detach_DbgThread(process));" },
		{ "terminate one process", manager, "DbgThread(() => Terminate_DbgThread(process));" },
		{ "stop debugging all", manager, "DbgThread(() => StopDebuggingAll_DbgThread());" },
		{ "terminate all", manager, "DbgThread(() => TerminateAll_DbgThread());" },
		{ "detach all", manager, "DbgThread(() => DetachAll_DbgThread());" },
		{ "stepping", steppers, "DbgThread(() => Step_DbgThread(stepper, stepperTag, step, singleProcess));" },
		{ "set instruction pointer", runtime, "Dispatcher.BeginInvoke(() => SetIP_DbgThread(thread, location));" },
		{ "breakpoint modify", breakpoints, "Dbg(() => ModifyCore(settings));" },
		{ "breakpoint remove", breakpoints, "Dbg(() => RemoveCore(breakpoints));" },
		{ "breakpoint clear", breakpoints, "Dbg(() => RemoveCore(VisibleBreakpoints.ToArray()));" },
	};

	/// <summary>The unguarded marshal each site used to do. Restoring one is how a reviewer proves the
	/// assertion above is not vacuous, and it fails here as well rather than only there.</summary>
	[Theory]
	[MemberData(nameof(PreFixShapes))]
	public void No_guarded_site_still_marshals_straight_to_its_mutation(string site, string file, string preFix) {
		Assert.False(String.IsNullOrEmpty(site));
		Assert.DoesNotContain(preFix, Normalize(File.ReadAllText(Path.Combine(RepositoryRoot, file))), StringComparison.Ordinal);
	}

	[Fact]
	public void The_guard_contract_carries_the_capture_the_sites_depend_on() {
		var source = Normalize(File.ReadAllText(Path.Combine(RepositoryRoot, @"dnSpy\dnSpy.Contracts.Debugger\DbgActionGuard.cs")));
		Assert.Contains("public virtual object? CaptureAuthorization() => null;", source, StringComparison.Ordinal);
		Assert.Contains("public virtual bool TryGetBlock(DbgProcess? process, string operation, object? authorization, out DbgActionBlockInfo info)", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Discovery_faults_are_recorded_in_host_diagnostics() {
		var host = Normalize(File.ReadAllText(Path.Combine(RepositoryRoot, @"Extensions\dgSpy.Extension\Rpc\RpcHost.cs")));
		var identity = Normalize(File.ReadAllText(Path.Combine(RepositoryRoot, @"Extensions\dgSpy.Extension\Identity\RpcHost.Host.cs")));
		Assert.Contains("if (ex is ProgramDiscoveryException) RecordOperationFault(ex.InnerException ?? ex);", host, StringComparison.Ordinal);
		Assert.Contains("DispatcherFaultCount=(dispatcher?.FaultCount ?? 0)+operationFaults", identity, StringComparison.Ordinal);
	}

	static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

	/// <summary>Line comments are removed so a future comment cannot break an ordering assertion, and
	/// whitespace is collapsed so indentation and line breaks cannot either.</summary>
	static string Normalize(string source) =>
		Regex.Replace(Regex.Replace(source, @"//[^\r\n]*", " "), @"\s+", " ");
}
