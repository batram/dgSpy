using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HookLab.Contracts;

namespace dgSpy.Extension.Debugger.AtomicActions {
	/// <summary>The four fixed operations a host drives to install a probe into a live target, read its
	/// events. They are separate atomic actions on purpose, and the reason is measured rather than stylistic:
	/// the target does <b>not</b> run between two evaluations held under one stop - two trivial evaluations
	/// 300 ms apart produced one continuous 531.8 ms target freeze - so preparation and commit must be two
	/// runs with a real resume between them, which is exactly what <c>resume_policy=resume</c> already gives.
	/// A single action doing both would hold the target frozen across the whole of it.</summary>
	public enum PayloadOperation {
		/// <summary>CoreCLR-only combined prepare and commit, used because a second func-eval cannot be
		/// reached after the first run-all-threads payload evaluation.</summary>
		initialize,
		/// <summary>Byte-loads the payload and runs <c>HookLabBootstrap.Prepare</c>: resolver, probe,
		/// contracts and Harmony become permanently resident, the target is validated, and the commit entry
		/// is pre-JITted. This is the residency commit and it cannot be undone.</summary>
		prepare,
		/// <summary>Invokes the already-resident launcher, which starts one worker and returns. Completion is
		/// published by that worker to <c>completion_path</c> and is read out-of-band: polling it through a
		/// second stop would cost another 110-130 ms of target freeze.</summary>
		commit,
		/// <summary>Reads a bounded batch of hook events out of the resident probe.</summary>
		drain,
		/// <summary>Installs another guarded hook into the resident probe.</summary>
		install,
		/// <summary>Removes one HookLab-owned patch from the resident probe.</summary>
		uninstall,
		/// <summary>Unpatches every installed hook while leaving the byte-loaded payload resident.</summary>
		shutdown,
	}

	/// <summary>
	/// Every expression this action ever evaluates, composed here and nowhere else.
	///
	/// <para><b>Why the shapes are what they are.</b> An expression compiles in the frame's module context,
	/// so no expression can name a byte-loaded type: the payload's types are reached through
	/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> and reflection, never by name. Everything below is
	/// mscorlib-only for the same reason - <c>System.Linq</c> and <c>System.Text.RegularExpressions</c> live
	/// in assemblies an arbitrary carrier frame need not reference.</para>
	///
	/// <para><b>Nothing here is caller-composed.</b> The only caller-supplied text that reaches an expression
	/// is a path and a bounded key/value parameter block, and both go through <see cref="Literal"/>, which
	/// refuses control characters and escapes everything else. There is no code path that concatenates a
	/// caller string into an expression unescaped.</para>
	/// </summary>
	public static class PayloadExpressions {
		public const string BootstrapTypeName="HookLab.Bootstrap.HookLabBootstrap";
		public const string LauncherTypeName="HookLab.Bootstrap.ResidentLauncher";
		/// <summary>The prefix of the payload assembly's display name. A pre-load scan can only see display
		/// names, so this - not <see cref="GenerationIdentity"/> - is what finds a generation before anything
		/// of ours is resident. The identity is what confirms <em>which</em> generation was found, and it is
		/// checked in the target before any resident method is invoked.</summary>
		public const string GenerationAssemblyPrefix="HookLab.Bootstrap,";
		/// <summary>Mirrors <c>HookLab.Bootstrap.ResidentLauncher.GenerationIdentity</c>. The payload targets
		/// net48 and is never referenced by the host, so the constant is duplicated rather than imported;
		/// <c>PayloadExpressionTests</c> asserts the two still agree by reading the payload source.</summary>
		public const string GenerationIdentity="HookLab.Bootstrap.Stage1.v1";
		/// <summary>Appended to the assembly-inventory scan so a truncated answer is detectable. The scan is
		/// the resident-generation guard; a silently shortened inventory would under-count generations and
		/// let a second permanently unloadable one be loaded, so a missing sentinel refuses.</summary>
		public const string ScanSentinel="#dgspy-generation-scan-end";
		public const string ScanSeparator=";";
		/// <summary>What the guarded invoke returns instead of calling anything when the assembly at the
		/// scanned index is not the generation this host expects.</summary>
		public const string IdentityMismatchReport="status=error\nerror_type=generation_identity_mismatch\n";

		const string Assemblies="System.AppDomain.CurrentDomain.GetAssemblies()";

		/// <summary>The host-side resident-generation scan, and the only thing that may be evaluated before a
		/// load. It loads nothing: <c>GetAssemblies</c> and <c>String.Join</c> are mscorlib calls that need
		/// nothing of ours resident, which is exactly why the guard has to live here and not inside the
		/// bootstrap - a scan inside the payload would have to load the assembly it exists to refuse.</summary>
		public static string ResidentGenerationScan() =>
			"System.String.Join("+Literal(ScanSeparator)+",(object[])"+Assemblies+")+"+Literal(ScanSeparator+ScanSentinel);

		/// <summary>Load the payload from bytes and run <c>Prepare</c> in one evaluation. One evaluation, not
		/// two, because a stop held across two of them freezes the target for both plus the host delay
		/// between them.</summary>
		public static string Prepare(string payloadPath,string parameters) {
			if(String.IsNullOrEmpty(payloadPath)) throw new ArgumentException("A payload path is required.",nameof(payloadPath));
			if(String.IsNullOrEmpty(parameters)) throw new ArgumentException("Bootstrap parameters are required.",nameof(parameters));
			return "(string)System.Reflection.Assembly.Load(System.IO.File.ReadAllBytes("+Literal(payloadPath)+"))"
				+".GetType("+Literal(BootstrapTypeName)+").GetMethod(\"Prepare\").Invoke(null,new object[]{"+Literal(parameters)+"})";
		}
		public static string PrepareAndCommit(string payloadPath,string parameters) {
			if(String.IsNullOrEmpty(payloadPath)) throw new ArgumentException("A payload path is required.",nameof(payloadPath));
			if(String.IsNullOrEmpty(parameters)) throw new ArgumentException("Bootstrap parameters are required.",nameof(parameters));
			return "(string)System.Reflection.Assembly.Load(System.IO.File.ReadAllBytes("+Literal(payloadPath)+"))"
				+".GetType("+Literal(BootstrapTypeName)+").GetMethod(\"PrepareAndCommit\").Invoke(null,new object[]{"+Literal(parameters)+"})";
		}

		public static string Commit(int generationIndex) => GuardedInvoke(generationIndex,"Commit","null");

		public static string Drain(int generationIndex,int max) {
			if(max<=0) throw new ArgumentOutOfRangeException(nameof(max));
			return GuardedInvoke(generationIndex,"DrainEvents","new object[]{"+max.ToString(CultureInfo.InvariantCulture)+"}");
		}

		public static string Shutdown(int generationIndex) => GuardedInvoke(generationIndex,"Shutdown","null");
		public static string Install(int generationIndex,string parameters) => GuardedInvoke(generationIndex,"InstallHook","new object[]{"+Literal(parameters)+"}");
		public static string Uninstall(int generationIndex,string patchId) => GuardedInvoke(generationIndex,"UninstallHook","new object[]{"+Literal(patchId)+"}");

		/// <summary>Reads <c>ResidentLauncher.GenerationIdentity</c> out of the assembly at the scanned index
		/// and invokes the requested entry only if it matches. The identity check is the condition of a
		/// ternary rather than a second evaluation for two reasons: a second evaluation is another 110-130 ms
		/// of target freeze, and a check performed after the call would have already run it.</summary>
		static string GuardedInvoke(int generationIndex,string method,string arguments) {
			if(generationIndex<0) throw new ArgumentOutOfRangeException(nameof(generationIndex));
			var assembly=Assemblies+"["+generationIndex.ToString(CultureInfo.InvariantCulture)+"]";
			var identity=assembly+".GetType("+Literal(LauncherTypeName)+").GetField(\"GenerationIdentity\").GetValue(null)";
			var call=assembly+".GetType("+Literal(BootstrapTypeName)+").GetMethod("+Literal(method)+").Invoke(null,"+arguments+")";
			return "(string)("+Literal(GenerationIdentity)+".Equals("+identity+")?"+call+":(object)"+Literal(IdentityMismatchReport)+")";
		}

		/// <summary>A C# string literal for the target's compiler. Control characters are refused rather than
		/// escaped away: nothing this action legitimately sends contains one, and refusing keeps the set of
		/// bytes that can reach a compiled expression as small as the set that can be reasoned about.</summary>
		public static string Literal(string value) {
			if(value is null) throw new ArgumentNullException(nameof(value));
			var builder=new StringBuilder(value.Length+2);
			builder.Append('"');
			foreach(var character in value) {
				switch(character) {
					case '"': builder.Append("\\\""); break;
					case '\\': builder.Append("\\\\"); break;
					case '\n': builder.Append("\\n"); break;
					case '\r': builder.Append("\\r"); break;
					case '\t': builder.Append("\\t"); break;
					default:
						if(character<' ' || character==(char)0x7f)
							throw new RpcException("invalid_arguments","A payload expression literal may not contain control characters.");
						builder.Append(character);
						break;
				}
			}
			builder.Append('"');
			return builder.ToString();
		}
	}

	/// <summary>What the assembly-inventory scan found. Parsed host-side from one string the target composed,
	/// so the count and the index come from the same reading and cannot disagree.</summary>
	public sealed class PayloadGenerationScan {
		PayloadGenerationScan(bool complete,int assemblyCount,int generationCount,int firstGenerationIndex,string? refusal) {
			Complete=complete; AssemblyCount=assemblyCount; GenerationCount=generationCount; FirstGenerationIndex=firstGenerationIndex; Refusal=refusal;
		}
		/// <summary>True only when the sentinel arrived, which is what proves the inventory was not truncated.
		/// Everything else on this object is meaningless while it is false.</summary>
		public bool Complete { get; }
		public int AssemblyCount { get; }
		public int GenerationCount { get; }
		/// <summary>The index into <c>GetAssemblies()</c> of the first resident generation, or -1.</summary>
		public int FirstGenerationIndex { get; }
		/// <summary>Why an incomplete scan is unusable, for the report. Null when <see cref="Complete"/>.</summary>
		public string? Refusal { get; }

		public static PayloadGenerationScan Parse(string? inventory) {
			if(inventory is null) return new PayloadGenerationScan(false,0,0,-1,"The resident-generation scan returned no string value, so the assembly inventory is unknown.");
			var entries=inventory.Split(PayloadExpressions.ScanSeparator[0]);
			if(entries.Length==0 || entries[entries.Length-1]!=PayloadExpressions.ScanSentinel)
				return new PayloadGenerationScan(false,0,0,-1,"The resident-generation scan did not end with its sentinel, so the assembly inventory was truncated and a second generation cannot be ruled out.");
			var count=0; var first=-1;
			for(var index=0;index<entries.Length-1;index++) {
				if(!entries[index].StartsWith(PayloadExpressions.GenerationAssemblyPrefix,StringComparison.Ordinal)) continue;
				if(first<0) first=index;
				count++;
			}
			return new PayloadGenerationScan(true,entries.Length-1,count,first,null);
		}
	}

	/// <summary>The bootstrap's line-oriented key/value report, read exactly as it was returned. Duplicate
	/// keys keep the first value: a report that says two different things about one key is a defect, and
	/// silently preferring the later one would hide it.</summary>
	public sealed class PayloadReport {
		readonly Dictionary<string,string> values;
		PayloadReport(string text,Dictionary<string,string> values) { Text=text; this.values=values; }
		public string Text { get; }
		public string? Status => Value("status");
		public string? Value(string key) => key is not null && values.TryGetValue(key,out var value) ? value : null;
		public bool Has(string key) => key is not null && values.ContainsKey(key);

		public static PayloadReport Parse(string? text) {
			var values=new Dictionary<string,string>(StringComparer.Ordinal);
			if(String.IsNullOrEmpty(text)) return new PayloadReport("",values);
			foreach(var line in text!.Split('\n')) {
				var trimmed=line.Trim('\r',' ','\t');
				if(trimmed.Length==0) continue;
				var separator=trimmed.IndexOf('=');
				if(separator<=0) continue;
				var key=trimmed.Substring(0,separator);
				if(!values.ContainsKey(key)) values.Add(key,trimmed.Substring(separator+1));
			}
			return new PayloadReport(text!,values);
		}
	}

	/// <summary>One evaluation's outcome, reduced to what this action can decide from. Deliberately not
	/// dnSpy's or the protocol's type: the action is testable only because it never names either.</summary>
	public sealed class PayloadEvaluation {
		/// <summary>The evaluation produced a value rather than an error. It says nothing about whether the
		/// payload accepted the request - that is what the returned report says, and it is checked in
		/// verification.</summary>
		public bool Completed { get; set; }
		/// <summary>The raw string the expression returned, or null when it returned no raw value.</summary>
		public string? Text { get; set; }
		public string? Error { get; set; }
		/// <summary>True when the expression did not compile, which means nothing ran in the target. This is
		/// the only thing that lets a refusal claim it loaded nothing.</summary>
		public bool? CompilerError { get; set; }
		public string? AuditId { get; set; }
	}

	public interface IPayloadEvaluator {
		/// <summary>Evaluates one host-composed expression on the owned stop's thread, with func-eval and side
		/// effects enabled. <c>capture</c> is not an alternative: it sets <c>allow_func_eval=false</c>, and a
		/// live sweep found 13 optimized offsets where a capture failed while a real func-eval at the same
		/// stop succeeded and returned a value - so a capture proves nothing about evaluability here.</summary>
		Task<PayloadEvaluation> EvaluateAsync(AtomicActionContext context,string expression,int timeoutMs,CancellationToken cancellationToken);
	}

	/// <summary>An open, digest-verified payload file. The handle is held across the whole evaluation so a
	/// same-user process cannot swap the bytes under the target mid-read.</summary>
	public interface IPayloadHandle : IDisposable {
		string Path { get; }
		string Sha256 { get; }
		long Length { get; }
		/// <summary>Re-hashes from the still-open handle, so it measures the bytes that were delivered rather
		/// than whatever is at the path afterwards.</summary>
		string RehashFromHandle();
	}

	public interface IPayloadSource { IPayloadHandle Open(); }

	/// <summary>Everything a payload action needs that can be decided without touching the debugger.</summary>
	public sealed class PayloadActionRequest {
		public const int DefaultDrainMax=64;
		public const int MaxDrainMax=256;
		/// <summary>Mirrors <c>HookLab.Bootstrap.BootstrapParameters.KnownKeys</c>, which is the authority.
		/// The payload is a net48 assembly the host never references, so the list is duplicated rather than
		/// imported; <c>PayloadActionRequestTests</c> reads that source file and fails when they diverge.</summary>
		public static readonly string[] KnownParameterKeys={
			"host_id","image_path","process_id","process_creation_utc_ticks","architecture","runtime_id",
			"appdomain_id","event_capacity","byte_capacity","endpoint","endpoint_secret_base64","completion_path",
			"hook_id","hook_kind","hook_assembly","hook_type","hook_method","hook_module_mvid",
			"hook_metadata_token","hook_declaring_type","hook_method_signature","hook_il_sha256",
			"hook_source_base64","hook_revision","maximum_events_per_second","maximum_string_length",
		};
		internal const int MaxParameterValueLength=2048;
		const int MaxParameterBytes=8192;
		const int MaxParameterLines=64;

		PayloadActionRequest(PayloadOperation operation,List<KeyValuePair<string,string>> parameters,string? patchId,int drainMax,int evaluationTimeoutMs) {
			Operation=operation; Parameters=parameters; PatchId=patchId; DrainMax=drainMax; EvaluationTimeoutMs=evaluationTimeoutMs;
		}

		public PayloadOperation Operation { get; }
		/// <summary>The bootstrap parameter block, in the order the caller supplied it. Empty for
		/// <c>commit</c> and <c>drain</c>, which reuse what preparation already installed.</summary>
		public IReadOnlyList<KeyValuePair<string,string>> Parameters { get; }
		public int DrainMax { get; }
		public string? PatchId { get; }
		public int EvaluationTimeoutMs { get; }
		public string? CompletionPath => Find("completion_path");
		/// <summary>The hook this preparation will install, or null. It is deliberately separate from the
		/// carrier - the request's module/method/offset - because an optimized binary refuses func-eval at
		/// most offsets: in the optimized fixture three small methods had zero evaluable naturally-reached
		/// offsets while <c>Main</c> had 13, so the method the breakpoint sits on cannot be assumed to be the
		/// method being hooked.</summary>
		public string? HookId => Find("hook_id");

		string? Find(string key) {
			foreach(var pair in Parameters) if(String.Equals(pair.Key,key,StringComparison.Ordinal)) return pair.Value;
			return null;
		}

		public static PayloadActionRequest Parse(JsonObject? arguments) {
			if(arguments is null) throw new RpcException("invalid_arguments","payload_operation is required.");
			var name=(string?)arguments["payload_operation"];
			if(String.IsNullOrWhiteSpace(name)) throw new RpcException("invalid_arguments","payload_operation is required.");
			PayloadOperation operation;
			switch(name) {
				case "initialize": operation=PayloadOperation.initialize; break;
				case "prepare": operation=PayloadOperation.prepare; break;
				case "commit": operation=PayloadOperation.commit; break;
				case "drain": operation=PayloadOperation.drain; break;
				case "install": operation=PayloadOperation.install; break;
				case "uninstall": operation=PayloadOperation.uninstall; break;
				case "shutdown": operation=PayloadOperation.shutdown; break;
				default: throw new RpcException("invalid_arguments","payload_operation must be initialize, prepare, commit, drain, install, uninstall, or shutdown.");
			}
			var drainMax=(int?)arguments["drain_max"] ?? DefaultDrainMax;
			if(operation==PayloadOperation.drain && (drainMax<1 || drainMax>MaxDrainMax))
				throw new RpcException("invalid_arguments","drain_max must be between 1 and "+MaxDrainMax.ToString(CultureInfo.InvariantCulture)+".");
			// The action composes its own evaluation and sets timeout_ms explicitly, so the public evaluate
			// tool's 1000 ms argument default never applies to it. The ceiling is the host's, not a budget:
			// durations are recorded as facts and nothing here is tuned against them.
			var timeout=(int?)arguments["evaluation_timeout_ms"] ?? dgSpy.Protocol.CapabilityCatalog.Limits.MaxEvaluationTimeoutMs;
			if(timeout<1 || timeout>dgSpy.Protocol.CapabilityCatalog.Limits.MaxEvaluationTimeoutMs)
				throw new RpcException("invalid_arguments","evaluation_timeout_ms must be between 1 and "+dgSpy.Protocol.CapabilityCatalog.Limits.MaxEvaluationTimeoutMs.ToString(CultureInfo.InvariantCulture)+".");
			var parameters=ParseParameters(arguments["payload_parameters"],operation);
			var patchId=(string?)arguments["patch_id"];
			if(operation==PayloadOperation.uninstall && String.IsNullOrWhiteSpace(patchId)) throw new RpcException("invalid_arguments","patch_id is required for payload_operation=uninstall.");
			if(operation!=PayloadOperation.uninstall && patchId is not null) throw new RpcException("invalid_arguments","patch_id applies to payload_operation=uninstall only.");
			return new PayloadActionRequest(operation,parameters,patchId,drainMax,timeout);
		}

		static List<KeyValuePair<string,string>> ParseParameters(JsonNode? node,PayloadOperation operation) {
			var parsed=new List<KeyValuePair<string,string>>();
			if(node is null) {
				if(operation==PayloadOperation.initialize || operation==PayloadOperation.prepare || operation==PayloadOperation.install) throw new RpcException("invalid_arguments","payload_parameters is required for payload_operation="+operation.ToString()+".");
				return parsed;
			}
			if(operation!=PayloadOperation.initialize && operation!=PayloadOperation.prepare && operation!=PayloadOperation.install)
				throw new RpcException("invalid_arguments","payload_parameters applies to payload_operation=initialize, prepare, or install only.");
			if(node is not JsonObject supplied) throw new RpcException("invalid_arguments","payload_parameters must be an object of string values.");
			var total=0;
			var seen=new HashSet<string>(StringComparer.Ordinal);
			foreach(var pair in supplied) {
				if(Array.IndexOf(KnownParameterKeys,pair.Key)<0)
					throw new RpcException("invalid_arguments","payload_parameters contains an unknown key: "+pair.Key+".");
				if(!seen.Add(pair.Key)) throw new RpcException("invalid_arguments","payload_parameters contains a duplicate key: "+pair.Key+".");
				var value=Scalar(pair.Value,pair.Key);
				if(value.Length==0) throw new RpcException("invalid_arguments","payload_parameters value for "+pair.Key+" is empty.");
				if(value.Length>MaxParameterValueLength) throw new RpcException("invalid_arguments","payload_parameters value for "+pair.Key+" exceeds "+MaxParameterValueLength.ToString(CultureInfo.InvariantCulture)+" characters.");
				foreach(var character in value)
					if(character<' ' || character==(char)0x7f || character=='"')
						throw new RpcException("invalid_arguments","payload_parameters value for "+pair.Key+" contains a character that may not appear in a bootstrap parameter.");
				total+=pair.Key.Length+value.Length+2;
				parsed.Add(new KeyValuePair<string,string>(pair.Key,value));
			}
			if(parsed.Count>MaxParameterLines) throw new RpcException("invalid_arguments","payload_parameters carries more than "+MaxParameterLines.ToString(CultureInfo.InvariantCulture)+" keys.");
			if(total>MaxParameterBytes) throw new RpcException("invalid_arguments","payload_parameters exceeds "+MaxParameterBytes.ToString(CultureInfo.InvariantCulture)+" characters.");
			// endpoint is required and never defaulted, exactly as the payload requires it: forgetting the
			// transport choice must fail rather than silently changing the probe's reachability.
			var endpoint=Value(parsed,"endpoint");
			if(endpoint is null) throw new RpcException("invalid_arguments","payload_parameters must set endpoint explicitly; it is never defaulted.");
			if(endpoint!="none" && endpoint!="pipe") throw new RpcException("invalid_arguments","payload_parameters endpoint must be exactly none or pipe.");
			if(Value(parsed,"endpoint_secret_base64") is not null && endpoint!="pipe")
				throw new RpcException("invalid_arguments","payload_parameters endpoint_secret_base64 requires endpoint=pipe.");
			if(Value(parsed,"completion_path") is null) throw new RpcException("invalid_arguments","payload_parameters must set completion_path: commit publishes its result to that file.");
			return parsed;
		}

		static string? Value(List<KeyValuePair<string,string>> parsed,string key) {
			foreach(var pair in parsed) if(String.Equals(pair.Key,key,StringComparison.Ordinal)) return pair.Value;
			return null;
		}

		static string Scalar(JsonNode? value,string key) {
			if(value is not JsonValue scalar) throw new RpcException("invalid_arguments","payload_parameters value for "+key+" must be a JSON string or number.");
			if(scalar.TryGetValue<string>(out var text)) return text;
			return scalar.ToJsonString();
		}

		/// <summary>
		/// The parameter block as the payload parses it, reconciled with the stop where - and only where -
		/// the two sides mean the same thing. Three identities wear two names here and getting them wrong is
		/// how an identity check ends up either fabricated or permanently failing:
		///
		/// <list type="bullet">
		/// <item><c>process_id</c> is the same number on both sides, so it is filled in when absent and
		/// refused when it disagrees. This is the one real cross-check.</item>
		/// <item><c>appdomain_id</c> is the CLR AppDomain id on both sides - measured in a live target as
		/// <c>1</c>, which is what dnSpy reports too - so it is filled in when absent. It is never refused on
		/// a mismatch: the extension has no RPC surface that reports an AppDomain id, so a caller cannot
		/// learn the value it would be judged against, and requiring it is exactly what made every earlier
		/// run answer <c>appdomain_unloaded</c>. dnSpy's <c>"default"</c> placeholder is not an id and is not
		/// filled in.</item>
		/// <item><c>runtime_id</c> is <b>not</b> derived from the stop, deliberately. The stop carries
		/// dnSpy's <c>DbgRuntime</c> identity, matched as a GUID or as a name such as
		/// <c>"CLR v4.0.30319"</c>, while the payload compares the target's own
		/// <c>RuntimeEnvironment.GetSystemVersion()</c>, measured as <c>"v4.0.30319"</c>. The name form is
		/// the only bridge and it is a substring relationship rather than an equality, so the caller supplies
		/// the value and this host does not guess at a transformation between two namespaces.</item>
		/// </list>
		/// </summary>
		public string Compose(int stopProcessId,string? stopAppDomainId) {
			var declaredProcess=Find("process_id");
			if(declaredProcess is not null) {
				if(!Int32.TryParse(declaredProcess,NumberStyles.Integer,CultureInfo.InvariantCulture,out var value) || value!=stopProcessId)
					throw new RpcException("invalid_arguments","payload_parameters process_id "+declaredProcess+" does not name the process the action stopped in ("+stopProcessId.ToString(CultureInfo.InvariantCulture)+").");
			}
			var builder=new StringBuilder();
			foreach(var pair in Parameters) { builder.Append(pair.Key); builder.Append('='); builder.Append(pair.Value); builder.Append('\n'); }
			if(declaredProcess is null) { builder.Append("process_id="); builder.Append(stopProcessId.ToString(CultureInfo.InvariantCulture)); builder.Append('\n'); }
			if(Find("appdomain_id") is null && !String.IsNullOrEmpty(stopAppDomainId) && stopAppDomainId!="default") {
				builder.Append("appdomain_id="); builder.Append(stopAppDomainId); builder.Append('\n');
			}
			return builder.ToString();
		}
	}

	/// <summary>
	/// One atomic action with four fixed operations - <c>prepare</c>, <c>commit</c>, <c>drain</c> and
	/// <c>shutdown</c> - that a
	/// host drives to install a probe into a live target and read its events.
	///
	/// <para><b>The resident-generation guard lives here, and it cannot live anywhere else.</b> Every
	/// <c>Assembly.Load(byte[])</c> creates a new, permanently unloadable generation - measured:
	/// <c>Assembly.Load(b)==Assembly.Load(b)</c> is <c>False</c>, and four loads produced four modules. A scan
	/// inside the bootstrap would have to load the assembly it exists to prevent, so the scan is an expression
	/// the host evaluates <b>before</b> any load, and a preparation that finds a generation refuses instead of
	/// creating a second one.</para>
	///
	/// <para><b>Verification reads the evaluation's own return value.</b> It does not re-evaluate and it does
	/// not look at the target after the resume: the state machine verifies while the target is still paused,
	/// and a post-resume check would either need another 110-130 ms stop or would be reading a target that had
	/// moved on. The report the payload returned at the stop is the evidence, and verification parses it out
	/// of the execution's own evidence rather than out of a field only this action can see.</para>
	///
	/// <para><b>Cleanup is <c>not_required</c>, and that is the truthful answer rather than a stub.</b> A
	/// residency commit cannot be rolled back - a net48 AppDomain has no mechanism to unload an assembly - so
	/// the cleanup reports that payloads are resident and names the operation that reconciles it.</para>
	/// </summary>
	public sealed class PayloadAction : IAtomicAction {
		/// <summary>What a caller runs to find out what this action left behind. It is <c>prepare</c> because
		/// preparation's first act is the host-side resident-generation scan, evaluated before any load: it
		/// reports exactly which generations are resident and refuses rather than adding another. There is no
		/// operation that removes a generation, and naming one that does not exist would be worse than naming
		/// the one that can only report.</summary>
		public const string ResidencyReconciliationOperation="start_atomic_action:payload_operation=prepare";

		readonly IPayloadEvaluator evaluator;
		readonly IPayloadSource payloads;
		readonly PayloadActionRequest request;
		bool payloadsResidentObserved;
		/// <summary>Set the moment the loading evaluation is issued, whatever it answers. A refusal that
		/// happened before that point installed nothing; one after it may have made the payload resident
		/// forever, and the two must never report the same thing.</summary>
		bool residencyMayBeCommitted;

		public PayloadAction(IPayloadEvaluator evaluator,IPayloadSource payloads,PayloadActionRequest request) {
			this.evaluator=evaluator ?? throw new ArgumentNullException(nameof(evaluator));
			this.payloads=payloads ?? throw new ArgumentNullException(nameof(payloads));
			this.request=request ?? throw new ArgumentNullException(nameof(request));
		}

		public string Kind=>"payload";
		public string? ReconciliationOperation=>ResidencyReconciliationOperation;

		public async Task<AtomicActionExecution> ExecuteAsync(AtomicActionContext context,CancellationToken cancellationToken) {
			if(context is null) throw new ArgumentNullException(nameof(context));
			var evidence=new JsonObject { ["payload_operation"]=request.Operation.ToString() };

			// The scan is first on every operation, and on prepare it is first *before any load* - which is
			// the whole point. It loads nothing itself, so a failed scan can honestly say so.
			var scanned=await evaluator.EvaluateAsync(context,PayloadExpressions.ResidentGenerationScan(),request.EvaluationTimeoutMs,cancellationToken).ConfigureAwait(false);
			evidence["generation_scan_expression"]=PayloadExpressions.ResidentGenerationScan();
			if(!scanned.Completed)
				return Refused(evidence,"The resident-generation scan did not evaluate, so a second permanently unloadable generation cannot be ruled out: "+(scanned.Error ?? "no error reported."));
			var scan=PayloadGenerationScan.Parse(scanned.Text);
			evidence["generation_scan_complete"]=scan.Complete;
			evidence["resident_generation_count"]=scan.GenerationCount;
			evidence["resident_generation_index"]=scan.FirstGenerationIndex;
			evidence["assembly_count"]=scan.AssemblyCount;
			if(!scan.Complete) return Refused(evidence,scan.Refusal!);
			payloadsResidentObserved=scan.GenerationCount>0;

			switch(request.Operation) {
				case PayloadOperation.prepare:
				case PayloadOperation.initialize: return await PrepareAsync(context,evidence,scan,cancellationToken).ConfigureAwait(false);
				default: return await ResidentAsync(context,evidence,scan,cancellationToken).ConfigureAwait(false);
			}
		}

		async Task<AtomicActionExecution> PrepareAsync(AtomicActionContext context,JsonObject evidence,PayloadGenerationScan scan,CancellationToken cancellationToken) {
			if(scan.GenerationCount!=0)
				return Refused(evidence,"A HookLab.Bootstrap generation is already resident in this target ("+scan.GenerationCount.ToString(CultureInfo.InvariantCulture)
					+" at index "+scan.FirstGenerationIndex.ToString(CultureInfo.InvariantCulture)+"). Preparing again would byte-load a second generation, and a byte-loaded generation can never be unloaded from a .NET Framework AppDomain. Nothing was loaded.");
			var parameters=request.Compose(context.Stop.ProcessId,context.Stop.AppDomainId);
			using var payload=payloads.Open();
			evidence["payload_path"]=payload.Path;
			evidence["payload_bytes"]=payload.Length;
			evidence["payload_sha256_before"]=payload.Sha256;
			var expression=request.Operation==PayloadOperation.initialize?PayloadExpressions.PrepareAndCommit(payload.Path,parameters):PayloadExpressions.Prepare(payload.Path,parameters);
			evidence["expression"]=expression;
			// From here on the load may have happened, whatever comes back.
			residencyMayBeCommitted=true;
			payloadsResidentObserved=true;
			var evaluated=await evaluator.EvaluateAsync(context,expression,request.EvaluationTimeoutMs,cancellationToken).ConfigureAwait(false);
			// A compiler error is the one answer that proves nothing ran, so it is the one answer allowed to
			// take the residency claim back.
			if(evaluated.CompilerError==true) residencyMayBeCommitted=false;
			var after=payload.RehashFromHandle();
			evidence["payload_sha256_after"]=after;
			var completed=Record(evidence,evaluated);
			if(completed && !String.Equals(after,payload.Sha256,StringComparison.OrdinalIgnoreCase))
				return new AtomicActionExecution { Completed=false,MayHaveExecuted=true,Evidence=evidence.ToJsonString(),MutationAuditId=evaluated.AuditId,
					Error="The payload digest changed while it was being delivered: "+payload.Sha256+" before, "+after+" after." };
			return new AtomicActionExecution { Completed=completed,MayHaveExecuted=residencyMayBeCommitted,Evidence=evidence.ToJsonString(),MutationAuditId=evaluated.AuditId,Error=evaluated.Error };
		}

		async Task<AtomicActionExecution> ResidentAsync(AtomicActionContext context,JsonObject evidence,PayloadGenerationScan scan,CancellationToken cancellationToken) {
			if(scan.GenerationCount==0)
				return Refused(evidence,"No HookLab.Bootstrap generation is resident in this target, so there is nothing to "+request.Operation.ToString()+". Run payload_operation=prepare first.");
			if(scan.GenerationCount>1)
				return Refused(evidence,"This target carries "+scan.GenerationCount.ToString(CultureInfo.InvariantCulture)+" HookLab.Bootstrap generations, so the index this operation would reach is ambiguous. Refusing rather than picking one.");
			string expression;
			switch(request.Operation) {
				case PayloadOperation.commit: expression=PayloadExpressions.Commit(scan.FirstGenerationIndex); break;
				case PayloadOperation.shutdown: expression=PayloadExpressions.Shutdown(scan.FirstGenerationIndex); break;
				case PayloadOperation.install: expression=PayloadExpressions.Install(scan.FirstGenerationIndex,request.Compose(context.Stop.ProcessId,context.Stop.AppDomainId)); break;
				case PayloadOperation.uninstall: expression=PayloadExpressions.Uninstall(scan.FirstGenerationIndex,request.PatchId!); break;
				default: expression=PayloadExpressions.Drain(scan.FirstGenerationIndex,request.DrainMax); break;
			}
			evidence["expression"]=expression;
			var evaluated=await evaluator.EvaluateAsync(context,expression,request.EvaluationTimeoutMs,cancellationToken).ConfigureAwait(false);
			var completed=Record(evidence,evaluated);
			// The generation was already resident before this ran, so a commit, drain, or shutdown adds no residency of
			// its own; what it may have done is start the worker, which is behaviour, not residency.
			return new AtomicActionExecution { Completed=completed,MayHaveExecuted=evaluated.CompilerError!=true,Evidence=evidence.ToJsonString(),MutationAuditId=evaluated.AuditId,Error=evaluated.Error };
		}

		/// <summary>Copies the evaluation's own return value into the evidence, which is where verification
		/// reads it from. Nothing else is allowed to become the subject of verification.</summary>
		static bool Record(JsonObject evidence,PayloadEvaluation evaluated) {
			evidence["report"]=evaluated.Text;
			evidence["evaluation_completed"]=evaluated.Completed;
			if(evaluated.Error is not null) evidence["evaluation_error"]=evaluated.Error;
			if(evaluated.CompilerError is not null) evidence["compiler_error"]=evaluated.CompilerError.Value;
			return evaluated.Completed;
		}

		AtomicActionExecution Refused(JsonObject evidence,string reason) {
			evidence["refusal"]=reason;
			return new AtomicActionExecution { Completed=false,MayHaveExecuted=residencyMayBeCommitted,Evidence=evidence.ToJsonString(),Error=reason };
		}

		/// <summary>Verifies from the report the evaluation returned at the stop. It issues no evaluation of
		/// its own: the target is still paused here, and a check that needed the target to have run would
		/// either cost another stop or be reading state the resume had already moved past.</summary>
		public Task<AtomicActionVerification> VerifyAsync(AtomicActionContext context,AtomicActionExecution execution,CancellationToken cancellationToken) {
			if(execution is null) throw new ArgumentNullException(nameof(execution));
			var evidence=execution.Evidence is null ? null : JsonNode.Parse(execution.Evidence) as JsonObject;
			var text=evidence is null ? null : (string?)evidence["report"];
			if(String.IsNullOrEmpty(text))
				return Task.FromResult(new AtomicActionVerification { Verified=false,Evidence=execution.Evidence,Error="The evaluation returned no payload report, so there is nothing that says whether the payload accepted the request." });
			var report=PayloadReport.Parse(text);
			var error=Disagreement(report);
			return Task.FromResult(new AtomicActionVerification { Verified=error is null,Evidence=execution.Evidence,Error=error });
		}

		string? Disagreement(PayloadReport report) {
			if(report.Status!="ok") return "The payload report is not ok: status="+(report.Status ?? "absent")+(report.Value("error_type") is string type ? ", error_type="+type : "")+(report.Value("error_message") is string message ? ", error_message="+message : "");
			// Every operation's report has to distinguish the two commit boundaries. A report that does not
			// carry them is not a report this host knows how to be truthful about.
			if(!report.Has("residency_commit")) return "The payload report does not say whether the residency commit happened, so nothing can be claimed about what is resident.";
			if(!report.Has("behavior_commit")) return "The payload report does not distinguish behavior_commit from residency_commit.";
			if(!report.Has("prototype_compromises")) return "The payload report does not carry prototype_compromises, so the compromises this prototype ships with would not travel with the answer.";
			switch(request.Operation) {
				case PayloadOperation.prepare:
					if(report.Value("generation_identity")!=PayloadExpressions.GenerationIdentity)
						return "The prepared generation reported identity '"+(report.Value("generation_identity") ?? "absent")+"' rather than '"+PayloadExpressions.GenerationIdentity+"', so the host cannot tell which generation it is holding.";
					if(report.Value("residency_commit")!="completed") return "Preparation reported residency_commit="+report.Value("residency_commit")+"; a successful preparation makes the payload resident and must say so.";
					if(report.Value("payloads_resident")!="true") return "Preparation reported payloads_resident="+(report.Value("payloads_resident") ?? "absent")+"; preparation byte-loads the payload and cannot claim otherwise.";
					return null;
				case PayloadOperation.initialize:
				case PayloadOperation.commit:
					if(report.Value("residency_commit")!="completed") return "Commit reported residency_commit="+report.Value("residency_commit")+", which means it did not reach a prepared generation.";
					if(!report.Has("worker_started")) return "Commit did not report worker_started, so a first commit cannot be told from a repeat of one.";
					return null;
				case PayloadOperation.shutdown:
					if(report.Value("payloads_resident")!="true") return "Shutdown reported payloads_resident="+(report.Value("payloads_resident") ?? "absent")+"; byte-loaded payloads cannot be unloaded.";
					if(report.Value("behavior_commit")!="stopped") return "Shutdown reported behavior_commit="+(report.Value("behavior_commit") ?? "absent")+" rather than stopped.";
					return null;
				case PayloadOperation.install:
				case PayloadOperation.uninstall:
					if(!report.Has("patch_id")) return "The hook operation did not report a patch_id.";
					if(!report.Has("hooks_version")) return "The hook operation did not report hooks_version.";
					if(!report.Has("changed")) return "The hook operation did not report whether state changed.";
					return null;
				default:
					// dropped is what makes a drain truthful: a count without it cannot distinguish "no events"
					// from "events that were thrown away".
					if(!report.Has("count")) return "The drain report carries no count.";
					if(!report.Has("dropped")) return "The drain report carries no dropped count, so the events it returned cannot be told from the events it lost.";
					return null;
			}
		}

		/// <summary>
		/// There is nothing to undo, and saying so is the truthful answer rather than a stub.
		///
		/// <para>A residency commit is unundoable by construction: a .NET Framework AppDomain cannot unload an
		/// assembly, so what preparation byte-loaded stays for the life of the process. Reporting
		/// <c>completed</c> would claim a rollback that did not happen; reporting <c>failed</c> would claim a
		/// rollback that was attempted. The cleanup evidence therefore states what is resident and names the
		/// operation that reconciles it, and it does that on every path - the state machine only surfaces
		/// <c>reconciliation_operation</c> when it independently decides reconciliation is needed, so a
		/// successful preparation would otherwise leave its permanent residue unnamed.</para>
		/// </summary>
		public Task<AtomicActionCleanup> CleanupAsync(AtomicActionCleanupContext context,CancellationToken cancellationToken) {
			var resident=payloadsResidentObserved || residencyMayBeCommitted;
			var evidence="payload_operation="+request.Operation.ToString()
				+" payloads_resident="+(resident ? "true" : "false")
				+" residency_rollback=not_possible"
				+" reconciliation_operation="+ResidencyReconciliationOperation
				+(request.CompletionPath is null ? "" : " completion_path="+request.CompletionPath)
				+" detail="+(resident
					? "A byte-loaded assembly cannot be unloaded from a .NET Framework AppDomain, so this action installed nothing it is able to remove."
					: "This action refused before issuing any load, so it made nothing resident.");
			return Task.FromResult(new AtomicActionCleanup {
				Outcome=CleanupOutcome.not_required,
				Evidence=evidence,
				ReconciliationOperation=ResidencyReconciliationOperation,
			});
		}
	}
}
