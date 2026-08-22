using System.Diagnostics;
using System.Globalization;

namespace HookLab.Injector;

public sealed class ResidentPayloadStore {
	readonly string root;
	public ResidentPayloadStore(string? stateRoot=null) {
		var state=HookLab.Host.Transport.Discovery.DgSpyStateRoot.Resolve(stateRoot);
		root=Path.Combine(state,"hooklab","resident-payloads");
		Directory.CreateDirectory(root); RejectReparse(root);
	}
	public string Create(int processId,long creationUtcTicks) {
		CleanupExited();
		var name=processId.ToString(CultureInfo.InvariantCulture)+"-"+creationUtcTicks.ToString(CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
		var path=SafeChild(name); Directory.CreateDirectory(path); RejectReparse(path); return path;
	}
	public IReadOnlyList<string> Find(int processId,long creationUtcTicks) {
		if(!Directory.Exists(root)) return Array.Empty<string>();
		var prefix=processId.ToString(CultureInfo.InvariantCulture)+"-"+creationUtcTicks.ToString(CultureInfo.InvariantCulture)+"-";
		var matches=new List<string>();
		foreach(var path in Directory.EnumerateDirectories(root,prefix+"*",SearchOption.TopDirectoryOnly)) { RejectReparse(path); EnsureChild(path); matches.Add(path); }
		return matches.AsReadOnly();
	}
	public void Delete(string path) { var exact=Path.GetFullPath(path); EnsureChild(exact); if(Directory.Exists(exact)) Directory.Delete(exact,true); }
	public void CleanupExited() {
		if(!Directory.Exists(root)) return;
		foreach(var path in Directory.EnumerateDirectories(root)) {
			RejectReparse(path); var name=Path.GetFileName(path); var parts=name.Split('-');
			if(parts.Length!=3||!Int32.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out var processId)||!Int64.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var creationTicks)) throw new InvalidDataException("Resident payload directory has an invalid identity: "+name);
			if(!IsAlive(processId,creationTicks)) Delete(path);
		}
	}
	static bool IsAlive(int processId,long creationTicks) { try { using var process=Process.GetProcessById(processId); return process.StartTime.ToUniversalTime().Ticks==creationTicks; } catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } }
	string SafeChild(string name) { var path=Path.GetFullPath(Path.Combine(root,name)); EnsureChild(path); return path; }
	void EnsureChild(string path) { var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Resident payload path escapes its root."); }
	static void RejectReparse(string path) { if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Resident payload paths may not be reparse points."); }
}
