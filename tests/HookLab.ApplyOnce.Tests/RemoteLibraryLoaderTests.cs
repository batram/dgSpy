using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HookLab.Injector;
using Xunit;

/// <summary>
/// The target's loader is authoritative for load failure, and until now it had no voice: a remote
/// thread started at LoadLibraryW reports a truncated HMODULE as its exit code, so access denied, a
/// missing dependency and a bad image were the same zero. These tests prove the four outcomes that
/// were previously indistinguishable are now distinguishable, against a real target process.
///
/// The target is a disposable <c>cmd.exe</c> holding on redirected stdin, with no window. Nothing is
/// injected into it that runs code except in the one success case, which loads a Windows system DLL.
/// </summary>
public sealed class RemoteLibraryLoaderTests : IDisposable {
	readonly string directory=Path.Combine(Path.GetTempPath(),"dgspy-remote-loader-tests-"+Guid.NewGuid().ToString("N"));
	readonly List<string> restrictedFiles=new();

	public RemoteLibraryLoaderTests()=>Directory.CreateDirectory(directory);

	public void Dispose() {
		foreach(var path in restrictedFiles) TryUnrestrict(path);
		try { Directory.Delete(directory,true); } catch { }
	}

	[Fact]
	public void Reports_the_module_handle_when_the_target_loads_the_library() {
		using var target=DisposableTarget.Start();
		var result=RemoteLibraryLoader.TryLoad(target.Id,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"version.dll"),10000);
		Assert.True(result.StubCompleted);
		Assert.True(result.Loaded,result.Describe());
		// The whole point of the stub: a full 64-bit handle, not a DWORD with the top half discarded.
		Assert.NotEqual(0UL,result.ModuleHandle);
		// Measured: 0x00007fff612c0000 on the machine this was written on. A module base in the x64
		// user-mode range does not survive a DWORD, which is what the old exit-code path stored it in.
		Assert.True(result.ModuleHandle>0xFFFFFFFFUL,"Expected a 64-bit module base; got 0x"+result.ModuleHandle.ToString("x16"));
	}

	[Fact]
	public void Reports_ERROR_MOD_NOT_FOUND_when_the_target_cannot_see_the_file() {
		using var target=DisposableTarget.Start();
		var result=RemoteLibraryLoader.TryLoad(target.Id,Path.Combine(directory,"absent-"+Guid.NewGuid().ToString("N")+".dll"),10000);
		Assert.True(result.StubCompleted);
		Assert.False(result.Loaded);
		Assert.Equal(126,result.LastError);
		Assert.Equal("ERROR_MOD_NOT_FOUND",result.LastErrorName);
	}

	[Fact]
	public void Reports_ERROR_MOD_NOT_FOUND_when_a_dependency_of_the_file_is_missing() {
		// A genuinely missing *dependency*, not a missing file: a copy of our own injected artifact
		// with the name of an imported module rewritten to one that does not exist. The replacement is
		// the same length, so nothing in the image moves and no other field needs fixing up. This is
		// the shape of the defect that actually blocked initialization on a live target, where the
		// absent dependency was VCRUNTIME140_1.dll.
		using var target=DisposableTarget.Start();
		var broken=Path.Combine(directory,"broken-import.dll");
		var image=File.ReadAllBytes(NativeBootstrapPath());
		Assert.True(Rewrite(image,"mscoree.dll","mscorzz.dll")>0,"Expected the injected artifact to import mscoree.dll.");
		File.WriteAllBytes(broken,image);
		var result=RemoteLibraryLoader.TryLoad(target.Id,broken,10000);
		Assert.True(result.StubCompleted);
		Assert.False(result.Loaded);
		Assert.Equal(126,result.LastError);
		Assert.Equal("ERROR_MOD_NOT_FOUND",result.LastErrorName);
	}

	[Fact]
	public void Reports_ERROR_ACCESS_DENIED_when_the_target_cannot_read_the_file() {
		using var target=DisposableTarget.Start();
		var denied=Restrict(Path.Combine(directory,"denied.dll"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"version.dll"));
		var result=RemoteLibraryLoader.TryLoad(target.Id,denied,10000);
		Assert.True(result.StubCompleted);
		Assert.False(result.Loaded);
		Assert.Equal(5,result.LastError);
		Assert.Equal("ERROR_ACCESS_DENIED",result.LastErrorName);
	}

	[Fact]
	public void Reports_ERROR_BAD_EXE_FORMAT_for_a_file_that_is_not_an_image() {
		using var target=DisposableTarget.Start();
		var junk=Path.Combine(directory,"not-an-image.dll");
		File.WriteAllBytes(junk,Encoding.ASCII.GetBytes(new string('x',4096)));
		var result=RemoteLibraryLoader.TryLoad(target.Id,junk,10000);
		Assert.True(result.StubCompleted);
		Assert.False(result.Loaded);
		Assert.Equal(193,result.LastError);
		Assert.Equal("ERROR_BAD_EXE_FORMAT",result.LastErrorName);
	}

	/// <summary>The exit evidence for this subslice: the two causes that produced one byte-identical
	/// message now produce two messages that name different codes. Everything above establishes the
	/// codes; this establishes that they survive into the text a caller reads.</summary>
	[Fact]
	public void A_denied_read_and_a_missing_dependency_are_distinguishable_in_the_message() {
		using var target=DisposableTarget.Start();
		var denied=Restrict(Path.Combine(directory,"denied-message.dll"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"version.dll"));
		var missing=Path.Combine(directory,"missing-message.dll");

		var deniedError=Assert.Throws<RemoteLibraryLoadException>(()=>RemoteLibraryLoader.Load(target.Id,denied,10000));
		var missingError=Assert.Throws<RemoteLibraryLoadException>(()=>RemoteLibraryLoader.Load(target.Id,missing,10000));

		Assert.Contains("ERROR_ACCESS_DENIED (5)",deniedError.Message,StringComparison.Ordinal);
		Assert.Contains("ERROR_MOD_NOT_FOUND (126)",missingError.Message,StringComparison.Ordinal);
		Assert.NotEqual(deniedError.Message,missingError.Message);
		Assert.Equal(5,deniedError.Win32Error);
		Assert.Equal(126,missingError.Win32Error);
	}

	[Fact]
	public void Refuses_a_library_path_that_does_not_fit_the_shared_page() {
		using var target=DisposableTarget.Start();
		var absurd=Path.Combine(directory,new string('p',1200)+".dll");
		Assert.Throws<ArgumentException>(()=>RemoteLibraryLoader.TryLoad(target.Id,absurd,10000));
	}

	[Fact]
	public void Names_an_unmapped_code_deterministically_rather_than_leaving_a_bare_number() {
		var unmapped=new string[]{ "WIN32_ERROR_4242" };
		Assert.Equal(unmapped[0],Win32ErrorNames.Name(4242));
		Assert.Null(Win32ErrorNames.Meaning(4242));
		Assert.Equal("ERROR_MOD_NOT_FOUND",Win32ErrorNames.Name(126));
		Assert.NotNull(Win32ErrorNames.Meaning(126));
	}

	static string NativeBootstrapPath() {
		var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
		var path=Path.Combine(repo,"HookLab","HookLab.NativeBootstrap","bin","Release","HookLab.NativeBootstrap.x64.dll");
		Assert.True(File.Exists(path),"Build the native bootstrap first: dotnet run --project Build\\DgSpyTool -- pipeline");
		return path;
	}

	static int Rewrite(byte[] image,string from,string to) {
		Assert.Equal(from.Length,to.Length);
		var pattern=Encoding.ASCII.GetBytes(from);
		var replacement=Encoding.ASCII.GetBytes(to);
		var count=0;
		for(var at=0;at<=image.Length-pattern.Length;at++) {
			var match=true;
			for(var i=0;i<pattern.Length&&match;i++) match=image[at+i]==pattern[i];
			if(!match) continue;
			replacement.CopyTo(image,at);
			count++;
		}
		return count;
	}

	/// <summary>Copies a real library and denies the running account read access to the copy, with
	/// inheritance broken so the deny is the whole answer.</summary>
	string Restrict(string path,string source) {
		File.Copy(source,path,true);
		var account=WindowsIdentity.GetCurrent().User!;
		var security=new FileSecurity();
		security.SetAccessRuleProtection(true,false);
		security.AddAccessRule(new FileSystemAccessRule(account,FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.ReadPermissions,AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(account,FileSystemRights.ReadData|FileSystemRights.ReadAttributes|FileSystemRights.ExecuteFile,AccessControlType.Deny));
		security.SetOwner(account);
		new FileInfo(path).SetAccessControl(security);
		restrictedFiles.Add(path);
		return path;
	}

	static void TryUnrestrict(string path) {
		try {
			var info=new FileInfo(path);
			var security=info.GetAccessControl();
			security.SetAccessRuleProtection(false,false);
			foreach(FileSystemAccessRule rule in security.GetAccessRules(true,false,typeof(SecurityIdentifier)))
				if(rule.AccessControlType==AccessControlType.Deny) security.RemoveAccessRule(rule);
			info.SetAccessControl(security);
		}
		catch { }
	}

	sealed class DisposableTarget : IDisposable {
		readonly Process process;
		DisposableTarget(Process process)=>this.process=process;
		public int Id=>process.Id;
		public static DisposableTarget Start() {
			// cmd holds until stdin closes, so the target stays alive for exactly as long as the test
			// does and needs no cooperation from a fixture build. CreateNoWindow keeps it off the
			// desktop entirely.
			var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
			var process=Process.Start(start)??throw new InvalidOperationException("Could not start the disposable target.");
			// Wait for the target to finish initializing: a process whose loader has not run yet has
			// no module list to resolve kernel32 against, and that would look like a loader defect.
			var deadline=DateTime.UtcNow.AddSeconds(10);
			while(DateTime.UtcNow<deadline) {
				try { process.Refresh(); if(process.Modules.Count>1) return new DisposableTarget(process); }
				catch(System.ComponentModel.Win32Exception) { }
				Thread.Sleep(20);
			}
			process.Kill(true);
			throw new TimeoutException("The disposable target did not finish starting.");
		}
		public void Dispose() { try { process.Kill(true); process.WaitForExit(5000); } catch { } process.Dispose(); }
	}
}
