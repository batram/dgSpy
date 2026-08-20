using HookLab.Injector;
using Xunit;

/// <summary>
/// Preserving a failed initialization's staging is right - it is the only record of what a target was
/// offered - but nothing ever removed one, so the machine-wide root grew by a directory per failure and
/// never shrank. Thirty-five accumulated on one developer machine in a single afternoon of chasing an
/// intermittent timeout.
///
/// These cases pin both halves of the bargain: old evidence goes, recent evidence stays, and a
/// directory that cannot be removed does not become an exception on the path of the initialization
/// that happens to be starting.
/// </summary>
public sealed class ExchangeAreaPruningTests : IDisposable {
	readonly string root = Path.Combine(Path.GetTempPath(), "dgspy-exchange-prune-" + Guid.NewGuid().ToString("N"));

	public ExchangeAreaPruningTests() => Directory.CreateDirectory(root);
	public void Dispose() { try { Directory.Delete(root, true); } catch { } }

	string Aged(string name, TimeSpan age) {
		var path = Path.Combine(root, name);
		Directory.CreateDirectory(path);
		File.WriteAllText(Path.Combine(path, "completion.txt"), "status=ok\n");
		Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
		return path;
	}

	[Fact]
	public void Evidence_older_than_the_retention_is_removed() {
		var stale = Aged("stale", ExchangeArea.PreservedRetention + TimeSpan.FromDays(1));
		ExchangeArea.PrunePreserved(root, DateTime.UtcNow - ExchangeArea.PreservedRetention);
		Assert.False(Directory.Exists(stale));
	}

	[Fact]
	public void Evidence_inside_the_retention_is_kept() {
		// The point of preserving at all. A prune that took this would delete the failure someone is
		// still reading.
		var recent = Aged("recent", TimeSpan.FromHours(1));
		ExchangeArea.PrunePreserved(root, DateTime.UtcNow - ExchangeArea.PreservedRetention);
		Assert.True(Directory.Exists(recent));
		Assert.True(File.Exists(Path.Combine(recent, "completion.txt")));
	}

	[Fact]
	public void A_directory_that_cannot_be_removed_does_not_fail_the_prune() {
		var stale = Aged("locked", ExchangeArea.PreservedRetention + TimeSpan.FromDays(1));
		var keeper = Aged("also-stale", ExchangeArea.PreservedRetention + TimeSpan.FromDays(1));
		// An open handle is the same obstacle as another account's directory in the machine-wide root:
		// not this process's to remove, and not a reason to fail the initialization that is starting.
		using (File.Open(Path.Combine(stale, "completion.txt"), FileMode.Open, FileAccess.Read, FileShare.None)) {
			ExchangeArea.PrunePreserved(root, DateTime.UtcNow - ExchangeArea.PreservedRetention);
			Assert.True(Directory.Exists(stale));
		}
		// And the obstacle does not stop the rest of the sweep.
		Assert.False(Directory.Exists(keeper));
	}

	[Fact]
	public void A_missing_or_unnamed_root_is_not_an_error() {
		ExchangeArea.PrunePreserved(Path.Combine(root, "no-such-directory"), DateTime.UtcNow);
		ExchangeArea.PrunePreserved(null, DateTime.UtcNow);
		ExchangeArea.PrunePreserved("", DateTime.UtcNow);
	}
}
