using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using dgSpy.Extension.PayloadDelivery;
using Xunit;

namespace dgSpy.Extension.Tests;

/// <summary>Covers T09b: resolving the shipped HookLab payload from the running host's own tree, verifying
/// it from an open handle, and refusing everything that must be refused.
///
/// Three of these run against real host layouts rather than simulated directories, because the failure the
/// spec review found - an implementation that only knows the packed <c>..\manifest.json</c> convention -
/// passes every synthetic packed-layout test while breaking the Gateway-deployed layout that
/// <c>launch_local_host</c> actually produces.</summary>
public class HookLabPayloadResolverTests {

	// ---------------------------------------------------------------------------------------------
	// Real layouts.
	// ---------------------------------------------------------------------------------------------

	[Fact]
	public void Resolves_from_a_real_packed_install_layout() {
		var root = RealLayouts.PackedHostRoot();
		Assert.True(root is not null, "No packed dgSpy install was found under %LOCALAPPDATA%\\Programs\\*\\cli. Run install-dgspy.ps1 to create one; this case must be proven against a real packed tree, not a simulated directory.");
		using var payload = HookLabPayloadResolver.OpenFrom(root!);
		Assert.Equal(HookLabPayloadLayout.PackedInstall, payload.Layout);
		Assert.Equal(HookLabPayloadCrossCheck.Verified, payload.CrossCheck);
		Assert.Equal(Path.Combine(Directory.GetParent(root!)!.FullName, "manifest.json"), payload.IndependentRecordPath);
		Assert.Equal(payload.Sha256, payload.IndependentRecordSha256, ignoreCase: true);
		Assert.Equal(payload.Sha256, Sha256OfFile(payload.PayloadPath));
	}

	[Fact]
	public void Resolves_from_the_real_gateway_deployed_layout() {
		var root = RealLayouts.GatewayDeployedHostRoot();
		Assert.True(root is not null, "No active Gateway-deployed host root was found under the dgSpy install root. Run launch_local_host to create one; the Gateway-deployed layout is the one launch_local_host actually produces and must be proven against a real deployment.");
		using var payload = HookLabPayloadResolver.OpenFrom(root!);
		Assert.Equal(HookLabPayloadLayout.GatewayDeployment, payload.Layout);
		Assert.Equal(HookLabPayloadCrossCheck.Verified, payload.CrossCheck);
		Assert.Equal(Path.Combine(root!, "deployment-manifest.json"), payload.IndependentRecordPath);
		Assert.Equal(payload.Sha256, payload.IndependentRecordSha256, ignoreCase: true);
	}

	[Fact]
	public void Resolves_from_the_real_developer_worktree_and_says_the_cross_check_was_skipped() {
		var root = RealLayouts.WorktreeHostRoot();
		Assert.True(root is not null, "The dnSpy build output carries no staged HookLab payload. Run build-dgspy.ps1; the worktree layout must be proven against the real build output.");
		using var payload = HookLabPayloadResolver.OpenFrom(root!);
		Assert.Equal(HookLabPayloadLayout.DeveloperWorktree, payload.Layout);
		Assert.Equal(HookLabPayloadCrossCheck.SkippedDeveloperWorktree, payload.CrossCheck);
		Assert.Null(payload.IndependentRecordPath);
		Assert.Null(payload.IndependentRecordSha256);
	}

	// ---------------------------------------------------------------------------------------------
	// The handle is the product.
	// ---------------------------------------------------------------------------------------------

	[Fact]
	public void Hands_back_an_open_handle_whose_bytes_are_the_verified_bytes() {
		using var layout = TestHostLayout.Packed();
		using var payload = HookLabPayloadResolver.OpenFrom(layout.HostRoot);
		Assert.True(payload.Handle.CanRead);
		var buffer = new byte[payload.Length];
		payload.Handle.Position = 0;
		var read = payload.Handle.Read(buffer, 0, buffer.Length);
		Assert.Equal(buffer.Length, read);
		Assert.Equal(payload.Sha256, Hex(SHA256.HashData(buffer)));
		Assert.Equal(payload.Sha256, payload.RehashFromHandle());
	}

	/// <summary>The measurement D7 is built on, as a test rather than a comment. Each operation is attempted
	/// from a separate process while the handle is held, and the identical operation is attempted on an
	/// unlocked twin in the same directory. Without the control the "it failed" half proves nothing - the
	/// operation could be failing for its own reasons.</summary>
	[Theory]
	[InlineData("delete")]
	[InlineData("replace_nobackup")]
	[InlineData("replace_withbackup")]
	[InlineData("write_open")]
	// A co-operative writer, not a hostile one: it asks for write access while offering to share. This is
	// the case that separates FileShare.Read from FileShare.ReadWrite - every other operation here is
	// denied under both, so without this one the share mode could be widened and no test would notice.
	[InlineData("write_open_shared")]
	[InlineData("move_rename")]
	public void A_held_handle_denies_another_process_the_write_delete_and_replace(string operation) {
		using var layout = TestHostLayout.Packed();
		var control = Path.Combine(layout.HostRoot, "control-twin.bin");
		var replacement = Path.Combine(layout.HostRoot, "replacement.bin");
		File.Copy(layout.PayloadPath, control);
		File.WriteAllBytes(replacement, new byte[] { 9, 9, 9 });

		string locked;
		using (var payload = HookLabPayloadResolver.OpenFrom(layout.HostRoot)) {
			locked = ChildProcess.Attempt(operation, payload.PayloadPath, replacement);
			Assert.True(File.Exists(payload.PayloadPath));
			Assert.Equal(payload.Sha256, payload.RehashFromHandle());
		}
		File.WriteAllBytes(replacement, new byte[] { 9, 9, 9 });
		var unlocked = ChildProcess.Attempt(operation, control, replacement);

		Assert.StartsWith("FAILED", locked);
		Assert.StartsWith("OK", unlocked);
	}

	[Fact]
	public void A_held_handle_still_permits_another_process_to_read() {
		using var layout = TestHostLayout.Packed();
		using var payload = HookLabPayloadResolver.OpenFrom(layout.HostRoot);
		Assert.StartsWith("OK", ChildProcess.Attempt("read_open", payload.PayloadPath, payload.PayloadPath));
	}

	// ---------------------------------------------------------------------------------------------
	// Refusals.
	// ---------------------------------------------------------------------------------------------

	[Fact]
	public void Refuses_a_missing_payload() {
		using var layout = TestHostLayout.Packed();
		File.Delete(layout.PayloadPath);
		Assert.Equal(HookLabPayloadRefusal.PayloadMissing, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_missing_payload_manifest() {
		using var layout = TestHostLayout.Packed();
		File.Delete(layout.PayloadManifestPath);
		Assert.Equal(HookLabPayloadRefusal.PayloadManifestMissing, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_an_unparseable_payload_manifest() {
		using var layout = TestHostLayout.Packed();
		File.WriteAllText(layout.PayloadManifestPath, "{\"format_version\":1,\"payloads\":[");
		Assert.Equal(HookLabPayloadRefusal.PayloadManifestUnparseable, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_payload_manifest_without_a_single_bootstrap_entry() {
		using var layout = TestHostLayout.Packed();
		File.WriteAllText(layout.PayloadManifestPath, "{\"format_version\":1,\"payloads\":[]}");
		Assert.Equal(HookLabPayloadRefusal.PayloadManifestInvalid, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_truncated_payload() {
		using var layout = TestHostLayout.Packed();
		var bytes = File.ReadAllBytes(layout.PayloadPath);
		File.WriteAllBytes(layout.PayloadPath, bytes.Take(bytes.Length - 16).ToArray());
		Assert.Equal(HookLabPayloadRefusal.PayloadTruncated, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_payload_whose_digest_does_not_match_its_own_manifest() {
		using var layout = TestHostLayout.Packed();
		var bytes = File.ReadAllBytes(layout.PayloadPath);
		bytes[7] ^= 0xFF;
		File.WriteAllBytes(layout.PayloadPath, bytes);
		Assert.Equal(HookLabPayloadRefusal.PayloadDigestMismatch, Refusal(layout.HostRoot));
	}

	/// <summary>P01's whole reason for a second record: the payload and its own manifest rewritten together
	/// agree with each other and disagree with the independent one. This must hold on every layout that has
	/// a record, so it is proven on both.</summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Refuses_a_payload_and_manifest_rewritten_together(bool packed) {
		using var layout = packed ? TestHostLayout.Packed() : TestHostLayout.GatewayDeployed();
		layout.RewritePayloadAndItsOwnManifest();
		Assert.Equal(HookLabPayloadRefusal.RecordsDisagree, Refusal(layout.HostRoot));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Refuses_an_absent_independent_record(bool packed) {
		using var layout = packed ? TestHostLayout.Packed() : TestHostLayout.GatewayDeployed();
		layout.RemoveIndependentRecordDigest();
		Assert.Equal(HookLabPayloadRefusal.PackageRecordMissing, Refusal(layout.HostRoot));
	}

	/// <summary>"Absent" and "unreadable" are different answers. Collapsing them turns the cross-check off
	/// for exactly the install most likely to be damaged - the Gateway learned that in 83ddc2452.</summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Refuses_an_unreadable_independent_record_distinctly_from_an_absent_one(bool packed) {
		using var layout = packed ? TestHostLayout.Packed() : TestHostLayout.GatewayDeployed();
		File.WriteAllText(layout.IndependentRecordPath, "{\"format_version\":1,\"hooklab_payload_sha256\": \"unterminated");
		Assert.Equal(HookLabPayloadRefusal.PackageRecordUnreadable, Refusal(layout.HostRoot));
	}

	/// <summary>An independent record that exists but cannot be opened must refuse as <i>unreadable</i>, and in
	/// particular must not be mistaken for absence.
	///
	/// <para>This is the case <c>File.Exists</c> got wrong. File.Exists answers "is there a readable file
	/// here" and returns false for access and metadata errors as well as for absence, so an existing but
	/// inaccessible <c>deployment-manifest.json</c> skipped the Gateway branch entirely - and a version
	/// directory is neither named <c>cli</c> nor adjacent to <c>install-dgspy.ps1</c>, so classification fell
	/// through to DeveloperWorktree and the cross-check was silently skipped. The assertion below is therefore
	/// two-part on purpose: the refusal must be the unreadable one, and it must not be a successful resolution
	/// that merely reports the cross-check as skipped.</para>
	///
	/// <para>A non-file entity at the record path is used because it is the privilege-free way to reproduce
	/// it. Measured on this machine: a <c>FileShare.None</c> lock leaves File.Exists returning <b>true</b>, and
	/// so does a Deny ACE on the file itself; only something that defeats the metadata query - here a directory
	/// occupying the record's name - makes File.Exists answer false while an open attempt reports access
	/// denied. The lock case is covered separately below, and does not discriminate this fix.</para></summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Refuses_an_unopenable_independent_record_and_never_downgrades_to_a_worktree(bool packed) {
		using var layout = packed ? TestHostLayout.Packed() : TestHostLayout.GatewayDeployed();
		File.Delete(layout.IndependentRecordPath);
		Directory.CreateDirectory(layout.IndependentRecordPath);
		Assert.False(File.Exists(layout.IndependentRecordPath), "The premise of this test is that File.Exists reports this record as absent; if it does not, the case no longer reproduces the fail-open.");

		var refusal = AssertRefusedWithoutWorktreeDowngrade(layout.HostRoot);
		Assert.Equal(HookLabPayloadRefusal.PackageRecordUnreadable, refusal);
		Assert.NotEqual(HookLabPayloadRefusal.PackageRecordMissing, refusal);
	}

	/// <summary>The same property against a record held open <c>FileShare.None</c> by this process - the
	/// resolver's own open then fails with <c>IOException</c>, which is precisely the unreadable case.
	///
	/// <para>Measured, and worth stating because it is counter-intuitive: this case does <b>not</b>
	/// discriminate the File.Exists defect. <c>GetFileAttributesEx</c> is exempt from share-mode checks, so
	/// File.Exists returns true for a locked file and the old code already reached the unreadable refusal here.
	/// It is kept because it pins the behaviour for the most likely real-world unreadable record - one another
	/// process is writing - not because it proves the fix.</para></summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Refuses_a_record_another_handle_holds_exclusively(bool packed) {
		using var layout = packed ? TestHostLayout.Packed() : TestHostLayout.GatewayDeployed();
		using var hold = new FileStream(layout.IndependentRecordPath, FileMode.Open, FileAccess.Read, FileShare.None);
		Assert.Equal(HookLabPayloadRefusal.PackageRecordUnreadable, AssertRefusedWithoutWorktreeDowngrade(layout.HostRoot));
	}

	/// <summary>Deleting the packed record entirely must not downgrade the layout to "developer worktree",
	/// which would skip the cross-check on the install most likely to have been tampered with.</summary>
	[Fact]
	public void Refuses_a_packed_install_whose_package_manifest_was_deleted() {
		using var layout = TestHostLayout.Packed();
		File.Delete(layout.IndependentRecordPath);
		Assert.Equal(HookLabPayloadRefusal.PackageRecordMissing, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_gateway_deployment_whose_manifest_records_no_package() {
		using var layout = TestHostLayout.GatewayDeployed();
		File.WriteAllText(layout.IndependentRecordPath, "{\"version\":\"bundled-000000000000\",\"payload_sha256\":\"00\"}");
		Assert.Equal(HookLabPayloadRefusal.PackageRecordMissing, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Permits_a_developer_worktree_and_reports_the_skip() {
		using var layout = TestHostLayout.Worktree();
		using var payload = HookLabPayloadResolver.OpenFrom(layout.HostRoot);
		Assert.Equal(HookLabPayloadCrossCheck.SkippedDeveloperWorktree, payload.CrossCheck);
	}

	[Fact]
	public void Refuses_a_manifest_that_names_a_payload_outside_the_host_root() {
		using var layout = TestHostLayout.Packed();
		layout.RenamePayloadInManifest(@"..\..\escaped.payload");
		Assert.Equal(HookLabPayloadRefusal.PathEscapesHostRoot, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_manifest_that_names_a_different_payload_file() {
		using var layout = TestHostLayout.Packed();
		layout.RenamePayloadInManifest("some-other.payload");
		Assert.Equal(HookLabPayloadRefusal.PayloadManifestInvalid, Refusal(layout.HostRoot));
	}

	[Fact]
	public void Refuses_a_reparse_point_on_the_resolved_path() {
		using var layout = TestHostLayout.Packed();
		var real = Path.Combine(layout.Base, "elsewhere");
		Directory.CreateDirectory(real);
		foreach (var file in Directory.GetFiles(layout.PayloadDirectory)) File.Copy(file, Path.Combine(real, Path.GetFileName(file)));
		Directory.Delete(layout.PayloadDirectory, true);
		Assert.True(Junction.Create(layout.PayloadDirectory, real), "mklink /J failed, so the reparse-point guard was never exercised.");
		Assert.Equal(HookLabPayloadRefusal.ReparsePoint, Refusal(layout.HostRoot));
	}

	// ---------------------------------------------------------------------------------------------
	// Host-root resolution.
	// ---------------------------------------------------------------------------------------------

	[Fact]
	public void Resolves_the_host_root_from_an_app_base_below_it() {
		using var layout = TestHostLayout.Packed();
		var bin = Path.Combine(layout.HostRoot, "bin");
		Directory.CreateDirectory(bin);
		Assert.Equal(layout.HostRoot, HookLabPayloadResolver.ResolveHostRoot(bin));
		Assert.Equal(layout.HostRoot, HookLabPayloadResolver.ResolveHostRoot(layout.HostRoot));
	}

	[Fact]
	public void Falls_back_to_the_app_base_when_no_payload_directory_is_found() {
		using var layout = TestHostLayout.Worktree();
		var stray = Path.Combine(layout.Base, "no-payload-here");
		Directory.CreateDirectory(stray);
		Assert.Equal(stray, HookLabPayloadResolver.ResolveHostRoot(stray));
		Assert.Equal(HookLabPayloadRefusal.PayloadMissing, Refusal(stray));
	}

	/// <summary>An ancestor is not a host root just because it contains a directory called <c>hooklab</c>.
	/// Stopping at the first such ancestor let a nested tree or a host started from an unusual working
	/// directory mask the real host root - and because the independent record is looked for relative to the
	/// resolved root, masking the root also silently changes which record, if any, is cross-checked. The
	/// resolution is followed through to a full verification here so the consequence is asserted, not just the
	/// path.</summary>
	[Fact]
	public void Walks_past_an_ancestor_whose_hooklab_directory_holds_no_payload() {
		using var layout = TestHostLayout.Packed();
		var decoy = Path.Combine(layout.HostRoot, "bin", "decoy");
		Directory.CreateDirectory(Path.Combine(decoy, "hooklab"));

		Assert.Equal(layout.HostRoot, HookLabPayloadResolver.ResolveHostRoot(decoy));
		using var payload = HookLabPayloadResolver.OpenFrom(HookLabPayloadResolver.ResolveHostRoot(decoy));
		Assert.Equal(HookLabPayloadLayout.PackedInstall, payload.Layout);
		Assert.Equal(HookLabPayloadCrossCheck.Verified, payload.CrossCheck);
	}

	/// <summary>A partial copy - the payload without its manifest - is not positive evidence either.</summary>
	[Fact]
	public void Walks_past_an_ancestor_whose_hooklab_directory_is_a_partial_copy() {
		using var layout = TestHostLayout.Packed();
		var decoy = Path.Combine(layout.HostRoot, "bin", "decoy");
		var decoyPayloadDirectory = Path.Combine(decoy, "hooklab");
		Directory.CreateDirectory(decoyPayloadDirectory);
		File.Copy(layout.PayloadPath, Path.Combine(decoyPayloadDirectory, Path.GetFileName(layout.PayloadPath)));

		Assert.Equal(layout.HostRoot, HookLabPayloadResolver.ResolveHostRoot(decoy));
	}

	// ---------------------------------------------------------------------------------------------

	/// <summary>Asserts a refusal and, separately, that the resolver did not instead succeed by classifying the
	/// tree as a developer worktree. Those are different failures: the second is the silent one.</summary>
	static HookLabPayloadRefusal AssertRefusedWithoutWorktreeDowngrade(string hostRoot) {
		ResolvedHookLabPayload? resolved = null;
		try { resolved = HookLabPayloadResolver.OpenFrom(hostRoot); }
		catch (HookLabPayloadRefusedException refused) { return refused.Refusal; }
		finally { resolved?.Dispose(); }
		Assert.Fail($"The resolver accepted a host root whose independent record could not be opened, reporting layout {resolved!.Layout} and cross-check {resolved.CrossCheck}. An unreadable record must refuse, never downgrade to a skipped cross-check.");
		throw new InvalidOperationException();
	}

	static HookLabPayloadRefusal Refusal(string hostRoot) =>
		Assert.Throws<HookLabPayloadRefusedException>(() => HookLabPayloadResolver.OpenFrom(hostRoot).Dispose()).Refusal;

	static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
	static string Sha256OfFile(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return Hex(sha.ComputeHash(stream)); }

	/// <summary>Builds one of the three real layout shapes in a temp directory, with real digests computed
	/// over real bytes. Only the success shape is built here - every refusal test mutates it afterwards, so
	/// no helper can supply the effect its test is meant to detect.</summary>
	sealed class TestHostLayout : IDisposable {
		public string Base { get; }
		public string HostRoot { get; }
		public string PayloadDirectory => Path.Combine(HostRoot, "hooklab");
		public string PayloadPath => Path.Combine(PayloadDirectory, "hooklab-bootstrap.net48.payload");
		public string PayloadManifestPath => Path.Combine(PayloadDirectory, "hooklab-payload-manifest.json");
		/// <summary>Empty for the worktree shape, which has no independent record by design.</summary>
		public string IndependentRecordPath { get; private set; } = string.Empty;

		TestHostLayout(string hostRootRelative) {
			Base = Path.Combine(Path.GetTempPath(), "t09b-" + Guid.NewGuid().ToString("N"));
			HostRoot = Path.Combine(Base, hostRootRelative);
			Directory.CreateDirectory(PayloadDirectory);
			WritePayload(RandomNumberGenerator.GetBytes(4096));
		}

		public static TestHostLayout Packed() {
			var layout = new TestHostLayout(Path.Combine("install", "cli"));
			File.WriteAllText(Path.Combine(layout.Base, "install", "install-dgspy.ps1"), "# installer\n");
			layout.IndependentRecordPath = Path.Combine(layout.Base, "install", "manifest.json");
			layout.WriteIndependentRecord(layout.CurrentSha());
			return layout;
		}

		public static TestHostLayout GatewayDeployed() {
			var layout = new TestHostLayout(Path.Combine("versions", "bundled-0123456789ab"));
			layout.IndependentRecordPath = Path.Combine(layout.HostRoot, "deployment-manifest.json");
			layout.WriteIndependentRecord(layout.CurrentSha());
			return layout;
		}

		public static TestHostLayout Worktree() => new TestHostLayout(Path.Combine("dnSpy", "bin", "Release", "net48"));

		void WriteIndependentRecord(string sha) {
			var packaged = new JsonObject { ["format_version"] = 1, ["hooklab_payload_sha256"] = sha };
			var json = IndependentRecordPath.EndsWith("deployment-manifest.json", StringComparison.OrdinalIgnoreCase)
				? new JsonObject { ["version"] = "bundled-0123456789ab", ["payload_sha256"] = "irrelevant", ["packaged"] = packaged }
				: packaged;
			File.WriteAllText(IndependentRecordPath, json.ToJsonString(), new UTF8Encoding(false));
		}

		void WritePayload(byte[] bytes) {
			File.WriteAllBytes(PayloadPath, bytes);
			var manifest = new JsonObject {
				["format_version"] = 1,
				["payloads"] = new JsonArray(new JsonObject {
					["id"] = "hooklab_bootstrap",
					["file"] = "hooklab-bootstrap.net48.payload",
					["assembly_name"] = "HookLab.Bootstrap",
					["target_framework"] = "net48",
					["architecture"] = "x64",
					["delivery"] = "bytes_only",
					["size"] = bytes.LongLength,
					["sha256"] = Hex(SHA256.HashData(bytes)),
				}),
			};
			File.WriteAllText(PayloadManifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
		}

		string CurrentSha() => Sha256OfFile(PayloadPath);

		/// <summary>The attack the independent record exists to catch: both the bytes and the record that
		/// ships with them are replaced consistently, so they agree with each other.</summary>
		public void RewritePayloadAndItsOwnManifest() => WritePayload(RandomNumberGenerator.GetBytes(2048));

		public void RemoveIndependentRecordDigest() {
			var root = JsonNode.Parse(File.ReadAllText(IndependentRecordPath))!.AsObject();
			var target = root.ContainsKey("packaged") ? root["packaged"]!.AsObject() : root;
			target.Remove("hooklab_payload_sha256");
			File.WriteAllText(IndependentRecordPath, root.ToJsonString(), new UTF8Encoding(false));
		}

		public void RenamePayloadInManifest(string name) {
			var root = JsonNode.Parse(File.ReadAllText(PayloadManifestPath))!.AsObject();
			root["payloads"]!.AsArray()[0]!["file"] = name;
			File.WriteAllText(PayloadManifestPath, root.ToJsonString(), new UTF8Encoding(false));
		}

		public void Dispose() { try { Directory.Delete(Base, true); } catch { } }
	}

	static class Junction {
		public static bool Create(string link, string target) {
			var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
			process.WaitForExit(30000);
			return process.ExitCode == 0 && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
		}
	}

	/// <summary>Runs one file operation in a separate process, because a share mode is only interesting
	/// against a process that did not open the handle.</summary>
	static class ChildProcess {
		const string Script = @"param([string]$Op,[string]$Path,[string]$Replacement)
try {
    switch ($Op) {
        'delete' { [IO.File]::Delete($Path) }
        'replace_nobackup' { [IO.File]::Replace($Replacement,$Path,[NullString]::Value) }
        'replace_withbackup' { [IO.File]::Replace($Replacement,$Path,($Path + '.bak')) }
        'write_open' { $s = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None); $s.Dispose() }
        'write_open_shared' { $s = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $s.Dispose() }
        'move_rename' { [IO.File]::Move($Path,($Path + '.renamed')) }
        'read_open' { $s = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read); $s.Dispose() }
        default { throw ""unknown operation $Op"" }
    }
    Write-Output 'OK'
}
catch { Write-Output ('FAILED ' + $_.Exception.GetType().FullName + ': ' + $_.Exception.Message) }
";
		public static string Attempt(string operation, string path, string replacement) {
			var script = Path.Combine(Path.GetTempPath(), "t09b-child-" + Guid.NewGuid().ToString("N") + ".ps1");
			File.WriteAllText(script, Script, new UTF8Encoding(false));
			try {
				var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")) {
					UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
				};
				foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Op", operation, "-Path", path, "-Replacement", replacement })
					start.ArgumentList.Add(argument);
				using var process = Process.Start(start)!;
				var output = process.StandardOutput.ReadToEnd();
				process.WaitForExit(60000);
				Assert.True(process.HasExited, "The child process did not exit.");
				return output.Trim();
			}
			finally { try { File.Delete(script); } catch { } }
		}
	}

	/// <summary>Locates the three real layouts on this machine. Nothing is fabricated: if a layout is not
	/// present the test that needs it fails and names what to run, rather than quietly passing against a
	/// directory the test built itself.</summary>
	static class RealLayouts {
		static string InstallRoot() => Environment.GetEnvironmentVariable("DGSPY_INSTALL_ROOT")
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "dgSpy");

		public static string? PackedHostRoot() {
			var configured = Environment.GetEnvironmentVariable("DGSPY_TEST_PACKED_HOST_ROOT");
			if (!string.IsNullOrWhiteSpace(configured) && HasPayload(configured!)) return Path.GetFullPath(configured!);
			var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
			if (!Directory.Exists(programs)) return null;
			foreach (var candidate in Directory.EnumerateDirectories(programs)) {
				var host = Path.Combine(candidate, "cli");
				if (HasPayload(host) && File.Exists(Path.Combine(candidate, "manifest.json"))) return host;
			}
			return null;
		}

		public static string? GatewayDeployedHostRoot() {
			var current = Path.Combine(InstallRoot(), "current.json");
			if (!File.Exists(current)) return null;
			string? active;
			try { active = (string?)JsonNode.Parse(File.ReadAllText(current))?["active_version"]; } catch { return null; }
			if (string.IsNullOrWhiteSpace(active)) return null;
			var root = Path.Combine(InstallRoot(), "versions", active!);
			return HasPayload(root) && File.Exists(Path.Combine(root, "deployment-manifest.json")) ? root : null;
		}

		public static string? WorktreeHostRoot() {
			var configured = Environment.GetEnvironmentVariable("DGSPY_TEST_WORKTREE_HOST_ROOT");
			if (!string.IsNullOrWhiteSpace(configured) && HasPayload(configured!)) return Path.GetFullPath(configured!);
			var directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory is not null) {
				var candidate = Path.Combine(directory.FullName, "dnSpy", "dnSpy", "bin", "Release", "net48");
				if (HasPayload(candidate)) return candidate;
				directory = directory.Parent;
			}
			return null;
		}

		static bool HasPayload(string root) => File.Exists(Path.Combine(root, "hooklab", "hooklab-bootstrap.net48.payload"));
	}
}
