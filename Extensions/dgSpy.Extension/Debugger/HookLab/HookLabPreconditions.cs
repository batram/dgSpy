using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace dgSpy.Extension {
	/// <summary>The three results a precondition may carry, and there is no fourth.
	///
	/// <para>This repeats <c>HookLab.Injector.PreconditionResult</c> deliberately and cannot reference it:
	/// the injector targets net48 and net10-windows, and this contract is kept BCL-only so it can be
	/// exercised directly by tests rather than only through a class no test can construct. The acting
	/// path maps between them in exactly one place, with a switch that throws on an unrecognized value so
	/// a divergence is loud rather than silent.</para></summary>
	enum PreconditionOutcome { Satisfied, Failed, NotProvablePreflight }

	/// <summary>What HookLab initialization requires of a target, evaluated once and answered by name.
	///
	/// <para>The incident this exists for produced five distinct defects that all surfaced as one
	/// undifferentiated failure at the last step, each hiding the next: a missing CRT dependency and an
	/// unreadable staging directory both reported <c>did not load its runtime component</c>, and nothing
	/// could reveal the application-domain defect until the four before it were fixed. A contract that is
	/// evaluated up front converts that serial hunt into one answer.</para>
	///
	/// <para>Three results and no fourth. <see cref="PreconditionOutcome.Satisfied"/> means proved before
	/// the attempt; <see cref="PreconditionOutcome.Failed"/> means disproved before the attempt, and is a
	/// refusal; <see cref="PreconditionOutcome.NotProvablePreflight"/> means the question cannot honestly
	/// be decided without attempting the operation. A precondition that could not be evaluated is never
	/// reported as satisfied - that silent degradation is the failure mode this whole road exists to
	/// remove.</para>
	///
	/// <para><b>This never reports "loadable".</b> Whether Windows can map an artifact into a target
	/// depends on KnownDLLs, API-set forwarding, the target's DLL search configuration, activation
	/// context, already-loaded modules, transitive imports, token access and code-integrity policy.
	/// dgSpy does not model that and must not claim to. At its strongest this reports
	/// <c>no known incompatibility</c>, in those words, and the target-side loader stays authoritative
	/// for load failure.</para>
	///
	/// <para>The computation is pure: callers gather the facts, and both the acting path and the
	/// read-only probe evaluate the same function over them. A preflight that is a second implementation
	/// drifts from the path it is supposed to gate, which would be a worse defect than the one it
	/// prevents.</para></summary>
	static class HookLabPreconditions {
		/// <summary>The exact wording the road demands, kept in one place so it cannot soften into a
		/// promise by paraphrase.</summary>
		public const string StrongestClaim="no known incompatibility";

		public const string IdentityTargetReadable="identity.target_readable";
		public const string ArchitectureSupported="target.architecture_supported";
		public const string RuntimeSupported="target.runtime_supported";
		public const string ApplicationDomain="target.application_domain";
		public const string ExchangeCreationAuthorized="exchange.creation_authorized";
		public const string ExchangeTargetEffectiveAccess="exchange.target_effective_access";
		public const string PayloadReadableByTarget="payload.readable_by_target";
		public const string PayloadCompatibility="payload_compatibility";
		public const string EndpointControllerReachable="endpoint.controller_reachable";

		/// <summary>Facts gathered from the target and the payload, with no decision taken. Every field is
		/// something the caller already had to read on the way to initializing, which is what keeps the
		/// probe and the acting path evaluating one contract over one set of inputs.</summary>
		public sealed class Facts {
			public int ProcessId { get; set; }
			public int Bitness { get; set; }
			public string Architecture { get; set; }="";
			public IReadOnlyList<HookLabRuntimeIdentity> Runtimes { get; set; }=Array.Empty<HookLabRuntimeIdentity>();
			/// <summary>Every application domain the process exposes, as id and friendly name.</summary>
			public IReadOnlyList<(int Id,string Name)> ApplicationDomains { get; set; }=Array.Empty<(int,string)>();
			/// <summary>The domain the caller asked for, or null when none was named.</summary>
			public int? RequestedApplicationDomain { get; set; }
			/// <summary>False when the target's SID could not be read at all. An unknown target identity is
			/// not "the same as ours"; assuming it was is what staged a payload where the target could not
			/// read it.</summary>
			public bool TargetIdentityKnown { get; set; }
			public bool CrossIdentity { get; set; }
			public string? IdentityFailureDetail { get; set; }
			/// <summary>The prospective exchange area's verdict. Planning creates nothing, which is what
			/// lets the probe answer these two rows without touching the filesystem.</summary>
			public bool ExchangePlanned { get; set; }
			public PreconditionOutcome ExchangeCreation { get; set; }=PreconditionOutcome.NotProvablePreflight;
			public string ExchangeCreationDetail { get; set; }="";
			/// <summary>True when the payload was opened and verified; false with a detail when it was
			/// refused. Opening is a read of shipped product, never a mutation.</summary>
			public bool PayloadVerified { get; set; }
			public string? PayloadFailureDetail { get; set; }
		}

		public sealed class Precondition {
			public Precondition(string name,PreconditionOutcome result,string detail,string? refusalCode=null,bool refusesWhenUnprovable=false) {
				Name=name; Result=result; Detail=detail; RefusalCode=refusalCode; RefusesWhenUnprovable=refusesWhenUnprovable;
			}

			/// <summary>Whether an undecidable answer to this precondition is itself a refusal.
			///
			/// <para>Every row answers this explicitly, including the rows that say no, because "we could
			/// not tell, so we continued" is a policy and not an absence of one. Today no row refuses on an
			/// unprovable result: each of the three is decided by the attempt itself and reported with a
			/// named failure when it goes wrong - the target reads the payload or the loader says why, the
			/// controller opens the endpoint or the round trip fails. Refusing on them would trade a
			/// diagnosable failure for a guess that no target ever works.</para></summary>
			public bool RefusesWhenUnprovable { get; }
			public string Name { get; }
			public PreconditionOutcome Result { get; }
			public string Detail { get; }
			/// <summary>The error code the acting path refuses with, so that the refusal a caller sees from
			/// <c>initialize_hooklab</c> and the row the probe reports are the same fact under one name.</summary>
			public string? RefusalCode { get; }
		}

		public sealed class Report {
			public Report(IReadOnlyList<Precondition> preconditions) { Preconditions=preconditions; }
			public IReadOnlyList<Precondition> Preconditions { get; }
			public Precondition? FirstFailure => Preconditions.FirstOrDefault(value=>
				value.Result==PreconditionOutcome.Failed ||
				(value.Result==PreconditionOutcome.NotProvablePreflight && value.RefusesWhenUnprovable));
			public bool Refuses => FirstFailure is not null;
			public IReadOnlyList<Precondition> NotProvable => Preconditions.Where(value=>value.Result==PreconditionOutcome.NotProvablePreflight).ToArray();
			/// <summary>Never "ready" and never "loadable". Either a named failure, or the bounded claim.</summary>
			public string Summary => FirstFailure is Precondition failure
				?"Refused by "+failure.Name+": "+failure.Detail
				:StrongestClaim+"; "+NotProvable.Count.ToString(CultureInfo.InvariantCulture)+" of "+
					Preconditions.Count.ToString(CultureInfo.InvariantCulture)+" preconditions cannot be decided without attempting the operation.";
		}

		public static Report Evaluate(Facts facts) {
			if(facts is null) throw new ArgumentNullException(nameof(facts));
			var preconditions=new List<Precondition> {
				Identity(facts),
				Architecture(facts),
				Runtime(facts),
				ApplicationDomainOf(facts),
				ExchangeCreation(facts),
				ExchangeAccess(facts),
				Payload(facts),
				Compatibility(facts),
				Endpoint(),
			};
			return new Report(preconditions);
		}

		static Precondition Identity(Facts facts) =>
			facts.TargetIdentityKnown
				?new Precondition(IdentityTargetReadable,PreconditionOutcome.Satisfied,
					facts.CrossIdentity
						?"The controller and the target are different principals, and both SIDs were read."
						:"The controller and the target are the same principal.")
				:new Precondition(IdentityTargetReadable,PreconditionOutcome.Failed,
					facts.IdentityFailureDetail ?? "The target process identity could not be read.","hooklab_identity_unavailable");

		static Precondition Architecture(Facts facts) =>
			facts.Bitness==64 && String.Equals(facts.Architecture,"X64",StringComparison.OrdinalIgnoreCase)
				?new Precondition(ArchitectureSupported,PreconditionOutcome.Satisfied,"The target is x64.")
				:new Precondition(ArchitectureSupported,PreconditionOutcome.Failed,
					"HookLab currently supports x64 targets; this process is "+facts.Architecture+" ("+
					facts.Bitness.ToString(CultureInfo.InvariantCulture)+"-bit).","unsupported_hooklab_target");

		static Precondition Runtime(Facts facts) {
			// Deliberately answered independently of architecture: reporting only the first failure would
			// rebuild the serial hunt this contract exists to replace.
			var backend=HookLabTargetEligibility.SelectBackend(64,"X64",facts.Runtimes);
			if(backend is not null) return new Precondition(RuntimeSupported,PreconditionOutcome.Satisfied,"The target has loaded "+backend.Value+".");
			var names=facts.Runtimes.Select(runtime=>String.IsNullOrWhiteSpace(runtime.Name)?runtime.Guid.ToString("D"):runtime.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			return new Precondition(RuntimeSupported,PreconditionOutcome.Failed,
				"HookLab supports desktop CLR v4 and CoreCLR; the target exposes "+
				(names.Length==0?"no managed runtime":String.Join(", ",names))+".","unsupported_hooklab_target");
		}

		static Precondition ApplicationDomainOf(Facts facts) {
			var domains=facts.ApplicationDomains;
			var describe=String.Join(", ",domains.Select(domain=>domain.Id.ToString(CultureInfo.InvariantCulture)+"="+domain.Name));
			if(facts.RequestedApplicationDomain is null)
				return domains.Count<=1
					?new Precondition(ApplicationDomain,PreconditionOutcome.Satisfied,"The process runs code in one application domain.")
					:new Precondition(ApplicationDomain,PreconditionOutcome.Failed,
						"This process runs code in "+domains.Count.ToString(CultureInfo.InvariantCulture)+" application domains, so HookLab will not guess which one to enter. "+
						"Pass app_domain_id naming the domain that holds the code you intend to hook; list_modules reports it per module. Domains: "+describe,
						"hooklab_application_domain_required");
			var selected=domains.Where(domain=>domain.Id==facts.RequestedApplicationDomain.Value).ToArray();
			if(selected.Length==0)
				return new Precondition(ApplicationDomain,PreconditionOutcome.Failed,
					"No application domain with id "+facts.RequestedApplicationDomain.Value.ToString(CultureInfo.InvariantCulture)+" is loaded in this process. Domains: "+describe,
					"hooklab_application_domain_not_found");
			// The resident selects its domain by friendly name, because the COM _AppDomain interface has
			// no get_Id, so two domains sharing a name is a coin flip and therefore a refusal.
			if(domains.Count(domain=>String.Equals(domain.Name,selected[0].Name,StringComparison.Ordinal))>1)
				return new Precondition(ApplicationDomain,PreconditionOutcome.Failed,
					"More than one application domain in this process is named '"+selected[0].Name+"', and the resident selects its domain by name. Domains: "+describe,
					"hooklab_application_domain_ambiguous");
			return new Precondition(ApplicationDomain,PreconditionOutcome.Satisfied,
				"Application domain "+selected[0].Id.ToString(CultureInfo.InvariantCulture)+" ('"+selected[0].Name+"') was named and is loaded.");
		}

		static Precondition ExchangeCreation(Facts facts) =>
			!facts.ExchangePlanned
				?new Precondition(ExchangeCreationAuthorized,PreconditionOutcome.NotProvablePreflight,
					"No exchange area could be planned, because the identity pair is unknown.")
				:new Precondition(ExchangeCreationAuthorized,facts.ExchangeCreation,facts.ExchangeCreationDetail,
					facts.ExchangeCreation==PreconditionOutcome.Failed?"hooklab_exchange_unavailable":null);

		static Precondition ExchangeAccess(Facts facts) =>
			!facts.ExchangePlanned
				?new Precondition(ExchangeTargetEffectiveAccess,PreconditionOutcome.NotProvablePreflight,
					"No exchange area could be planned, because the identity pair is unknown.")
				:!facts.CrossIdentity
					?new Precondition(ExchangeTargetEffectiveAccess,PreconditionOutcome.Satisfied,
						"The controller and the target are the same principal, so the target's access is the controller's own.")
					// SID equality does not establish effective access, and neither does reading back a DACL
					// we just wrote: restricted SIDs, deny-only groups, deny ACEs, integrity level and
					// impersonation state all sit between an authored grant and a usable one.
					:new Precondition(ExchangeTargetEffectiveAccess,PreconditionOutcome.NotProvablePreflight,
						"Whether the target's effective token can use a directory granted to its SID is decided by that token, not by the grant. "+
						"It is proved by the target reading the payload, which is part of the attempt.");

		static Precondition Payload(Facts facts) =>
			facts.PayloadVerified
				?new Precondition(PayloadReadableByTarget,PreconditionOutcome.Satisfied,
					"The shipped payload was opened and verified against its manifest and the independent package record.")
				:new Precondition(PayloadReadableByTarget,PreconditionOutcome.Failed,
					facts.PayloadFailureDetail ?? "The shipped HookLab payload could not be verified.","hooklab_payload_unavailable");

		/// <summary>Never a promise that the artifact loads. The dependency surface is asserted at build
		/// time by the injected-import policy, and the target-side loader remains authoritative for what
		/// actually happens when the bytes are mapped.</summary>
		static Precondition Compatibility(Facts facts) =>
			!facts.PayloadVerified
				?new Precondition(PayloadCompatibility,PreconditionOutcome.NotProvablePreflight,
					"The payload was not verified, so its compatibility was not examined.")
				:new Precondition(PayloadCompatibility,PreconditionOutcome.NotProvablePreflight,
					"The injected import surface is asserted at build time against a reviewed allowlist, which is "+StrongestClaim+
					" rather than a promise that the target's loader will map it. The target-side loader decides, and reports the Win32 code when it does not.");

		static Precondition Endpoint() =>
			new Precondition(EndpointControllerReachable,PreconditionOutcome.NotProvablePreflight,
				"Authoring a DACL does not establish that the controller's effective token can open the endpoint. Connectivity is proved by an authenticated round trip, which is part of the attempt.");
	}
}
