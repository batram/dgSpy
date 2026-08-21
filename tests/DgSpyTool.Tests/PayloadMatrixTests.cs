using Xunit;
using HookLab.Bootstrap;

/// <summary>
/// The resident payload matrix is the build's own account of what dgSpy injects into a target: which
/// assemblies, for which runtime family, from which project or package, at which identity and digest.
///
/// A generated manifest that nothing disagrees with proves nothing, so these tests do two separate
/// things. The parser tests reject matrices that are internally wrong - an unresolvable dependency, a
/// framework that cannot load on the runtime it claims, a duplicated slot. The byte tests take the real
/// shipped payload, state a matrix that differs from the one inside it, and require verification to
/// notice: a wrong assembly version, an empty slot, and - the failure that motivated all of this - a
/// resident DLL that ships without being declared anywhere.
/// </summary>
public sealed class PayloadMatrixTests {
	// A minimal well-formed matrix. Every negative case below is this text with one thing changed, so the
	// thing changed is unambiguously what the rejection is about.
	const string Digest="0000000000000000000000000000000000000000000000000000000000000000";
	static readonly string[] Rows={
		// The seventh field is the fallback set: the subset of the runtimes field on which the slot defers
		// to whatever the runtime already supplies. Empty on everything here except the Facade row, which
		// exists so the rules about it are exercised against a matrix that also has a legal use of it.
		"Contracts|contracts|bootstrap|P.Contracts.dll|netstandard2.0|any|clrv4,coreclr,mono||Contracts|1.0.0.0|none|project:a||"+Digest,
		"Resident|resident|bootstrap|P.Resident.dll|net48|x64|clrv4,coreclr,mono||Resident|1.0.0.0|none|project:b|Contracts,Compiler|"+Digest,
		"Compiler|compiler|bootstrap|P.Compiler.dll|netstandard2.0|any|clrv4,coreclr,mono||Compiler|5.6.0.0|31bf3856ad364e35|nuget:c/5.6.0||"+Digest,
		"Facade|compiler-support|bootstrap|P.Facade.dll|net462|any|clrv4,mono|mono|Facade|4.0.5.0|cc7b13ffcd2ddd51|nuget:e/4.6.3||"+Digest,
		"Engine.Desktop|patch-engine|probe|Q.Desktop.dll|net48|any|clrv4,mono||0Harmony|2.4.2.0|none|nuget:d/2.4.2||"+Digest,
		"Engine.CoreClr|patch-engine|probe|Q.CoreClr.dll|net6.0|any|coreclr||0Harmony|2.4.2.0|none|nuget:d/2.4.2||"+Digest,
	};

	static string Matrix(params (string Find,string Replace)[] edits) {
		var rows=Rows.Select(row=>{
			foreach(var edit in edits) if(row.Contains(edit.Find,StringComparison.Ordinal)) row=row.Replace(edit.Find,edit.Replace,StringComparison.Ordinal);
			return row;
		}).Where(row=>row.Length!=0);
		return String.Join(Environment.NewLine,new[]{PayloadMatrix.Header}.Concat(rows));
	}

	[Fact]
	public void The_example_matrix_is_accepted_so_the_rejections_below_are_about_what_they_change() {
		var matrix=PayloadMatrix.Parse(Matrix());
		Assert.Equal(6,matrix.Entries.Count);
		Assert.Equal("Resident",matrix.Entries.Single(entry=>entry.Role==PayloadRole.Resident).Id);
		// The fallback set is a subset of the runtime set, not a restatement of it: the Facade row is valid
		// on CLR v4 and Mono, and defers only on Mono.
		Assert.Equal(PayloadRuntimes.Mono,matrix["Facade"].Fallback);
		Assert.Equal(PayloadRuntimes.ClrV4|PayloadRuntimes.Mono,matrix["Facade"].Runtimes);
		Assert.Equal(PayloadRuntimes.None,matrix["Compiler"].Fallback);
	}

	[Fact]
	public void A_fallback_on_a_family_the_payload_does_not_claim_is_refused() {
		// Otherwise a slot could describe deferral behaviour on a runtime that never loads it at all,
		// which reads as coverage and is not.
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix(("clrv4,mono|mono|Facade","clrv4,mono|coreclr,mono|Facade"))));
		Assert.Contains("cannot be a fallback on a runtime family it does not claim",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void Only_a_compiler_support_payload_may_defer_to_the_runtime() {
		// The security boundary, asserted rather than described: dgSpy's own contracts, resident, compiler
		// and patch engine are exactly the assemblies whose provenance the bootstrap exists to guarantee,
		// and none of them may be satisfied by something already present in the target.
		foreach(var role in new[]{"contracts","resident","compiler","patch-engine"}) {
			var error=Assert.Throws<BootstrapIntegrityException>(()=>
				PayloadMatrix.Parse(Matrix(("|compiler-support|bootstrap|P.Facade.dll","|"+role+"|bootstrap|P.Facade.dll"))));
			Assert.Contains("Only a compiler-support payload may defer",error.Message,StringComparison.Ordinal);
		}
	}

	[Fact]
	public void A_dependency_that_is_not_a_slot_is_an_incomplete_closure() {
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix(("Contracts,Compiler","Contracts,Compiler,Roslyn"))));
		Assert.Contains("Incomplete dependency closure",error.Message,StringComparison.Ordinal);
		Assert.Contains("Roslyn",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_modern_framework_asset_may_not_claim_clr_v4() {
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix(("net6.0|any|coreclr","net6.0|any|clrv4,coreclr"))));
		Assert.Contains("Framework-incompatible",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_runtime_family_left_without_a_patch_engine_is_refused() {
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix(("Engine.CoreClr|patch-engine","Engine.CoreClr|compiler-support"))));
		Assert.Contains("no patch engine payload for CoreCLR",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_duplicated_slot_is_refused() {
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix()+Environment.NewLine+Rows[0]));
		Assert.Contains("Duplicate payload matrix id",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_malformed_row_or_digest_is_refused() {
		Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(PayloadMatrix.Header+Environment.NewLine+"too|few|fields"));
		Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix((Digest,"not-a-digest"))));
		Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse("hooklab-payload-matrix|1"+Environment.NewLine+Rows[0]));
	}

	[Fact]
	public void The_shipped_payload_declares_exactly_what_it_carries() {
		var matrix=PayloadMatrixVerification.VerifyPayloadFile(Payload());
		Assert.Equal(new[]{"HookLab.Contracts","HookLab.Probe.CorDebug","Microsoft.CodeAnalysis","Microsoft.CodeAnalysis.CSharp","System.Collections.Immutable","System.Runtime.CompilerServices.Unsafe","System.Memory","System.Buffers","System.Numerics.Vectors","System.Threading.Tasks.Extensions","System.Text.Encoding.CodePages","System.Reflection.Metadata","Harmony.Desktop","Harmony.CoreClr"},
			matrix.Entries.Select(entry=>entry.Id).ToArray());
		// The runtime axis the matrix exists to make explicit: one pinned patch engine per family, and
		// neither of them visible anywhere before this manifest described them.
		Assert.Equal("net48",matrix["Harmony.Desktop"].TargetFramework);
		Assert.Equal(PayloadRuntimes.ClrV4|PayloadRuntimes.Mono,matrix["Harmony.Desktop"].Runtimes);
		Assert.Equal("net6.0",matrix["Harmony.CoreClr"].TargetFramework);
		Assert.Equal(PayloadRuntimes.CoreClr,matrix["Harmony.CoreClr"].Runtimes);
		Assert.All(matrix.Entries,entry=>Assert.Equal(64,entry.Sha256.Length));
		// The fallback axis, in the shipped matrix: exactly the facades whose presence differs between Mono
		// builds, deferring on Mono only. Naming them here means widening the set - or quietly making one
		// of dgSpy's own payloads deferrable - has to be a deliberate edit to this list.
		//
		// The last three were added when a *shipped* Unity player turned out to carry no Facades directory
		// at all, which the editor profile does; every Mono build before that had supplied them.
		Assert.Equal(new[]{"System.Memory","System.Buffers","System.Numerics.Vectors","System.Threading.Tasks.Extensions","System.Text.Encoding.CodePages"},
			matrix.Entries.Where(entry=>entry.Fallback!=PayloadRuntimes.None).Select(entry=>entry.Id).ToArray());
		Assert.All(matrix.Entries.Where(entry=>entry.Fallback!=PayloadRuntimes.None),entry=>{
			Assert.Equal(PayloadRuntimes.Mono,entry.Fallback);
			Assert.Equal(PayloadRole.CompilerSupport,entry.Role);
		});
	}

	[Fact]
	public void A_declared_identity_that_the_bytes_do_not_carry_is_refused() {
		var image=File.ReadAllBytes(Payload());
		var text=PayloadMatrixVerification.ReadMatrixText(image,"payload")
			.Replace("|Microsoft.CodeAnalysis|5.6.0.0|","|Microsoft.CodeAnalysis|5.6.0.1|",StringComparison.Ordinal);
		var error=Assert.Throws<InvalidOperationException>(()=>PayloadMatrixVerification.Verify(PayloadMatrix.Parse(text),image,"payload"));
		Assert.Contains("wrong identity",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_declared_slot_with_nothing_behind_it_is_refused() {
		var image=File.ReadAllBytes(Payload());
		var text=PayloadMatrixVerification.ReadMatrixText(image,"payload")
			.Replace("HookLab.Bootstrap.Payloads.HookLab.Contracts.dll","HookLab.Bootstrap.Payloads.Absent.dll",StringComparison.Ordinal);
		var error=Assert.Throws<InvalidOperationException>(()=>PayloadMatrixVerification.Verify(PayloadMatrix.Parse(text),image,"payload"));
		Assert.Contains("is empty",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void A_payload_the_matrix_does_not_declare_is_refused() {
		var image=File.ReadAllBytes(Payload());
		var lines=PayloadMatrixVerification.ReadMatrixText(image,"payload")
			.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)
			.Where(line=>!line.StartsWith("System.Collections.Immutable|",StringComparison.Ordinal))
			.Select(line=>line.Replace("System.Collections.Immutable","",StringComparison.Ordinal).Replace(",|","|",StringComparison.Ordinal));
		var error=Assert.Throws<InvalidOperationException>(()=>PayloadMatrixVerification.Verify(PayloadMatrix.Parse(String.Join(Environment.NewLine,lines)),image,"payload"));
		Assert.Contains("Unmanifested payload",error.Message,StringComparison.Ordinal);
		Assert.Contains("System.Collections.Immutable.dll",error.Message,StringComparison.Ordinal);
	}

	/// <summary>The property a stripped Unity player needs, asserted against the bytes that ship.
	///
	/// <para>A player's Managed directory has nine non-Unity assemblies and no Facades, so
	/// <c>netstandard, Version=2.0.0.0</c> is not there - and it cannot be supplied, being strong-named to
	/// Microsoft's key. A payload naming it is a payload a supported target refuses to load, which is what
	/// the build's retarget step removes. Read from the shipped image rather than from the matrix, because
	/// the matrix records what the build believed.</para></summary>
	[Fact]
	public void No_payload_a_player_must_load_references_the_netstandard_facade() {
		var image=File.ReadAllBytes(Payload());
		var matrix=PayloadMatrixVerification.VerifyPayloadFile(Payload());
		var bootstrapResources=PayloadMatrixVerification.ReadEmbeddedResources(image,"payload");
		foreach(var entry in matrix.Carried(PayloadCarrier.Bootstrap)) {
			var references=PayloadMatrixVerification.ReadAssemblyReferences(bootstrapResources[entry.ResourceName]);
			Assert.DoesNotContain("netstandard",references);
			// The retarget is only meaningful if it pointed somewhere: the three rewritten payloads must
			// name the corlib a player actually ships.
			if(entry.Provenance.StartsWith("build:",StringComparison.Ordinal)) Assert.Contains("mscorlib",references);
		}
	}

	[Fact]
	public void A_build_derived_payload_must_name_its_pinned_inputs() {
		// "build:" describes bytes dgSpy produced. Allowing it to stand alone would make it a way to
		// describe bytes of no stated origin at all, which is the one thing provenance is for.
		var error=Assert.Throws<BootstrapIntegrityException>(()=>PayloadMatrix.Parse(Matrix(("|nuget:c/5.6.0|","|build:SomeTransform|"))));
		Assert.Contains("must name its pinned inputs",error.Message,StringComparison.Ordinal);
		// And the well-formed spelling is accepted, so the rejection above is about the missing input.
		Assert.Equal("build:SomeTransform(nuget:c/5.6.0)",
			PayloadMatrix.Parse(Matrix(("|nuget:c/5.6.0|","|build:SomeTransform(nuget:c/5.6.0)|")))["Compiler"].Provenance);
	}

	[Fact]
	public void The_shipped_compiler_payloads_are_build_derived_from_the_pinned_roslyn() {
		var matrix=PayloadMatrixVerification.VerifyPayloadFile(Payload());
		Assert.Equal("build:RetargetNetstandardReferences(nuget:microsoft.codeanalysis.common/5.6.0)",matrix["Microsoft.CodeAnalysis"].Provenance);
		Assert.Equal("build:RetargetNetstandardReferences(nuget:microsoft.codeanalysis.csharp/5.6.0)",matrix["Microsoft.CodeAnalysis.CSharp"].Provenance);
		Assert.Equal("build:RetargetNetstandardReferences(project:HookLab/HookLab.Contracts)",matrix["HookLab.Contracts"].Provenance);
		// Identity is untouched by the rewrite, which is why nothing that binds to Roslyn had to change.
		Assert.Equal("31bf3856ad364e35",matrix["Microsoft.CodeAnalysis.CSharp"].PublicKeyToken);
		Assert.Equal(new Version(5,6,0,0),matrix["Microsoft.CodeAnalysis.CSharp"].AssemblyVersion);
	}

	static string Payload()=>HookLabPayload.Path();
}
