namespace HookLab.ApplyOnce;

internal static class Program {
	public static int Main(string[] arguments) {
		if(arguments.Length is < 4 or > 6 || arguments[0]!="--pid" || !Int32.TryParse(arguments[1],out var processId) || processId<=0 || arguments[2]!="--definition" || (arguments.Length==6 && arguments[4]!="--payload-dir")) {
			Console.Error.WriteLine("Usage: HookLab.ApplyOnce.exe --pid <pid> --definition <hook.json> [--payload-dir <directory>]");
			return 2;
		}
		try {
			var definition=HookDefinition.Load(arguments[3]);
			using var process=System.Diagnostics.Process.GetProcessById(processId);
			var imagePath=process.MainModule?.FileName ?? throw new InvalidOperationException("The target image path is unavailable.");
			if(!String.Equals(Path.GetFileName(imagePath),definition.Process!.FileName,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PID "+processId+" is not "+definition.Process.FileName+"; observed "+imagePath+".");
			if(definition.Id!="vmconnect-fullscreen-sync-v1") throw new NotSupportedException("This minimal slice only has an injector adapter for vmconnect-fullscreen-sync-v1.");
			var project=FindPrototypeProject();
			var start=new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute=false };
			start.ArgumentList.Add("run"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(project); start.ArgumentList.Add("-c"); start.ArgumentList.Add("Release"); start.ArgumentList.Add("--"); start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
			start.ArgumentList.Add("--definition"); start.ArgumentList.Add(Path.GetFullPath(arguments[3]));
			if(arguments.Length==6) { start.ArgumentList.Add("--payload-dir"); start.ArgumentList.Add(Path.GetFullPath(arguments[5])); }
			using var child=System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start the proven injector adapter.");
			child.WaitForExit();
			return child.ExitCode;
		}
		catch(Exception ex) { Console.Error.WriteLine("HookLab.ApplyOnce failed: "+ex.Message); return 1; }
	}

	static string FindPrototypeProject() {
		for(var directory=new DirectoryInfo(Environment.CurrentDirectory);directory is not null;directory=directory.Parent) {
			var candidate=Path.Combine(directory.FullName,"tools","HookLab.VmConnectPrototype","HookLab.VmConnectPrototype.csproj");
			if(File.Exists(candidate)) return candidate;
		}
		throw new FileNotFoundException("Could not locate the proven HookLab.VmConnectPrototype adapter from the current directory.");
	}
}
