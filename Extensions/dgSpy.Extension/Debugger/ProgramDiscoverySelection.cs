using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace dgSpy.Extension.Debugger {
	sealed class ProcessDiscoveryCandidate : IDisposable {
		readonly Process? process;
		readonly Func<int>? getId;
		readonly Func<string>? getName;

		public ProcessDiscoveryCandidate(Process process) => this.process=process ?? throw new ArgumentNullException(nameof(process));
		internal ProcessDiscoveryCandidate(Func<int> getId,Func<string> getName) { this.getId=getId; this.getName=getName; }
		public int GetId() => process?.Id ?? getId!();
		public string GetName() => process?.ProcessName ?? getName!();
		public void Dispose() => process?.Dispose();
	}

	sealed class ProcessDiscoverySelection {
		public int[] ProcessIds { get; }
		public int SkippedCandidates { get; }
		public ProcessDiscoverySelection(int[] processIds,int skippedCandidates) { ProcessIds=processIds; SkippedCandidates=skippedCandidates; }
	}

	static class ProgramDiscoverySelector {
		public static ProcessDiscoverySelection Resolve(string[] processNames,int[] processIds) =>
			Resolve(processNames,processIds,Enumerate(processIds));

		internal static ProcessDiscoverySelection Resolve(string[] processNames,int[] processIds,IEnumerable<ProcessDiscoveryCandidate> candidates) {
			if (processNames is null) throw new ArgumentNullException(nameof(processNames));
			if (processIds is null) throw new ArgumentNullException(nameof(processIds));
			if (candidates is null) throw new ArgumentNullException(nameof(candidates));
			var matchers=processNames.Select(CreateMatcher).ToArray();
			var allowedIds=processIds.Length==0 ? null : new HashSet<int>(processIds);
			var result=new HashSet<int>();
			int skipped=0;
			foreach (var candidate in candidates) {
				using (candidate) {
					try {
						var id=candidate.GetId();
						if (allowedIds is not null && !allowedIds.Contains(id)) continue;
						var name=candidate.GetName();
						if (matchers.Any(a=>a.IsMatch(name))) result.Add(id);
					}
					catch (Win32Exception) { skipped++; }
					catch (InvalidOperationException) { skipped++; }
					catch (ArgumentException) { skipped++; }
				}
			}
			return new ProcessDiscoverySelection(result.OrderBy(a=>a).ToArray(),skipped);
		}

		static IEnumerable<ProcessDiscoveryCandidate> Enumerate(int[] processIds) {
			if (processIds.Length==0) return Process.GetProcesses().Select(a=>new ProcessDiscoveryCandidate(a));
			var result=new List<ProcessDiscoveryCandidate>();
			foreach (var id in processIds.Distinct()) {
				try { result.Add(new ProcessDiscoveryCandidate(Process.GetProcessById(id))); }
				catch (ArgumentException) { }
				catch (InvalidOperationException) { }
			}
			return result;
		}

		static Regex CreateMatcher(string value) {
			if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("process_names entries must not be empty.",nameof(value));
			if (value.IndexOfAny(new[]{'\\','/',':'})>=0) throw new ArgumentException("process_names entries are process names, not paths.",nameof(value));
			var pattern=value.Trim();
			if (pattern.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) pattern=pattern.Substring(0,pattern.Length-4);
			var expression="^"+Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"$";
			return new Regex(expression,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
		}
	}

	sealed class ProgramDiscoveryCache<T> where T : class {
		readonly object gate=new object();
		readonly Dictionary<string,T> values=new Dictionary<string,T>();
		long current;
		public long Begin() { lock(gate) { values.Clear(); return ++current; } }
		public bool TryPublish(long generation,IEnumerable<KeyValuePair<string,T>> entries) {
			lock(gate) {
				if (generation!=current) return false;
				values.Clear();
				foreach (var entry in entries) values.Add(entry.Key,entry.Value);
				return true;
			}
		}
		public bool TryGetValue(string id,out T value) { lock(gate) return values.TryGetValue(id,out value!); }
	}
}
