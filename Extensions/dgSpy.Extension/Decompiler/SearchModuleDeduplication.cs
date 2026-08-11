using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace dgSpy.Extension {
	/// <summary>De-duplicates metadata views of the same assembly without collapsing unrelated
	/// assemblies that merely share a display name.</summary>
	sealed class SearchModuleDeduplication {
		readonly HashSet<object> instances=new HashSet<object>(ReferenceComparer.Instance);
		readonly HashSet<Guid> mvids=new HashSet<Guid>();
		readonly HashSet<string> paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> assemblyIdentities=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public bool TryAdd(object instance,Guid? mvid,string? path,string? assemblyIdentity) {
			var normalizedPath=string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path!);
			var normalizedIdentity=string.IsNullOrWhiteSpace(assemblyIdentity) ? null : assemblyIdentity!.Trim();
			if (instances.Contains(instance)) return false;
			if (mvid.HasValue && mvid.Value!=Guid.Empty && mvids.Contains(mvid.Value)) return false;
			if (normalizedPath is not null && paths.Contains(normalizedPath)) return false;
			if (normalizedIdentity is not null && assemblyIdentities.Contains(normalizedIdentity)) return false;
			instances.Add(instance);
			if (mvid.HasValue && mvid.Value!=Guid.Empty) mvids.Add(mvid.Value);
			if (normalizedPath is not null) paths.Add(normalizedPath);
			if (normalizedIdentity is not null) assemblyIdentities.Add(normalizedIdentity);
			return true;
		}

		static string NormalizePath(string path) {
			try { return Path.GetFullPath(path); } catch { return path; }
		}

		sealed class ReferenceComparer : IEqualityComparer<object> {
			public static readonly ReferenceComparer Instance=new ReferenceComparer();
			public new bool Equals(object? x,object? y)=>ReferenceEquals(x,y);
			public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
		}
	}
}
