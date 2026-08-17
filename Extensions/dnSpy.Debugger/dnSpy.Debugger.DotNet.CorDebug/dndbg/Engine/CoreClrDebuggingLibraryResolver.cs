/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace dndbg.Engine {
	readonly struct CoreClrDebuggingLibraryIdentity {
		public string FileName { get; }
		public int TimeStamp { get; }
		public int SizeOfImage { get; }
		public int ArchitectureBits { get; }

		public CoreClrDebuggingLibraryIdentity(string fileName, int timeStamp, int sizeOfImage, int architectureBits) {
			FileName = Path.GetFileName(fileName ?? throw new ArgumentNullException(nameof(fileName)));
			TimeStamp = timeStamp;
			SizeOfImage = sizeOfImage;
			ArchitectureBits = architectureBits;
		}
	}

	readonly struct CoreClrDebuggingLibraryCandidate {
		public string Source { get; }
		public string Path { get; }

		public CoreClrDebuggingLibraryCandidate(string source, string path) {
			Source = source;
			Path = path;
		}
	}

	sealed class CoreClrDebuggingLibraryResolution {
		public string? ResolvedPath { get; }
		public IReadOnlyList<string> Trace { get; }

		public CoreClrDebuggingLibraryResolution(string? resolvedPath, IReadOnlyList<string> trace) {
			ResolvedPath = resolvedPath;
			Trace = trace;
		}
	}

	/// <summary>
	/// Resolves the exact Windows debugger component requested by ICLRDebuggingLibraryProvider3.
	/// Candidate stores are hints only; the PE identity supplied by CoreCLR is authoritative.
	/// </summary>
	sealed class CoreClrDebuggingLibraryResolver {
		readonly Func<string, IEnumerable<CoreClrDebuggingLibraryCandidate>> getCandidates;

		public CoreClrDebuggingLibraryResolver(string runtimeModulePath)
			: this(libraryName => EnumerateDefaultCandidates(runtimeModulePath, libraryName)) {
		}

		internal CoreClrDebuggingLibraryResolver(Func<string, IEnumerable<CoreClrDebuggingLibraryCandidate>> getCandidates) =>
			this.getCandidates = getCandidates ?? throw new ArgumentNullException(nameof(getCandidates));

		public CoreClrDebuggingLibraryResolution Resolve(CoreClrDebuggingLibraryIdentity identity) {
			var trace = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var candidate in getCandidates(identity.FileName)) {
				string fullPath;
				try {
					fullPath = Path.GetFullPath(candidate.Path);
				}
				catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) {
					trace.Add($"{candidate.Source}: invalid path '{candidate.Path}'");
					continue;
				}
				if (!seen.Add(fullPath))
					continue;
				if (!string.Equals(Path.GetFileName(fullPath), identity.FileName, StringComparison.OrdinalIgnoreCase)) {
					trace.Add($"{candidate.Source}: rejected '{fullPath}' (basename mismatch)");
					continue;
				}
				if (!File.Exists(fullPath)) {
					trace.Add($"{candidate.Source}: missing '{fullPath}'");
					continue;
				}
				if (!TryReadPeIdentity(fullPath, out int timeStamp, out int sizeOfImage, out int architectureBits, out string error)) {
					trace.Add($"{candidate.Source}: rejected '{fullPath}' ({error})");
					continue;
				}
				if (architectureBits != identity.ArchitectureBits) {
					trace.Add($"{candidate.Source}: rejected '{fullPath}' (architecture {architectureBits}, expected {identity.ArchitectureBits})");
					continue;
				}
				if (timeStamp != identity.TimeStamp || sizeOfImage != identity.SizeOfImage) {
					trace.Add($"{candidate.Source}: rejected '{fullPath}' (PE identity timestamp=0x{timeStamp:X8}, size=0x{sizeOfImage:X8}; expected timestamp=0x{identity.TimeStamp:X8}, size=0x{identity.SizeOfImage:X8})");
					continue;
				}
				trace.Add($"{candidate.Source}: resolved '{fullPath}'");
				return new CoreClrDebuggingLibraryResolution(fullPath, trace);
			}
			return new CoreClrDebuggingLibraryResolution(null, trace);
		}

		static IEnumerable<CoreClrDebuggingLibraryCandidate> EnumerateDefaultCandidates(string runtimeModulePath, string libraryName) {
			var runtimeDirectory = Path.GetDirectoryName(runtimeModulePath);
			if (!string.IsNullOrEmpty(runtimeDirectory))
				yield return new CoreClrDebuggingLibraryCandidate("runtime-adjacent", Path.Combine(runtimeDirectory!, libraryName));

			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (!string.IsNullOrEmpty(localAppData)) {
				var cacheRoot = Path.Combine(localAppData, "dgSpy", "coreclr-debugger-components");
				foreach (var directory in EnumerateDirectories(cacheRoot))
					yield return new CoreClrDebuggingLibraryCandidate("private-cache", Path.Combine(directory, libraryName));
			}

			foreach (var root in EnumerateDotNetRoots()) {
				var runtimeRoot = Path.Combine(root, "shared", "Microsoft.NETCore.App");
				foreach (var directory in EnumerateDirectories(runtimeRoot))
					yield return new CoreClrDebuggingLibraryCandidate("installed-runtime", Path.Combine(directory, libraryName));
			}
		}

		static IEnumerable<string> EnumerateDotNetRoots() {
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
			if (!string.IsNullOrEmpty(configuredRoot) && seen.Add(configuredRoot!))
				yield return configuredRoot!;
			var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			if (!string.IsNullOrEmpty(programFiles)) {
				var installedRoot = Path.Combine(programFiles, "dotnet");
				if (seen.Add(installedRoot))
					yield return installedRoot;
			}
		}

		static IEnumerable<string> EnumerateDirectories(string root) {
			yield return root;
			string[] directories;
			try {
				directories = Directory.GetDirectories(root);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				yield break;
			}
			Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
			foreach (var directory in directories)
				yield return directory;
		}

		internal static bool TryReadPeIdentity(string fileName, out int timeStamp, out int sizeOfImage, out int architectureBits, out string error) {
			timeStamp = sizeOfImage = architectureBits = 0;
			error = string.Empty;
			try {
				using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
				using (var reader = new BinaryReader(stream)) {
					if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) {
						error = "not a PE image";
						return false;
					}
					stream.Position = 0x3C;
					int peOffset = reader.ReadInt32();
					if (peOffset < 0 || peOffset > stream.Length - 0x60) {
						error = "invalid PE header offset";
						return false;
					}
					stream.Position = peOffset;
					if (reader.ReadUInt32() != 0x00004550) {
						error = "invalid PE signature";
						return false;
					}
					ushort machine = reader.ReadUInt16();
					stream.Position += 2;
					timeStamp = reader.ReadInt32();
					stream.Position += 8;
					ushort optionalHeaderSize = reader.ReadUInt16();
					stream.Position += 2;
					long optionalHeaderOffset = stream.Position;
					if (optionalHeaderSize < 60 || optionalHeaderOffset > stream.Length - optionalHeaderSize) {
						error = "truncated optional header";
						return false;
					}
					ushort magic = reader.ReadUInt16();
					architectureBits = machine == 0x8664 && magic == 0x020B ? 64 : machine == 0x014C && magic == 0x010B ? 32 : 0;
					if (architectureBits == 0) {
						error = $"unsupported PE machine 0x{machine:X4}";
						return false;
					}
					stream.Position = optionalHeaderOffset + 56;
					sizeOfImage = reader.ReadInt32();
					return true;
				}
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = ex.Message;
				return false;
			}
		}
	}
}
