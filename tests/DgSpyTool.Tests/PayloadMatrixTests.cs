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
		"Contracts|contracts|bootstrap|P.Contracts.dll|netstandard2.0|any|clrv4,coreclr|Contracts|1.0.0.0|none|project:a||"+Digest,
		"Resident|resident|bootstrap|P.Resident.dll|net48|x64|clrv4,coreclr|Resident|1.0.0.0|none|project:b|Contracts,Compiler|"+Digest,
		"Compiler|compiler|bootstrap|P.Compiler.dll|netstandard2.0|any|clrv4,coreclr|Compiler|5.6.0.0|31bf3856ad364e35|nuget:c/5.6.0||"+Digest,
		"Engine.Desktop|patch-engine|probe|Q.Desktop.dll|net48|any|clrv4|0Harmony|2.4.2.0|none|nuget:d/2.4.2||"+Digest,
		"Engine.CoreClr|patch-engine|probe|Q.CoreClr.dll|net6.0|any|coreclr|0Harmony|2.4.2.0|none|nuget:d/2.4.2||"+Digest,
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
		Assert.Equal(5,matrix.Entries.Count);
		Assert.Equal("Resident",matrix.Entries.Single(entry=>entry.Role==PayloadRole.Resident).Id);
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
		Assert.Equal(new[]{"HookLab.Contracts","HookLab.Probe.CorDebug","Microsoft.CodeAnalysis","Microsoft.CodeAnalysis.CSharp","System.Collections.Immutable","Harmony.Desktop","Harmony.CoreClr"},
			matrix.Entries.Select(entry=>entry.Id).ToArray());
		// The runtime axis the matrix exists to make explicit: one pinned patch engine per family, and
		// neither of them visible anywhere before this manifest described them.
		Assert.Equal("net48",matrix["Harmony.Desktop"].TargetFramework);
		Assert.Equal(PayloadRuntimes.ClrV4,matrix["Harmony.Desktop"].Runtimes);
		Assert.Equal("net6.0",matrix["Harmony.CoreClr"].TargetFramework);
		Assert.Equal(PayloadRuntimes.CoreClr,matrix["Harmony.CoreClr"].Runtimes);
		Assert.All(matrix.Entries,entry=>Assert.Equal(64,entry.Sha256.Length));
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

	static string Payload()=>HookLabPayload.Path();
}
