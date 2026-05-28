using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Retrieval.Indexing;

namespace ReviewBot.Retrieval.Tests.Indexing;

public sealed class RepoIndexCleanupServiceTests
{
    [Fact]
    public async Task SweepOnceAsyncDeletesRowsOlderThanRetentionFromKnownCacheDirectories()
    {
        var now = new DateTimeOffset(2026, 5, 27, 12, 0, 0, TimeSpan.Zero);
        var first = new FakeRepoIndex(2);
        var second = new FakeRepoIndex(3);
        var factory = new FakeRepoIndexFactory(new Dictionary<string, IRepoIndex>(StringComparer.Ordinal)
        {
            ["/tmp/reviewbot-index-a"] = first,
            ["/tmp/reviewbot-index-b"] = second
        });
        var service = new RepoIndexCleanupService(
            factory,
            new FixedTimeProvider(now),
            NullLogger<RepoIndexCleanupService>.Instance);

        var deleted = await service.SweepOnceAsync();

        deleted.Should().Be(5);
        first.Cutoff.Should().Be(now.AddDays(-30));
        second.Cutoff.Should().Be(now.AddDays(-30));
        factory.CreatedDirectories.Should().Equal("/tmp/reviewbot-index-a", "/tmp/reviewbot-index-b");
    }

    [Fact]
    public async Task SweepOnceAsyncContinuesWhenOneCacheDirectoryFails()
    {
        var healthy = new FakeRepoIndex(4);
        var factory = new FakeRepoIndexFactory(new Dictionary<string, IRepoIndex>(StringComparer.Ordinal)
        {
            ["/tmp/broken"] = new ThrowingRepoIndex(),
            ["/tmp/healthy"] = healthy
        });
        var service = new RepoIndexCleanupService(
            factory,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            NullLogger<RepoIndexCleanupService>.Instance);

        var deleted = await service.SweepOnceAsync();

        deleted.Should().Be(4);
        healthy.Cutoff.Should().Be(DateTimeOffset.UnixEpoch.AddDays(-30));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRepoIndexFactory(IReadOnlyDictionary<string, IRepoIndex> indexes) : IRepoIndexFactory
    {
        public List<string> CreatedDirectories { get; } = [];

        public IRepoIndex Create(string indexCacheDir)
        {
            CreatedDirectories.Add(indexCacheDir);
            return indexes[indexCacheDir];
        }

        public IReadOnlyList<string> GetKnownCacheDirectories() =>
            indexes.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class FakeRepoIndex(int deleteCount) : IRepoIndex
    {
        public DateTimeOffset? Cutoff { get; private set; }

        public Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task IndexChangesAsync(
            RepoIndexRequest request,
            RepoIndexKey baseKey,
            IReadOnlyCollection<string> changedPaths,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> IsIndexedAsync(RepoIndexKey key, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<RepoSymbol>> FindAsync(
            RepoIndexKey key,
            string name,
            RepoSymbolKind? kind = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepoSymbol>>([]);

        public Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        {
            Cutoff = cutoff;
            return Task.FromResult(deleteCount);
        }
    }

    private sealed class ThrowingRepoIndex : IRepoIndex
    {
        public Task IndexAsync(RepoIndexRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task IndexChangesAsync(
            RepoIndexRequest request,
            RepoIndexKey baseKey,
            IReadOnlyCollection<string> changedPaths,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> IsIndexedAsync(RepoIndexKey key, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<RepoSymbol>> FindAsync(
            RepoIndexKey key,
            string name,
            RepoSymbolKind? kind = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepoSymbol>>([]);

        public Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
            throw new InvalidOperationException("cleanup failed");
    }
}
