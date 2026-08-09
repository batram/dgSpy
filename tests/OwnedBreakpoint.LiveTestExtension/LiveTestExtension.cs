using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using dgSpy.Extension.Debugger.OwnedBreakpoints;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Extension;

namespace OwnedBreakpoint.LiveTestExtension {
	/// <summary>Test-only file channel. Every owner operation delegates to the shared production service.</summary>
	[ExportExtension]
	sealed class LiveTestExtension : IExtension {
		readonly DbgManager manager;
		readonly DbgCodeBreakpointsService breakpoints;
		readonly OwnedBreakpointService service;
		readonly Dictionary<Guid,dgSpy.Extension.Debugger.OwnedBreakpoints.OwnedBreakpoint> owners=new Dictionary<Guid,dgSpy.Extension.Debugger.OwnedBreakpoints.OwnedBreakpoint>();
		Thread? pump;
		volatile bool stopping;
		string directory="";
		string logPath="";

		[ImportingConstructor]
		LiveTestExtension(DbgManager manager,DbgCodeBreakpointsService breakpoints,OwnedBreakpointService service) {
			this.manager=manager; this.breakpoints=breakpoints; this.service=service;
		}

		public IEnumerable<string> MergedResourceDictionaries { get { yield break; } }
		public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription="Owned-breakpoint live acceptance controller" };

		public void OnEvent(ExtensionEvent @event,object? obj) {
			if(@event==ExtensionEvent.AppLoaded) {
				directory=Environment.GetEnvironmentVariable("DGSPY_OWNED_BP_TEST_DIR") ?? Path.Combine(Path.GetTempPath(),"dgspy-owned-bp-test");
				Directory.CreateDirectory(directory);
				logPath=Path.Combine(directory,"controller.log");
				Write("loaded","ok",OwnedBreakpointStopLabel.Prefix);
				pump=new Thread(Pump) { IsBackground=true,Name="owned-breakpoint-live-test" };
				pump.Start();
			}
			else if(@event==ExtensionEvent.AppExit) stopping=true;
		}

		void Pump() {
			var commands=Path.Combine(directory,"commands.txt");
			var consumed=0;
			while(!stopping) {
				try {
					if(File.Exists(commands)) {
						string[] lines;
						using(var stream=new FileStream(commands,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
						using(var reader=new StreamReader(stream)) lines=reader.ReadToEnd().Replace("\r","").Split('\n');
						if(lines.Length!=0 && lines[lines.Length-1].Length==0) Array.Resize(ref lines,lines.Length-1);
						for(;consumed<lines.Length;consumed++) Dispatch(lines[consumed]);
					}
				}
				catch(Exception ex) { Write("pump","error",Flat(ex)); }
				Thread.Sleep(50);
			}
		}

		void Dispatch(string line) {
			var parts=line.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length==0) return;
			var command=parts[0].ToLowerInvariant();
			try {
				switch(command) {
				case "snapshot": Snapshot(int.Parse(parts[1],CultureInfo.InvariantCulture),parts.Length>2 ? parts[2] : ""); break;
				case "labels": SetLabels(int.Parse(parts[1],CultureInfo.InvariantCulture),parts.Skip(2).ToArray()); break;
				case "add": Add(int.Parse(parts[1],CultureInfo.InvariantCulture),parts.Length>2 && parts[2]=="stop"); break;
				case "release": Release(parts.Length>1 ? parts[1] : "*"); break;
				case "reclaim": Reclaim(); break;
				case "owners": Owners(); break;
				case "physical": Physical(uint.Parse(parts[1],CultureInfo.InvariantCulture),uint.Parse(parts[2],CultureInfo.InvariantCulture)); break;
				default: Write(command,"error","unknown command"); break;
				}
			}
			catch(Exception ex) { Write(command,"error",Flat(ex)); }
		}

		void Snapshot(int id,string tag) {
			var bp=Breakpoint(id);
			var settings=bp.Settings;
			Write("snapshot","ok","tag="+tag+" id="+id+
				" enabled="+settings.IsEnabled+
				" cond="+(settings.Condition is null ? "<none>" : settings.Condition.Value.Kind+":"+settings.Condition.Value.Condition)+
				" trace="+(settings.Trace is null ? "<none>" : settings.Trace.Value.Continue+":"+settings.Trace.Value.Message)+
				" hitcount="+(settings.HitCount is null ? "<none>" : settings.HitCount.Value.Kind+":"+settings.HitCount.Value.Count)+
				" filter="+(settings.Filter is null ? "<none>" : settings.Filter.Value.Filter)+
				" labels="+(bp.Labels.Count==0 ? "<none>" : string.Join(",",bp.Labels))+
				" bound="+bp.BoundBreakpoints.Length);
		}

		void SetLabels(int id,string[] labels) { Breakpoint(id).Labels=Array.AsReadOnly(labels); Write("labels","ok","id="+id); }

		void Add(int id,bool stop) {
			var bp=Breakpoint(id);
			var location=bp.Location as DbgDotNetCodeLocation ?? throw new InvalidOperationException("Breakpoint is not a managed IL location.");
			var runtime=bp.BoundBreakpoints.Select(item=>item.Runtime).FirstOrDefault(service.IsSupported) ??
				manager.Processes.SelectMany(process=>process.Runtimes).FirstOrDefault(service.IsSupported) ??
				throw new InvalidOperationException("No CorDebug runtime is active.");
			var owner=service.AddOwnerAsync(runtime,location,(in OwnedBreakpointHit hit)=>stop,CancellationToken.None).GetAwaiter().GetResult();
			owners.Add(owner.OwnerToken,owner);
			Write("add","ok","token="+owner.OwnerToken.ToString("N")+" state="+owner.State+" bind_error="+(owner.BindError ?? "<none>"));
		}

		void Release(string tokenText) {
			var selected=tokenText=="*" ? owners.Values.ToArray() : new[]{owners[Guid.ParseExact(tokenText,"N")]};
			foreach(var owner in selected) {
				owner.ReleaseAsync(CancellationToken.None).GetAwaiter().GetResult();
				owners.Remove(owner.OwnerToken);
				Write("release","item","token="+owner.OwnerToken.ToString("N")+" state="+owner.State+" hits="+owner.Hits);
			}
			Write("release","ok","count="+selected.Length+" remaining="+service.Owners.Count);
		}

		void Reclaim() {
			var count=service.ReclaimAbandonedAsync(CancellationToken.None).GetAwaiter().GetResult();
			owners.Clear();
			Write("reclaim","ok","count="+count+" remaining="+service.Owners.Count);
		}

		void Owners() {
			foreach(var owner in owners.Values) Write("owners","item","token="+owner.OwnerToken.ToString("N")+" state="+owner.State+" hits="+owner.Hits);
			Write("owners","ok","count="+owners.Count+" service_count="+service.Owners.Count);
		}

		void Physical(uint token,uint offset) {
			var runtime=manager.Processes.SelectMany(process=>process.Runtimes).First(service.IsSupported);
			var engine=FindEngine(runtime) ?? throw new InvalidOperationException("CorDebug engine data not found.");
			var debugger=Field(engine,"dnDebugger") ?? throw new InvalidOperationException("DnDebugger not found.");
			var list=Field(debugger,"ilCodeBreakpointList") ?? throw new InvalidOperationException("IL breakpoint list not found.");
			var all=(IEnumerable)(list.GetType().GetMethod("GetBreakpoints",Type.EmptyTypes)?.Invoke(list,null) ?? throw new InvalidOperationException("GetBreakpoints not found."));
			var count=0;
			foreach(var item in all) {
				if(Convert.ToUInt32(Property(item,"Token"),CultureInfo.InvariantCulture)==token && Convert.ToUInt32(Property(item,"Offset"),CultureInfo.InvariantCulture)==offset) count++;
			}
			Write("physical","ok","token="+token+" offset="+offset+" count="+count);
		}

		DbgCodeBreakpoint Breakpoint(int id) => breakpoints.Breakpoints.FirstOrDefault(item=>item.Id==id) ?? throw new InvalidOperationException("Breakpoint "+id+" not found.");

		static object? FindEngine(DbgRuntime runtime) {
			var field=typeof(DbgObject).GetField("dataList",BindingFlags.NonPublic|BindingFlags.Instance);
			if(!(field?.GetValue(runtime) is IEnumerable values)) return null;
			foreach(var value in values) {
				var data=value.GetType().GetField("Item2")?.GetValue(value);
				var engine=data is null ? null : Property(data,"Engine");
				if(engine?.GetType().Name.Contains("DbgEngineImpl")==true) return engine;
			}
			return null;
		}
		static object? Field(object value,string name) {
			for(var type=value.GetType();type is not null;type=type.BaseType) {
				var field=type.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
				if(field is not null) return field.GetValue(value);
			}
			return null;
		}
		static object? Property(object value,string name) => value.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(value);
		static string Flat(Exception ex) => ex.GetType().Name+":"+ex.Message.Replace("\r"," ").Replace("\n"," ");
		void Write(string operation,string status,string detail) {
			var line=DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+"\t"+operation+"\t"+status+"\t"+detail;
			lock(owners) File.AppendAllText(logPath,line+Environment.NewLine,new UTF8Encoding(false));
		}
	}
}
