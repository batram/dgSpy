using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace HookLab.Watcher;

internal sealed record WatchControl(bool Paused,IReadOnlySet<string> DisabledProfiles) {
	public static WatchControl Active { get; }=new(false,new HashSet<string>(StringComparer.Ordinal));
}

internal sealed class WatchControlStore {
	readonly string path; readonly object gate=new();
	public WatchControlStore(string? path=null) { this.path=Path.GetFullPath(path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-control.json")); Directory.CreateDirectory(Path.GetDirectoryName(this.path)!); }
	public WatchControl Read() { lock(gate) { if(!File.Exists(path)) return WatchControl.Active; using var document=JsonDocument.Parse(File.ReadAllBytes(path)); var root=document.RootElement; if(root.GetProperty("schemaVersion").GetInt32()!=1) throw new InvalidDataException("Watcher control schema is unsupported."); var disabled=root.GetProperty("disabledProfiles").EnumerateArray().Select(value=>value.GetString()??throw new InvalidDataException("Watcher control contains a null profile ID.")).ToHashSet(StringComparer.Ordinal); return new(root.GetProperty("paused").GetBoolean(),disabled); } }
	public WatchControl Update(bool? paused=null,string? enableProfile=null,string? disableProfile=null) { lock(gate) { var current=Read(); var disabled=current.DisabledProfiles.ToHashSet(StringComparer.Ordinal); if(enableProfile is not null) disabled.Remove(enableProfile); if(disableProfile is not null) disabled.Add(disableProfile); var next=new WatchControl(paused??current.Paused,disabled); Write(next); return next; } }
	void Write(WatchControl value) { var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp"; File.WriteAllText(temporary,JsonSerializer.Serialize(new { schemaVersion=1,paused=value.Paused,disabledProfiles=value.DisabledProfiles.OrderBy(id=>id,StringComparer.Ordinal) }),new UTF8Encoding(false)); File.Move(temporary,path,true); }
}

internal interface IWatchCatalog { CatalogSnapshot Current(); }
internal sealed record CatalogSnapshot(string Generation,IReadOnlyList<WatchDefinition> Definitions,string? Error=null);
internal sealed class StaticWatchCatalog : IWatchCatalog {
	readonly CatalogSnapshot snapshot;
	public StaticWatchCatalog(IReadOnlyList<WatchDefinition> definitions)=>snapshot=new(Generation(definitions),definitions);
	public CatalogSnapshot Current()=>snapshot;
	internal static string Generation(IEnumerable<WatchDefinition> definitions) { using var sha=SHA256.Create(); var text=String.Join("\n",definitions.OrderBy(value=>value.Value.Id,StringComparer.Ordinal).Select(value=>value.Value.Id+":"+value.DefinitionSha256+":"+value.ProfileId)); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(); }
}
internal sealed class ReloadingProfileCatalog : IWatchCatalog {
	readonly string directory; readonly object gate=new(); CatalogSnapshot current; string fingerprint; DateTime nextCheck;
	public ReloadingProfileCatalog(string directory) { this.directory=Path.GetFullPath(directory); var definitions=ProfileCatalog.Load(this.directory); current=new(StaticWatchCatalog.Generation(definitions),definitions); fingerprint=Fingerprint(); }
	public CatalogSnapshot Current() { lock(gate) { if(DateTime.UtcNow<nextCheck) return current; nextCheck=DateTime.UtcNow.AddMilliseconds(500); try { var observed=Fingerprint(); if(observed==fingerprint&&current.Error is null) return current; var definitions=ProfileCatalog.Load(directory); current=new(StaticWatchCatalog.Generation(definitions),definitions); fingerprint=observed; } catch(Exception ex) { current=current with { Error=ex.Message }; } return current; } }
	string Fingerprint() { using var sha=SHA256.Create(); var values=Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).Select(path=>Path.GetRelativePath(directory,path)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n",values)))).ToLowerInvariant(); }
}

internal sealed class WatcherStatusStore {
	readonly string path; readonly object gate=new(); readonly Dictionary<string,object> results=new(StringComparer.Ordinal); readonly DateTime startedUtc=DateTime.UtcNow; readonly long processCreationUtcTicks=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; string lifecycle="starting",discoveryMode="polling",lastDiscoveryTrigger="startup",lastDiscoveryUtc=DateTime.UtcNow.ToString("O"); long subscriptionWakeCount;
	public WatcherStatusStore(string? path=null) { this.path=Path.GetFullPath(path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-status.json")); Directory.CreateDirectory(Path.GetDirectoryName(this.path)!); }
	public void SetLifecycle(string value) { lock(gate) { if(value is not ("running" or "stopping" or "stopped")) throw new ArgumentOutOfRangeException(nameof(value)); lifecycle=value; } }
	public void SetDiscoveryMode(string value) { lock(gate) { if(value is not ("polling" or "subscription+polling")) throw new ArgumentOutOfRangeException(nameof(value)); discoveryMode=value; } }
	public void RecordDiscoveryWake(bool subscription) { lock(gate) { lastDiscoveryTrigger=subscription?"subscription":"polling"; lastDiscoveryUtc=DateTime.UtcNow.ToString("O"); if(subscription) subscriptionWakeCount++; } }
	public void Publish(WatchControl control,CatalogSnapshot catalog,WatchWork? work=null,string? status=null,long? elapsedMs=null,string? message=null) { lock(gate) { if(work is not null&&status is not null) { var completed=work.Value; results[completed.Definition.Value.Id+":"+completed.Process.ProcessId+":"+completed.Process.CreationUtcTicks]=new { definitionId=completed.Definition.Value.Id,profileId=completed.Definition.ProfileId,packageId=completed.Definition.PackageId,processId=completed.Process.ProcessId,processCreationUtcTicks=completed.Process.CreationUtcTicks,status,elapsedMs,message,timestampUtc=DateTime.UtcNow.ToString("O") }; while(results.Count>256) results.Remove(results.Keys.First()); } Write(control,catalog); } }
	void Write(WatchControl control,CatalogSnapshot catalog) { var value=new { schemaVersion=1,watcherProcessId=Environment.ProcessId,watcherProcessCreationUtcTicks=processCreationUtcTicks,startedUtc=startedUtc.ToString("O"),updatedUtc=DateTime.UtcNow.ToString("O"),lifecycle,discoveryMode,lastDiscoveryTrigger,lastDiscoveryUtc,subscriptionWakeCount,paused=control.Paused,disabledProfiles=control.DisabledProfiles.OrderBy(id=>id,StringComparer.Ordinal),catalogGeneration=catalog.Generation,catalogError=catalog.Error,definitions=catalog.Definitions.Select(item=>new { definitionId=item.Value.Id,profileId=item.ProfileId,packageId=item.PackageId,digest=item.DefinitionSha256,notificationPolicy=item.NotificationPolicy,clrReadinessTimeoutMs=item.ClrReadinessTimeoutMs,initializationTimeoutMs=item.InitializationTimeoutMs }),lastResults=results.Values }; AtomicJson(path,value); }
	public static JsonElement? Read(string? path=null,Func<int,long,bool>? identityIsAlive=null) { var full=Path.GetFullPath(path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HookLab","watcher-status.json")); if(!File.Exists(full)) return null; var root=JsonNode.Parse(File.ReadAllBytes(full))?.AsObject()??throw new InvalidDataException("Watcher status JSON is empty."); var processId=root["watcherProcessId"]?.GetValue<int>()??0; var creationTicks=root["watcherProcessCreationUtcTicks"]?.GetValue<long>()??0; var alive=(identityIsAlive??IsAlive)(processId,creationTicks); var recorded=root["lifecycle"]?.GetValue<string>(); root["processAlive"]=alive; root["recordedLifecycle"]=recorded; if(!alive&&recorded is "starting" or "running" or "stopping") root["lifecycle"]="stale"; using var document=JsonDocument.Parse(root.ToJsonString()); return document.RootElement.Clone(); }
	static bool IsAlive(int processId,long creationTicks) { if(processId<=0||creationTicks<=0) return false; try { using var process=Process.GetProcessById(processId); return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==creationTicks; } catch(ArgumentException) { return false; } catch(InvalidOperationException) { return false; } catch(System.ComponentModel.Win32Exception) { return false; } }
	static void AtomicJson(string path,object value) { var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp"; File.WriteAllText(temporary,JsonSerializer.Serialize(value,new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase }),new UTF8Encoding(false)); File.Move(temporary,path,true); }
}
