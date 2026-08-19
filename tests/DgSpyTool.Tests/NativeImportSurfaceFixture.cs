// The negative fixture for the injected-artifact import policy.
//
// The policy test needs an image that violates the allowlist. Building one with /MD would make the
// gate depend on whichever Visual C++ toolchain happens to be installed on the machine running it -
// the exact class of ambient assumption Road 1 exists to remove - and a checked-in prebuilt DLL is a
// binary blob nobody can review and that could be mistaken for product. So the fixture is
// synthesised here: a minimal PE32+ image carrying nothing but the two import directories, byte-for-
// byte deterministic and pinned by digest in the test.
//
// It is never written into a layout or a package, is named so it cannot be confused with a shipped
// artifact, and is not a loadable DLL - it has no code, no entry point and no thunks. That is
// sufficient and intended: the policy reads import directories and never loads anything.
static class NativeImportSurfaceFixture {
	const uint SectionRva=0x1000, SectionRaw=0x200, SectionSize=0x200;
	const uint ImportTableRva=SectionRva, DelayTableRva=SectionRva+0x40, NameTableRva=SectionRva+0x80;
	const ulong FixtureImageBase=0x0000000180000000;

	public const string FileName="dgspy-import-policy-negative-fixture.notadll";

	/// <summary>The dependency surface a Release /MD build of the native bootstrap carried, which is
	/// the defect this policy prevents, plus a delay-load entry so that directory is proved read.</summary>
	public static readonly string[] DirectModules={ "KERNEL32.dll","VCRUNTIME140_1.dll" };
	public static readonly string[] DelayLoadModules={ "api-ms-win-core-fibers-l1-1-1.dll" };

	public static byte[] Build() {
		var image=new byte[SectionRaw+SectionSize];
		// DOS header: only the magic and the PE offset matter to any reader, and a stub would be code
		// this fixture deliberately does not carry.
		image[0]=(byte)'M'; image[1]=(byte)'Z';
		var pe=0x40;
		Write32(image,0x3C,(uint)pe);
		Write32(image,pe,0x00004550);                       // "PE\0\0"
		Write16(image,pe+4,0x8664);                         // Machine: AMD64
		Write16(image,pe+6,1);                              // NumberOfSections
		Write16(image,pe+20,0xF0);                          // SizeOfOptionalHeader (PE32+)
		Write16(image,pe+22,0x2022);                        // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL
		var optional=pe+24;
		Write16(image,optional,0x20B);                      // PE32+
		Write32(image,optional+28,SectionRva);              // BaseOfCode
		Write64(image,optional+24,FixtureImageBase);
		Write32(image,optional+32,0x1000);                  // SectionAlignment
		Write32(image,optional+36,0x200);                   // FileAlignment
		Write16(image,optional+40,6); Write16(image,optional+42,0);   // MajorOperatingSystemVersion
		Write16(image,optional+48,6); Write16(image,optional+50,0);   // MajorSubsystemVersion
		Write32(image,optional+56,SectionRva+0x1000);       // SizeOfImage
		Write32(image,optional+60,SectionRaw);              // SizeOfHeaders
		Write16(image,optional+68,3);                       // Subsystem: console
		Write32(image,optional+108,16);                     // NumberOfRvaAndSizes
		var directories=optional+112;
		Write32(image,directories+1*8,ImportTableRva); Write32(image,directories+1*8+4,3*20);
		Write32(image,directories+13*8,DelayTableRva); Write32(image,directories+13*8+4,2*32);
		var section=optional+0xF0;
		WriteAscii(image,section,".rdata",8);
		Write32(image,section+8,SectionSize);               // VirtualSize
		Write32(image,section+12,SectionRva);               // VirtualAddress
		Write32(image,section+16,SectionSize);              // SizeOfRawData
		Write32(image,section+20,SectionRaw);               // PointerToRawData
		Write32(image,section+36,0x40000040);               // INITIALIZED_DATA | MEM_READ

		var names=(int)(SectionRaw+(NameTableRva-SectionRva));
		var nameRva=NameTableRva;
		var imports=(int)(SectionRaw+(ImportTableRva-SectionRva));
		foreach(var module in DirectModules) {
			Write32(image,imports+12,nameRva);              // IMAGE_IMPORT_DESCRIPTOR.Name
			imports+=20;
			nameRva+=WriteName(image,ref names,module);
		}
		var delay=(int)(SectionRaw+(DelayTableRva-SectionRva));
		foreach(var module in DelayLoadModules) {
			Write32(image,delay,1);                         // ImgDelayDescr.grAttrs = dlattrRva
			Write32(image,delay+4,nameRva);                 // ImgDelayDescr.rvaDLLName
			delay+=32;
			nameRva+=WriteName(image,ref names,module);
		}
		// Both tables are terminated by an all-zero descriptor, which the zero-filled array already is.
		return image;
	}

	static uint WriteName(byte[] image,ref int at,string value) {
		WriteAscii(image,at,value,value.Length);
		var written=(uint)value.Length+1;
		at+=(int)written;
		return written;
	}
	static void WriteAscii(byte[] image,int at,string value,int length) { for(var i=0;i<value.Length&&i<length;i++) image[at+i]=(byte)value[i]; }
	static void Write16(byte[] image,int at,ushort value)=>BitConverter.GetBytes(value).CopyTo(image,at);
	static void Write32(byte[] image,int at,uint value)=>BitConverter.GetBytes(value).CopyTo(image,at);
	static void Write64(byte[] image,int at,ulong value)=>BitConverter.GetBytes(value).CopyTo(image,at);
}
