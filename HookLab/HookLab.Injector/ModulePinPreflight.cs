#if NET
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
#endif

namespace HookLab.Injector;

/// <summary>Refuses a fresh injection whose pinned module MVID cannot match the target's module file.
/// The resident checks the pin only after its payloads are committed, and residency cannot be rolled
/// back: a stale pin used to leave a loaded resident with no endpoint, which then made every later
/// HookLab initialization of that process - the watcher's other profiles and dgSpy alike - refuse as
/// a foreign resident. Measured on vmconnect after the 2026-10-26 cumulative update rebuilt it.
///
/// This reads only the module file on disk, before anything enters the target. It answers only when
/// it can be sure: no file, a file of another assembly, or an unreadable image all defer to the
/// resident's own exact guard as before. An in-use image can still be renamed aside and replaced, so a
/// process started before an update can be refused although it runs the pinned build; that error
/// is safe - nothing is injected - and the next start of the process matches the file.</summary>
public static class ModulePinPreflight {
	public static void Check(string imagePath,HookDefinition definition) {
		var target=definition.Target!; var actual=ModuleMvid(imagePath,target.Assembly!);
		if(actual is null||actual.Value==Guid.Parse(target.ModuleMvid!)) return;
		throw new StaleModulePinException("Hook "+definition.Id+" pins "+target.Assembly+" module MVID "+target.ModuleMvid+", but the target's module file now has MVID "+actual.Value.ToString("D")+
			"; refusing injection before anything enters the target. The module was rebuilt, typically by an update: re-pin the package against the current build.");
	}

	/// <summary>The MVID of the named assembly as the target would load it from its own directory, or null
	/// when that cannot be determined from disk.</summary>
	public static Guid? ModuleMvid(string imagePath,string assemblyName) {
		if(String.IsNullOrWhiteSpace(imagePath)) return null;
		var candidates=new[] { imagePath,Path.Combine(Path.GetDirectoryName(imagePath)??"",assemblyName+".dll") };
		foreach(var path in candidates) {
			if(!String.Equals(Path.GetFileNameWithoutExtension(path),assemblyName,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)) continue;
			return Read(path,assemblyName);
		}
		return null;
	}

	static Guid? Read(string path,string assemblyName) {
#if NET
		try {
			using var stream=File.OpenRead(path); using var pe=new PEReader(stream);
			if(!pe.HasMetadata) return null;
			var metadata=pe.GetMetadataReader(); if(!metadata.IsAssembly) return null;
			if(!String.Equals(metadata.GetString(metadata.GetAssemblyDefinition().Name),assemblyName,StringComparison.OrdinalIgnoreCase)) return null;
			return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
		}
		catch(BadImageFormatException) { return null; }
		catch(IOException) { return null; }
		catch(UnauthorizedAccessException) { return null; }
#else
		// Only the net10 watcher and apply-once tools inject through ResidentCoordinator; the net48
		// target exists for the extension, which has its own initialization path.
		return null;
#endif
	}
}

public sealed class StaleModulePinException : InvalidOperationException { public StaleModulePinException(string message):base(message) { } }
