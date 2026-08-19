using Xunit;

/// <summary>
/// The import-surface reader is harness code that the injected-artifact policy depends on entirely,
/// and a reader that quietly finds nothing looks exactly like an artifact that imports nothing. These
/// tests exercise it against real linker output rather than only against the synthesised fixture, so
/// a defect in the reader cannot present itself as a clean dependency surface.
/// </summary>
public sealed class NativeImportSurfaceReaderTests {
	// A Windows system DLL, chosen because it carries a populated delay-load import directory. The
	// assertions are structural rather than a fixed module list: which modules ole32 delay-loads is
	// Microsoft's business and changes between Windows builds, while "there is a delay-load directory
	// and it parses into plausible module names" is the property this reader must have.
	static readonly string SystemImageWithDelayLoadImports=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"ole32.dll");

	[Fact]
	public void Reads_both_import_directories_of_real_linker_output() {
		Assert.True(File.Exists(SystemImageWithDelayLoadImports),SystemImageWithDelayLoadImports+" is missing; this reader is Windows-only, as is the product.");
		var surface=NativeImportSurface.Read(SystemImageWithDelayLoadImports);
		Assert.NotEmpty(surface.Direct);
		Assert.NotEmpty(surface.DelayLoad);
		foreach(var module in surface.All) {
			Assert.EndsWith(".dll",module,StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(module,new[]{"","."});
			Assert.All(module,character=>Assert.InRange(character,' ','~'));
		}
		Assert.Contains(surface.Direct,module=>String.Equals(module,"KERNEL32.dll",StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Refuses_an_x86_image_rather_than_reporting_an_empty_surface() {
		var image=NativeImportSurfaceFixture.Build();
		image[0x40+4]=0x4C; image[0x40+5]=0x01;  // COFF Machine: I386
		var error=Assert.Throws<InvalidDataException>(()=>NativeImportSurface.Read(image,"x86-fixture"));
		Assert.Contains("x64 only",error.Message,StringComparison.Ordinal);
	}

	[Fact]
	public void Refuses_something_that_is_not_a_pe_image() {
		Assert.Throws<InvalidDataException>(()=>NativeImportSurface.Read(new byte[256],"zeroes"));
	}

	[Fact]
	public void Refuses_the_legacy_virtual_address_delay_load_form() {
		// Linkers older than Visual Studio 2005 leave ImgDelayDescr.grAttrs clear and store virtual
		// addresses. Decoding that as an RVA would name the wrong modules or none at all, so the
		// reader refuses it. An artifact in that form would be a finding, not a clean surface.
		var image=NativeImportSurfaceFixture.Build();
		BitConverter.GetBytes(0u).CopyTo(image,0x240);  // section raw 0x200 + delay table offset 0x40
		var error=Assert.Throws<InvalidDataException>(()=>NativeImportSurface.Read(image,"legacy-delay-fixture"));
		Assert.Contains("virtual-address delay-load form",error.Message,StringComparison.Ordinal);
	}
}
