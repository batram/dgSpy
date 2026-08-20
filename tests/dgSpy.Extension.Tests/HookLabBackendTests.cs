using Xunit;
using HookLab.Bootstrap;

namespace dgSpy.Extension.Tests;

/// <summary>
/// The backend table is the host's single statement of what each supported runtime needs. These tests
/// hold it to two separate things: that selection is deterministic and refuses by name, and - the part
/// that could not be checked while these facts were conditionals scattered through orchestration - that
/// what the host believes each runtime uses is what the build actually ships for it.
/// </summary>
public sealed class HookLabBackendTests {
	static readonly Guid DesktopClr=new("CD03ACDD-4F3A-4736-8591-4902B4DCC8C1");
	static readonly Guid CoreClr=new("E0B4EB52-D1D9-42AB-B130-028CA31CF9F6");
	static HookLabRuntimeIdentity[] Desktop=>new[]{ new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319") };
	static HookLabRuntimeIdentity[] Core=>new[]{ new HookLabRuntimeIdentity(CoreClr,"CoreCLR") };

	[Fact]
	public void X64_desktop_clr_v4_selects_the_native_bootstrap_backend() {
		Assert.Null(HookLabBackends.UnsupportedReason(64,"X64",Desktop));
		var backend=Assert.IsType<HookLabBackend>(HookLabBackends.Select(64,"X64",Desktop));
		Assert.Equal("clrv4-x64",backend.Id);
		Assert.Equal("DesktopClrV4",backend.Name);
		Assert.Equal(HookLabArrival.NativeBootstrap,backend.Arrival);
		// One version, so it is a constant rather than something read from the target.
		Assert.Equal("v4.0.30319",backend.FixedRuntimeId);
		Assert.False(backend.SynchronizesAfterArrival);
	}

	[Fact]
	public void X64_coreclr_selects_the_debugger_evaluation_backend() {
		Assert.Null(HookLabBackends.UnsupportedReason(64,"X64",Core));
		var backend=Assert.IsType<HookLabBackend>(HookLabBackends.Select(64,"X64",Core));
		Assert.Equal("coreclr-x64",backend.Id);
		Assert.Equal("CoreClr",backend.Name);
		Assert.Equal(HookLabArrival.DebuggerEvaluation,backend.Arrival);
		// Many versions, so the exact one has to come from the live process.
		Assert.Null(backend.FixedRuntimeId);
		Assert.True(backend.SynchronizesAfterArrival);
	}

	[Theory]
	[InlineData(32,"X86")]
	[InlineData(64,"Arm64")]
	public void Unsupported_architecture_is_rejected_before_runtime_selection(int bitness,string architecture) {
		Assert.Empty(HookLabBackends.Candidates(bitness,architecture,Desktop));
		Assert.Contains("attached process architecture is "+architecture,
			HookLabBackends.UnsupportedReason(bitness,architecture,Desktop),StringComparison.Ordinal);
	}

	[Fact]
	public void Missing_runtime_is_rejected_explicitly() {
		Assert.Null(HookLabBackends.Select(64,"X64",Array.Empty<HookLabRuntimeIdentity>()));
		Assert.Equal("HookLab currently supports x64 desktop CLR v4 and CoreCLR targets; the attached process exposes no managed runtime.",
			HookLabBackends.UnsupportedReason(64,"X64",Array.Empty<HookLabRuntimeIdentity>()));
	}

	[Fact]
	public void An_unknown_runtime_is_named_in_the_refusal() {
		var reason=HookLabBackends.UnsupportedReason(64,"X64",new[]{ new HookLabRuntimeIdentity(Guid.NewGuid(),"Mono") });
		Assert.Contains("the attached process exposes Mono.",reason,StringComparison.Ordinal);
	}

	/// <summary>CLR v2 shares the .NET Framework runtime GUID, so the GUID alone is not the identity.</summary>
	[Fact]
	public void The_desktop_runtime_guid_alone_does_not_select_a_backend() {
		Assert.Null(HookLabBackends.Select(64,"X64",new[]{ new HookLabRuntimeIdentity(DesktopClr,"CLR v2.0.50727") }));
	}

	/// <summary>A process really can have both loaded. What it gets is decided by declared priority, not
	/// by the order of a chain of ifs, so the answer is written down and stays put.</summary>
	[Fact]
	public void A_mixed_runtime_process_resolves_by_declared_priority() {
		var mixed=new[]{ new HookLabRuntimeIdentity(CoreClr,"CoreCLR"),new HookLabRuntimeIdentity(DesktopClr,"CLR v4.0.30319") };
		Assert.Equal(new[]{"clrv4-x64","coreclr-x64"},HookLabBackends.Candidates(64,"X64",mixed).Select(backend=>backend.Id).ToArray());
		Assert.Equal("clrv4-x64",HookLabBackends.Select(64,"X64",mixed)!.Id);
		Assert.Null(HookLabBackends.UnsupportedReason(64,"X64",mixed));
	}

	[Fact]
	public void The_table_is_the_two_proved_backends_and_nothing_else() {
		Assert.Equal(new[]{"clrv4-x64","coreclr-x64"},HookLabBackends.All.Select(backend=>backend.Id).ToArray());
		Assert.All(HookLabBackends.All,backend=>{ Assert.Equal(64,backend.Bitness); Assert.Equal("X64",backend.Architecture); });
		Assert.Equal(HookLabBackends.All.Length,HookLabBackends.All.Select(backend=>backend.Priority).Distinct().Count());
	}

	/// <summary>The cross-check that the descriptor exists for: every payload id the host names must be a
	/// real slot in the shipped payload matrix, and must be valid on the runtime family the host names it
	/// for. Before this, "CoreCLR uses the net6.0 Harmony" was asserted in the target's loader and
	/// nowhere the host could see, so the two could disagree and nothing would notice until a live gate.</summary>
	[SkippableFact]
	public void Every_backend_payload_id_is_a_matrix_slot_valid_on_that_family() {
		var payload=ShippedPayload();
		Skip.If(payload is null,"No composed layout to read the payload matrix from. Run the pipeline first.");
		var matrix=PayloadMatrixVerification.VerifyPayloadFile(payload!);
		foreach(var backend in HookLabBackends.All) {
			var expected=backend.Family=="clrv4"?PayloadRuntimes.ClrV4:PayloadRuntimes.CoreClr;
			foreach(var id in new[]{backend.PatchEnginePayloadId}.Concat(backend.CompilerPayloadIds)) {
				var entry=matrix.Entries.SingleOrDefault(value=>value.Id==id);
				Assert.True(entry is not null,backend.Id+" names payload '"+id+"', which the shipped matrix does not carry.");
				Assert.True((entry!.Runtimes&expected)!=0,backend.Id+" names payload '"+id+"', which the matrix says is not valid on "+backend.Family+".");
			}
			Assert.Equal(PayloadRole.PatchEngine,matrix[backend.PatchEnginePayloadId].Role);
		}
		// And the two backends must not share a patch engine: that they need different ones is the whole
		// reason the runtime axis exists.
		Assert.Equal(HookLabBackends.All.Length,HookLabBackends.All.Select(backend=>backend.PatchEnginePayloadId).Distinct().Count());
	}

	static string? ShippedPayload() {
		const string name="hooklab-bootstrap.net48.payload";
		var configured=Environment.GetEnvironmentVariable("DGSPY_LAYOUT_ROOT");
		if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(Path.Combine(configured,"hooklab",name))) return Path.Combine(configured,"hooklab",name);
		var repository=Repository();
		var layouts=repository is null?null:Path.Combine(repository,"artifacts","layouts");
		if(layouts is null||!Directory.Exists(layouts)) return null;
		return new DirectoryInfo(layouts).GetDirectories().OrderByDescending(directory=>directory.LastWriteTimeUtc)
			.Select(directory=>Path.Combine(directory.FullName,"hooklab",name)).FirstOrDefault(File.Exists);
	}

	static string? Repository() {
		for(var current=new DirectoryInfo(AppContext.BaseDirectory);current is not null;current=current.Parent)
			if(File.Exists(Path.Combine(current.FullName,"dnSpy.sln"))) return current.FullName;
		return null;
	}
}
