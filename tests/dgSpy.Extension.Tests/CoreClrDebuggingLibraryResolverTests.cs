using dndbg.Engine;
using Xunit;

namespace dgSpy.Extension.Tests;

public sealed class CoreClrDebuggingLibraryResolverTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "dgspy-coreclr-resolver-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public void Selects_exact_identity_after_rejecting_mismatch() {
		var wrong = WritePe("adjacent/mscordbi.dll", 0x11111111, 0x22000, 64);
		var exact = WritePe("runtime/mscordbi.dll", 0x22222222, 0x33000, 64);
		var resolver = Resolver(("runtime-adjacent", wrong), ("installed-runtime", exact));

		var result = resolver.Resolve(Identity(exact));

		Assert.Equal(Path.GetFullPath(exact), result.ResolvedPath);
		Assert.Contains(result.Trace, line => line.Contains("PE identity", StringComparison.Ordinal));
		Assert.Contains(result.Trace, line => line.StartsWith("installed-runtime: resolved", StringComparison.Ordinal));
	}

	[Fact]
	public void Adjacent_exact_match_wins_before_cache_and_runtime() {
		var adjacent = WritePe("adjacent/mscordaccore.dll", 1, 0x2000, 64);
		var cache = WritePe("cache/mscordaccore.dll", 1, 0x2000, 64);
		var runtime = WritePe("runtime/mscordaccore.dll", 1, 0x2000, 64);
		var result = Resolver(("runtime-adjacent", adjacent), ("private-cache", cache), ("installed-runtime", runtime)).Resolve(Identity(adjacent));

		Assert.Equal(Path.GetFullPath(adjacent), result.ResolvedPath);
		Assert.Single(result.Trace);
	}

	[Fact]
	public void Cache_exact_match_wins_after_adjacent_miss() {
		var missing = Path.Combine(root, "adjacent", "mscordbi.dll");
		var cache = WritePe("cache/mscordbi.dll", 2, 0x3000, 64);
		var runtime = WritePe("runtime/mscordbi.dll", 2, 0x3000, 64);
		var result = Resolver(("runtime-adjacent", missing), ("private-cache", cache), ("installed-runtime", runtime)).Resolve(Identity(cache));

		Assert.Equal(Path.GetFullPath(cache), result.ResolvedPath);
		Assert.Equal(2, result.Trace.Count);
	}

	[Fact]
	public void Rejects_wrong_architecture() {
		var x86 = WritePe("runtime/mscordbi.dll", 3, 0x4000, 32);
		var result = Resolver(("installed-runtime", x86)).Resolve(new CoreClrDebuggingLibraryIdentity("mscordbi.dll", 3, 0x4000, 64));

		Assert.Null(result.ResolvedPath);
		Assert.Contains("architecture 32, expected 64", Assert.Single(result.Trace), StringComparison.Ordinal);
	}

	[Fact]
	public void Missing_local_only_resolution_reports_every_source() {
		var result = Resolver(
			("runtime-adjacent", Path.Combine(root, "adjacent", "mscordbi.dll")),
			("private-cache", Path.Combine(root, "cache", "mscordbi.dll")),
			("installed-runtime", Path.Combine(root, "runtime", "mscordbi.dll")))
			.Resolve(new CoreClrDebuggingLibraryIdentity("mscordbi.dll", 4, 0x5000, 64));

		Assert.Null(result.ResolvedPath);
		Assert.Equal(3, result.Trace.Count);
		Assert.All(result.Trace, line => Assert.Contains(": missing '", line, StringComparison.Ordinal));
	}

	[Fact]
	public void Partial_cache_file_is_never_returned() {
		var partial = Path.Combine(root, "cache", "mscordaccore.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
		File.WriteAllBytes(partial, new byte[32]);

		var result = Resolver(("private-cache", partial)).Resolve(new CoreClrDebuggingLibraryIdentity("mscordaccore.dll", 5, 0x6000, 64));

		Assert.Null(result.ResolvedPath);
		Assert.Contains("not a PE image", Assert.Single(result.Trace), StringComparison.Ordinal);
	}

	CoreClrDebuggingLibraryResolver Resolver(params (string Source, string Path)[] candidates) =>
		new(_ => candidates.Select(candidate => new CoreClrDebuggingLibraryCandidate(candidate.Source, candidate.Path)));

	CoreClrDebuggingLibraryIdentity Identity(string path) {
		Assert.True(CoreClrDebuggingLibraryResolver.TryReadPeIdentity(path, out var timestamp, out var size, out var bits, out var error), error);
		return new CoreClrDebuggingLibraryIdentity(Path.GetFileName(path), timestamp, size, bits);
	}

	string WritePe(string relativePath, int timestamp, int sizeOfImage, int bits) {
		var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var bytes = new byte[0x200];
		WriteUInt16(bytes, 0, 0x5A4D);
		WriteInt32(bytes, 0x3C, 0x80);
		WriteInt32(bytes, 0x80, 0x00004550);
		WriteUInt16(bytes, 0x84, bits == 64 ? 0x8664 : 0x014C);
		WriteUInt16(bytes, 0x86, 1);
		WriteInt32(bytes, 0x88, timestamp);
		WriteUInt16(bytes, 0x94, bits == 64 ? 0xF0 : 0xE0);
		WriteUInt16(bytes, 0x98, bits == 64 ? 0x020B : 0x010B);
		WriteInt32(bytes, 0x98 + 56, sizeOfImage);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	static void WriteUInt16(byte[] bytes, int offset, int value) {
		bytes[offset] = (byte)value;
		bytes[offset + 1] = (byte)(value >> 8);
	}

	static void WriteInt32(byte[] bytes, int offset, int value) {
		bytes[offset] = (byte)value;
		bytes[offset + 1] = (byte)(value >> 8);
		bytes[offset + 2] = (byte)(value >> 16);
		bytes[offset + 3] = (byte)(value >> 24);
	}

	public void Dispose() {
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}
}
