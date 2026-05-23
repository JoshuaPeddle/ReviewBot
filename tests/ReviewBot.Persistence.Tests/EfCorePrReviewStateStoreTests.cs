using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewBot.Core.Storage;
using ReviewBot.Persistence;

namespace ReviewBot.Persistence.Tests;

public class EfCorePrReviewStateStoreTests
{
    [Fact]
    public async Task GetLastShaAsyncReturnsNullWhenNoPrStateExists()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        var sha = await store.GetLastShaAsync(1, "owner/repo", 42, CancellationToken.None);

        sha.Should().BeNull();
    }

    [Fact]
    public async Task SetThenGetReturnsStoredSha()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        await store.SetLastShaAsync(1, "owner/repo", 42, "abc123", CancellationToken.None);
        var sha = await store.GetLastShaAsync(1, "owner/repo", 42, CancellationToken.None);

        sha.Should().Be("abc123");
    }

    [Fact]
    public async Task SetTwiceOverwritesSha()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        await store.SetLastShaAsync(1, "owner/repo", 42, "abc123", CancellationToken.None);
        await store.SetLastShaAsync(1, "owner/repo", 42, "def456", CancellationToken.None);
        var sha = await store.GetLastShaAsync(1, "owner/repo", 42, CancellationToken.None);

        sha.Should().Be("def456");
    }

    [Fact]
    public async Task DifferentPrReturnsNull()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        await store.SetLastShaAsync(1, "owner/repo", 42, "abc123", CancellationToken.None);
        var sha = await store.GetLastShaAsync(1, "owner/repo", 99, CancellationToken.None);

        sha.Should().BeNull();
    }

    [Fact]
    public async Task DifferentRepoReturnsNull()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        await store.SetLastShaAsync(1, "owner/repo", 42, "abc123", CancellationToken.None);
        var sha = await store.GetLastShaAsync(1, "other/repo", 42, CancellationToken.None);

        sha.Should().BeNull();
    }

    [Fact]
    public async Task DifferentInstallationReturnsNull()
    {
        await using var fixture = await PrReviewStateFixture.CreateAsync();
        var store = fixture.CreateStore();

        await store.SetLastShaAsync(1, "owner/repo", 42, "abc123", CancellationToken.None);
        var sha = await store.GetLastShaAsync(2, "owner/repo", 42, CancellationToken.None);

        sha.Should().BeNull();
    }

    [Fact]
    public async Task MigrationsCreatePrReviewStateSchema()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ReviewBotDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new ReviewBotDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var factory = new PrReviewStateFixture.TestDbContextFactory(options);
        var store = new EfCorePrReviewStateStore(factory, TimeProvider.System);

        await store.SetLastShaAsync(1, "owner/repo", 1, "sha-after-migration", CancellationToken.None);
        var sha = await store.GetLastShaAsync(1, "owner/repo", 1, CancellationToken.None);

        sha.Should().Be("sha-after-migration");
    }

    private sealed class PrReviewStateFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ReviewBotDbContext> options;
        private readonly TestDbContextFactory factory;

        private PrReviewStateFixture(SqliteConnection connection)
        {
            this.connection = connection;
            options = new DbContextOptionsBuilder<ReviewBotDbContext>()
                .UseSqlite(connection)
                .Options;
            factory = new TestDbContextFactory(options);
        }

        public static async Task<PrReviewStateFixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var fixture = new PrReviewStateFixture(connection);
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public ReviewBotDbContext CreateContext() => new(options);

        public EfCorePrReviewStateStore CreateStore(TimeProvider? clock = null) =>
            new(factory, clock ?? TimeProvider.System);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }

        public sealed class TestDbContextFactory(DbContextOptions<ReviewBotDbContext> options)
            : IDbContextFactory<ReviewBotDbContext>
        {
            public ReviewBotDbContext CreateDbContext() => new(options);

            public Task<ReviewBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }
    }
}
