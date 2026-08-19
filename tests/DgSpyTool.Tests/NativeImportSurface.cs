// Reads the direct and delay-load import directories of a PE32+ image.
//
// This exists so that the dependency surface of anything dgSpy injects into a target process is a
// reviewed list rather than a side effect of a build setting. It deliberately reimplements the small
// part of the PE format it needs instead of taking a dependency: the artifact under test is a native
// DLL that no managed reflection API will open, and System.Reflection.PortableExecutable exposes the
// data directories but not the import descriptors behind them.
//
// PE32 (x86) is refused rather than parsed. x86 is out of scope for the product, and a reader that
// silently accepted it would report an empty surface for an image it did not actually understand.
static class NativeImportSurface {
	const int DosHeaderSize=0x40, PeOffsetField=0x3C;
	const ushort MachineAmd64=0x8664, Pe32PlusMagic=0x20B;
	const int ImportDirectoryIndex=1, DelayImportDirectoryIndex=13;
	const int ImportDescriptorSize=20, ImportDescriptorNameField=12;
	const int DelayDescriptorSize=32, DelayDescriptorAttributesField=0, DelayDescriptorNameField=4;
	// ImgDelayDescr.grAttrs bit 0 (dlattrRva): the descriptor's address fields are RVAs. Linkers
	// older than Visual Studio 2005 left it clear and stored virtual addresses instead.
	const uint DelayAttributeRva=1;

	public static ImportSurface Read(string path)=>Read(File.ReadAllBytes(path),path);

	public static ImportSurface Read(byte[] image,string name) {
		if(image.Length<DosHeaderSize||image[0]!=(byte)'M'||image[1]!=(byte)'Z') throw new InvalidDataException(name+" is not a PE image.");
		var pe=BitConverter.ToInt32(image,PeOffsetField);
		if(pe<0||pe+24>image.Length||BitConverter.ToUInt32(image,pe)!=0x00004550) throw new InvalidDataException(name+" has no PE signature.");
		var machine=BitConverter.ToUInt16(image,pe+4);
		if(machine!=MachineAmd64) throw new InvalidDataException(name+" is machine 0x"+machine.ToString("x4")+"; this reader is x64 only.");
		var sectionCount=BitConverter.ToUInt16(image,pe+6);
		var optionalSize=BitConverter.ToUInt16(image,pe+20);
		var optional=pe+24;
		if(BitConverter.ToUInt16(image,optional)!=Pe32PlusMagic) throw new InvalidDataException(name+" is not a PE32+ image.");
		var directoryCount=BitConverter.ToUInt32(image,optional+108);
		var directories=optional+112;
		var sections=optional+optionalSize;
		var map=new SectionMap(image,sections,sectionCount);
		return new ImportSurface(
			ReadDirect(image,map,Directory(image,directories,directoryCount,ImportDirectoryIndex),name),
			ReadDelayLoad(image,map,Directory(image,directories,directoryCount,DelayImportDirectoryIndex),name));
	}

	static (uint Rva,uint Size) Directory(byte[] image,int directories,uint count,int index) {
		if(index>=count) return (0,0);
		var at=directories+index*8;
		if(at+8>image.Length) return (0,0);
		return (BitConverter.ToUInt32(image,at),BitConverter.ToUInt32(image,at+4));
	}

	static IReadOnlyList<string> ReadDirect(byte[] image,SectionMap map,(uint Rva,uint Size) directory,string name) {
		var names=new List<string>();
		if(directory.Rva==0) return names;
		for(var rva=directory.Rva;;rva+=ImportDescriptorSize) {
			var at=map.Offset(rva,ImportDescriptorSize,name);
			if(IsZero(image,at,ImportDescriptorSize)) return names;
			names.Add(ReadName(image,map,BitConverter.ToUInt32(image,at+ImportDescriptorNameField),name));
		}
	}

	static IReadOnlyList<string> ReadDelayLoad(byte[] image,SectionMap map,(uint Rva,uint Size) directory,string name) {
		var names=new List<string>();
		if(directory.Rva==0) return names;
		for(var rva=directory.Rva;;rva+=DelayDescriptorSize) {
			var at=map.Offset(rva,DelayDescriptorSize,name);
			if(IsZero(image,at,DelayDescriptorSize)) return names;
			var attributes=BitConverter.ToUInt32(image,at+DelayDescriptorAttributesField);
			// The virtual-address form is refused rather than guessed at. It predates Visual Studio
			// 2005, never occurs in a PE32+ image, and its addresses do not fit the 32-bit fields once
			// the image base is 0x1_0000_0000 or above. Reading it as an RVA anyway would report the
			// wrong modules or none, and reporting none is precisely the failure this reader must not
			// have.
			if((attributes&DelayAttributeRva)==0) throw new InvalidDataException(name+" uses the pre-2005 virtual-address delay-load form, which this reader does not decode.");
			names.Add(ReadName(image,map,BitConverter.ToUInt32(image,at+DelayDescriptorNameField),name));
		}
	}

	static string ReadName(byte[] image,SectionMap map,uint rva,string name) {
		var at=map.Offset(rva,1,name);
		var end=at;
		while(end<image.Length&&image[end]!=0) end++;
		if(end==at) throw new InvalidDataException(name+" names an empty imported module at RVA 0x"+rva.ToString("x8")+".");
		return System.Text.Encoding.ASCII.GetString(image,at,end-at);
	}

	static bool IsZero(byte[] image,int at,int length) {
		for(var i=0;i<length;i++) if(image[at+i]!=0) return false;
		return true;
	}

	readonly struct SectionMap {
		readonly (uint Rva,uint Size,uint Raw)[] sections;
		readonly int length;
		public SectionMap(byte[] image,int at,int count) {
			length=image.Length;
			sections=new (uint,uint,uint)[count];
			for(var i=0;i<count;i++) {
				var header=at+i*40;
				var virtualSize=BitConverter.ToUInt32(image,header+8);
				var rawSize=BitConverter.ToUInt32(image,header+16);
				sections[i]=(BitConverter.ToUInt32(image,header+12),Math.Max(virtualSize,rawSize),BitConverter.ToUInt32(image,header+20));
			}
		}
		public int Offset(uint rva,int needed,string name) {
			foreach(var section in sections)
				if(rva>=section.Rva&&rva<section.Rva+section.Size) {
					var offset=checked((int)(section.Raw+(rva-section.Rva)));
					if(offset<0||offset+needed>length) break;
					return offset;
				}
			throw new InvalidDataException(name+" has an import RVA outside its sections: 0x"+rva.ToString("x8")+".");
		}
	}
}

readonly record struct ImportSurface(IReadOnlyList<string> Direct,IReadOnlyList<string> DelayLoad) {
	public IEnumerable<string> All=>Direct.Concat(DelayLoad);
}
