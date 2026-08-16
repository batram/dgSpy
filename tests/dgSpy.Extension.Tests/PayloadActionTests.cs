using System.Text.Json.Nodes;
using dgSpy.Extension.Debugger.AtomicActions;
using HookLab.Contracts;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class PayloadExpressionTests {
	[Fact]
	public void Scan_is_BCL_only_and_has_a_truncation_sentinel() {
		var expression = PayloadExpressions.ResidentGenerationScan();
		Assert.Contains("System.AppDomain.CurrentDomain.GetAssemblies()", expression);
		Assert.Contains(PayloadExpressions.ScanSentinel, expression);
		Assert.DoesNotContain("HookLab.", expression);
		Assert.DoesNotContain("Assembly.Load", expression);
	}

	[Fact]
	public void Prepare_loads_bytes_and_invokes_the_fixed_prepare_entry() {
		var expression = PayloadExpressions.Prepare("C:\\payload\\a\"b.payload", "endpoint=none\ncompletion_path=C:\\done\n");
		Assert.Contains("System.Reflection.Assembly.Load(System.IO.File.ReadAllBytes", expression);
		Assert.Contains("HookLab.Bootstrap.HookLabBootstrap", expression);
		Assert.Contains("GetMethod(\"Prepare\")", expression);
		Assert.Contains("a\\\"b.payload", expression);
	}

	[Fact]
	public void Commit_and_drain_reach_a_byte_loaded_generation_by_index_and_identity() {
		var commit = PayloadExpressions.Commit(7);
		var drain = PayloadExpressions.Drain(7, 23);
		foreach (var expression in new[] { commit, drain }) {
			Assert.Contains("CurrentDomain.GetAssemblies()[7]", expression);
			Assert.Contains("ResidentLauncher", expression);
			Assert.Contains("GenerationIdentity", expression);
			Assert.Contains(PayloadExpressions.GenerationIdentity, expression);
			Assert.DoesNotContain("using ", expression);
		}
		Assert.Contains("GetMethod(\"Commit\")", commit);
		Assert.Contains("GetMethod(\"DrainEvents\")", drain);
		Assert.Contains("new object[]{23}", drain);
	}

	[Fact]
	public void Literal_escapes_code_characters_and_refuses_controls() {
		Assert.Equal("\"a\\\"b\\\\c\"", PayloadExpressions.Literal("a\"b\\c"));
		var error = Assert.Throws<RpcException>(() => PayloadExpressions.Literal("bad\0value"));
		Assert.Equal("invalid_arguments", error.Code);
	}

	[Fact]
	public void Host_identity_constant_matches_the_resident_launcher_source() {
		var source = File.ReadAllText(RepoFile("HookLab", "HookLab.Bootstrap", "ResidentLauncher.cs"));
		Assert.Contains("GenerationIdentity = \"" + PayloadExpressions.GenerationIdentity + "\"", source);
	}

	static string RepoFile(params string[] parts) {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
		Assert.NotNull(directory);
		return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
	}
}

public sealed class PayloadGenerationScanTests {
	[Fact]
	public void Counts_generations_and_keeps_the_first_assembly_index() {
		var scan = PayloadGenerationScan.Parse("mscorlib, Version=4;HookLab.Bootstrap, Version=1;other;HookLab.Bootstrap, Version=1;" + PayloadExpressions.ScanSentinel);
		Assert.True(scan.Complete);
		Assert.Equal(4, scan.AssemblyCount);
		Assert.Equal(2, scan.GenerationCount);
		Assert.Equal(1, scan.FirstGenerationIndex);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("mscorlib;HookLab.Bootstrap, Version=1")]
	public void Refuses_absent_or_truncated_inventory(string? inventory) {
		var scan = PayloadGenerationScan.Parse(inventory);
		Assert.False(scan.Complete);
		Assert.Contains(inventory is null ? "no string" : "truncated", scan.Refusal!, StringComparison.OrdinalIgnoreCase);
	}
}

public sealed class PayloadActionWiringTests {
	[Fact]
	public void Rpc_host_registers_payload_through_the_real_action_and_host_adapters() {
		var source = File.ReadAllText(RepoFile("Extensions", "dgSpy.Extension", "Debugger", "AtomicActions", "RpcHost.AtomicActions.cs"));
		Assert.Contains("\"payload\" => new PayloadAction(new RpcPayloadEvaluator(this,req),new HostHookLabPayloadSource(),PayloadActionRequest.Parse(req.Arguments))", source);
	}

	[Fact]
	public void Focused_suite_compiles_the_product_payload_action_source() {
		var project = File.ReadAllText(RepoFile("tests", "dgSpy.Extension.Tests", "dgSpy.Extension.Tests.csproj"));
		Assert.Contains("Debugger\\AtomicActions\\PayloadAction.cs", project);
		Assert.Contains("Link=\"Debugger\\AtomicActions\\PayloadAction.cs\"", project);
	}

	static string RepoFile(params string[] parts) {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
		Assert.NotNull(directory);
		return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
	}
}

public sealed class PayloadActionRequestTests {
	[Fact]
	public void Prepare_requires_explicit_reachable_endpoint_and_completion_path() {
		AssertInvalid(new JsonObject { ["payload_operation"] = "prepare", ["payload_parameters"] = new JsonObject() }, "endpoint");
		Assert.Equal(PayloadOperation.prepare,PayloadActionRequest.Parse(PrepareArgs("pipe",@"C:\done")).Operation);
		AssertInvalid(PrepareArgs("none", null), "completion_path");
	}

	[Fact]
	public void Commit_and_drain_accept_no_prepare_parameters() {
		Assert.Equal(PayloadOperation.commit, PayloadActionRequest.Parse(new JsonObject { ["payload_operation"] = "commit" }).Operation);
		Assert.Equal(PayloadOperation.shutdown, PayloadActionRequest.Parse(new JsonObject { ["payload_operation"] = "shutdown" }).Operation);
		var drain = PayloadActionRequest.Parse(new JsonObject { ["payload_operation"] = "drain", ["drain_max"] = 17 });
		Assert.Equal(17, drain.DrainMax);
		AssertInvalid(new JsonObject { ["payload_operation"] = "commit", ["payload_parameters"] = new JsonObject() }, "prepare or install only");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(257)]
	public void Drain_is_bounded(int maximum) =>
		AssertInvalid(new JsonObject { ["payload_operation"] = "drain", ["drain_max"] = maximum }, "between 1 and 256");

	[Fact]
	public void Parameters_are_fixed_data_not_expression_fragments() {
		var bounded = PayloadActionRequest.Parse(PrepareArgs("pipe", @"C:\done", ("maximum_events_per_second", "25"), ("maximum_string_length", "128")));
		Assert.Contains("maximum_events_per_second=25\n", bounded.Compose(42, "1"));
		Assert.Contains("maximum_string_length=128\n", bounded.Compose(42, "1"));
		AssertInvalid(PrepareArgs("none", "x\ncode=evil"), "character");
		AssertInvalid(PrepareArgs("none", @"C:\done", ("surprise", "value")), "unknown key");
		Assert.Contains("hook_source_base64="+new string('x',2048)+"\n",PayloadActionRequest.Parse(PrepareArgs("none",@"C:\done",("hook_source_base64",new string('x',2048)))).Compose(42,"1"));
		AssertInvalid(PrepareArgs("none", @"C:\done", ("hook_source_base64", new string('x', 2049))), "exceeds 2048");
	}

	[Fact]
	public void Authenticated_endpoint_secret_is_forwarded_only_to_pipe_endpoints() {
		const string secret="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
		var request=PayloadActionRequest.Parse(PrepareArgs("pipe",@"C:\done",("endpoint_secret_base64",secret)));
		Assert.Contains("endpoint_secret_base64="+secret+"\n",request.Compose(42,"1"));
		AssertInvalid(PrepareArgs("none",@"C:\done",("endpoint_secret_base64",secret)),"requires endpoint=pipe");
	}

	[Fact]
	public void Compose_cross_checks_process_and_keeps_carrier_separate_from_hook_target() {
		var request = PayloadActionRequest.Parse(PrepareArgs("none", @"C:\done", ("hook_id", "target-hook"), ("hook_metadata_token", "123")));
		var composed = request.Compose(42, "7");
		Assert.Contains("process_id=42\n", composed);
		Assert.Contains("appdomain_id=7\n", composed);
		Assert.Equal("target-hook", request.HookId);
		AssertInvalidCompose(PrepareArgs("none", @"C:\done", ("process_id", "41")), 42, "does not name the process");
	}

	[Fact]
	public void Host_parameter_key_copy_matches_bootstrap_authority() {
		var source = File.ReadAllText(RepoFile("HookLab", "HookLab.Bootstrap", "BootstrapParameters.cs"));
		foreach (var key in PayloadActionRequest.KnownParameterKeys) Assert.Contains("\"" + key + "\"", source);
		var knownBlock = source.Substring(source.IndexOf("KnownKeys", StringComparison.Ordinal), source.IndexOf("HookKeys", StringComparison.Ordinal) - source.IndexOf("KnownKeys", StringComparison.Ordinal));
		Assert.Equal(PayloadActionRequest.KnownParameterKeys.Length, knownBlock.Count(c => c == '"') / 2);
		Assert.Contains("MaximumValueLength = "+PayloadActionRequest.MaxParameterValueLength+";",source);
	}

	static JsonObject PrepareArgs(string endpoint, string? completion, params (string Key, string Value)[] extra) {
		var parameters = new JsonObject { ["endpoint"] = endpoint };
		if (completion != null) parameters["completion_path"] = completion;
		foreach (var pair in extra) parameters[pair.Key] = pair.Value;
		return new JsonObject { ["payload_operation"] = "prepare", ["payload_parameters"] = parameters };
	}

	static void AssertInvalid(JsonObject arguments, string message) {
		var error = Assert.Throws<RpcException>(() => PayloadActionRequest.Parse(arguments));
		Assert.Equal("invalid_arguments", error.Code);
		Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	static void AssertInvalidCompose(JsonObject arguments, int processId, string message) {
		var request = PayloadActionRequest.Parse(arguments);
		var error = Assert.Throws<RpcException>(() => request.Compose(processId, "default"));
		Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
	}

	static string RepoFile(params string[] parts) {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
		Assert.NotNull(directory);
		return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
	}
}

public sealed class PayloadActionTests {
	const string Common = "status=ok\nresidency_commit=completed\nbehavior_commit=not_started\nprototype_compromises=endpoint_none\n";

	[Fact]
	public async Task Prepare_scans_before_open_or_load_and_refuses_a_second_generation() {
		var log = new List<string>();
		var evaluator = new Evaluator(log, Evaluation(Inventory("HookLab.Bootstrap, Version=1")));
		var source = new PayloadSource(log);
		var action = new PayloadAction(evaluator, source, PrepareRequest());
		var execution = await action.ExecuteAsync(Context(), default);
		Assert.False(execution.Completed);
		Assert.Equal(new[] { "evaluate:scan" }, log);
		Assert.Contains("second", execution.Error!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Prepare_holds_payload_across_evaluation_and_records_both_digests() {
		var log = new List<string>();
		var evaluator = new Evaluator(log, Evaluation(Inventory()), Evaluation(Common + "generation_identity=" + PayloadExpressions.GenerationIdentity + "\npayloads_resident=true\n", "audit-7"));
		var source = new PayloadSource(log);
		var action = new PayloadAction(evaluator, source, PrepareRequest());
		var execution = await action.ExecuteAsync(Context(), default);
		Assert.True(execution.Completed);
		Assert.Equal("audit-7", execution.MutationAuditId);
		Assert.Equal(new[] { "evaluate:scan", "open", "evaluate:prepare", "rehash", "dispose" }, log);
		Assert.Contains("\"payload_sha256_before\":\"AA\"", execution.Evidence!);
		Assert.Contains("\"payload_sha256_after\":\"AA\"", execution.Evidence!);
	}

	[Fact]
	public async Task Changed_payload_digest_makes_a_completed_evaluation_ambiguous() {
		var evaluator = new Evaluator(new(), Evaluation(Inventory()), Evaluation(Common));
		var action = new PayloadAction(evaluator, new PayloadSource(new()) { Rehash = "BB" }, PrepareRequest());
		var execution = await action.ExecuteAsync(Context(), default);
		Assert.False(execution.Completed);
		Assert.True(execution.MayHaveExecuted);
		Assert.Contains("changed", execution.Error!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Compile_failure_is_the_only_load_attempt_that_can_disclaim_residency() {
		var compiler = new PayloadAction(new Evaluator(new(), Evaluation(Inventory()), new PayloadEvaluation { Completed = false, CompilerError = true }), new PayloadSource(new()), PrepareRequest());
		var runtime = new PayloadAction(new Evaluator(new(), Evaluation(Inventory()), new PayloadEvaluation { Completed = false, Error = "timeout" }), new PayloadSource(new()), PrepareRequest());
		Assert.False((await compiler.ExecuteAsync(Context(), default)).MayHaveExecuted);
		Assert.True((await runtime.ExecuteAsync(Context(), default)).MayHaveExecuted);
	}

	[Theory]
	[InlineData("commit", "Commit")]
	[InlineData("drain", "DrainEvents")]
	[InlineData("shutdown", "Shutdown")]
	public async Task Resident_operations_scan_then_invoke_one_fixed_entry(string operation, string method) {
		var log = new List<string>();
		var evaluator = new Evaluator(log, Evaluation(Inventory("HookLab.Bootstrap, Version=1")), Evaluation(Common + (operation == "commit" ? "worker_started=true\n" : "count=2\ndropped=0\n")));
		var action = new PayloadAction(evaluator, new PayloadSource(log), Request(operation));
		var execution = await action.ExecuteAsync(Context(), default);
		Assert.True(execution.Completed);
		Assert.Equal(new[] { "evaluate:scan", "evaluate:" + operation }, log);
		Assert.Contains("GetMethod(\\u0022" + method + "\\u0022)", execution.Evidence!);
	}

	[Fact]
	public async Task Verify_reads_only_the_execution_report_and_rejects_missing_contract_fields() {
		var evaluator = new Evaluator(new());
		var action = new PayloadAction(evaluator, new PayloadSource(new()), Request("commit"));
		var good = await action.VerifyAsync(Context(), Execution(Common + "worker_started=true\n"), default);
		var bad = await action.VerifyAsync(Context(), Execution("status=ok\nresidency_commit=completed\nbehavior_commit=done\n"), default);
		Assert.True(good.Verified);
		Assert.False(bad.Verified);
		Assert.Contains("prototype_compromises", bad.Error!);
		Assert.Empty(evaluator.Expressions);
	}

	[Fact]
	public async Task Verify_rejects_wrong_generation_and_untruthful_drain() {
		var prepare = new PayloadAction(new Evaluator(new()), new PayloadSource(new()), PrepareRequest());
		var drain = new PayloadAction(new Evaluator(new()), new PayloadSource(new()), Request("drain"));
		Assert.False((await prepare.VerifyAsync(Context(), Execution(Common + "generation_identity=other\npayloads_resident=true\n"), default)).Verified);
		Assert.False((await drain.VerifyAsync(Context(), Execution(Common + "count=2\n"), default)).Verified);
	}

	[Fact]
	public async Task Cleanup_is_not_required_and_names_residency_reconciliation() {
		var action = new PayloadAction(new Evaluator(new(), Evaluation(Inventory()), Evaluation(Common)), new PayloadSource(new()), PrepareRequest());
		await action.ExecuteAsync(Context(), default);
		var cleanup = await action.CleanupAsync(CleanupContext(), default);
		Assert.Equal(CleanupOutcome.not_required, cleanup.Outcome);
		Assert.Equal(PayloadAction.ResidencyReconciliationOperation, cleanup.ReconciliationOperation);
		Assert.Contains("payloads_resident=true", cleanup.Evidence!);
		Assert.Contains("residency_rollback=not_possible", cleanup.Evidence!);
	}

	[Fact]
	public async Task Refused_commit_does_not_claim_payloads_are_resident() {
		var action = new PayloadAction(new Evaluator(new(), Evaluation(Inventory())), new PayloadSource(new()), Request("commit"));
		var execution = await action.ExecuteAsync(Context(), default);
		Assert.False(execution.Completed);
		var cleanup = await action.CleanupAsync(CleanupContext(), default);
		Assert.Contains("payloads_resident=false", cleanup.Evidence!);
	}

	static PayloadActionRequest PrepareRequest() => PayloadActionRequest.Parse(new JsonObject {
		["payload_operation"] = "prepare", ["payload_parameters"] = new JsonObject { ["endpoint"] = "none", ["completion_path"] = @"C:\done" }
	});
	static PayloadActionRequest Request(string operation) => PayloadActionRequest.Parse(new JsonObject { ["payload_operation"] = operation });
	static PayloadEvaluation Evaluation(string text, string? audit = null) => new() { Completed = true, Text = text, AuditId = audit };
	static string Inventory(params string[] assemblies) => string.Join(";", assemblies.Concat(new[] { PayloadExpressions.ScanSentinel }));
	static AtomicActionExecution Execution(string report) => new() { Completed = true, Evidence = new JsonObject { ["report"] = report }.ToJsonString() };
	static AtomicActionContext Context() {
		var request = new AtomicActionRequest { ProcessId = 42 };
		var slot = new AtomicActionSlot("carrier.dll", 123, 4);
		var stop = new AtomicActionStop("CLR v4", "7", 42, "thread", "carrier.dll", 123, 4, AtomicActionEvaluationProbe.Clear);
		return new AtomicActionContext(request, stop, slot, "audit");
	}
	static AtomicActionCleanupContext CleanupContext() => new(Context(), ActionOutcome.completed, InterruptionReason.none, true, DateTime.UtcNow.AddSeconds(1));

	sealed class Evaluator : IPayloadEvaluator {
		readonly Queue<PayloadEvaluation> results;
		readonly List<string> log;
		public Evaluator(List<string> log, params PayloadEvaluation[] results) { this.log = log; this.results = new(results); }
		public List<string> Expressions { get; } = new();
		public Task<PayloadEvaluation> EvaluateAsync(AtomicActionContext context, string expression, int timeoutMs, CancellationToken cancellationToken) {
			Expressions.Add(expression);
			var kind = expression == PayloadExpressions.ResidentGenerationScan() ? "scan" : expression.Contains("DrainEvents", StringComparison.Ordinal) ? "drain" : expression.Contains("GetMethod(\"Shutdown\")", StringComparison.Ordinal) ? "shutdown" : expression.Contains("GetMethod(\"Commit\")", StringComparison.Ordinal) ? "commit" : "prepare";
			log.Add("evaluate:" + kind);
			return Task.FromResult(results.Dequeue());
		}
	}

	sealed class PayloadSource : IPayloadSource {
		readonly List<string> log;
		public PayloadSource(List<string> log) => this.log = log;
		public string Rehash { get; set; } = "AA";
		public IPayloadHandle Open() { log.Add("open"); return new Handle(log, Rehash); }
	}

	sealed class Handle : IPayloadHandle {
		readonly List<string> log; readonly string rehash;
		public Handle(List<string> log, string rehash) { this.log = log; this.rehash = rehash; }
		public string Path => @"C:\payload.payload";
		public string Sha256 => "AA";
		public long Length => 17;
		public string RehashFromHandle() { log.Add("rehash"); return rehash; }
		public void Dispose() => log.Add("dispose");
	}
}
