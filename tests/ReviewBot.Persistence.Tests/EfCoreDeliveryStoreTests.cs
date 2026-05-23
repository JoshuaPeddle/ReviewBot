using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewBot.Persistence;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence.Tests;

public class EfCoreDeliveryStoreTests
{
    [Fact]
    public async Task TryRecordAsyncReturnsTrueForNewDeliveryAndStoresOneRow()
    {
        await using var fixture = await SqliteDeliveryStoreFixture.CreateAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero));
        var store = fixture.CreateStore(clock);

        var recorded = await store.TryRecordAsync("delivery-1", CancellationToken.None);

        recorded.Should().BeTrue();
        await using var db = fixture.CreateContext();
        var row = await db.Deliveries.SingleAsync();
        row.DeliveryId.Should().Be("delivery-1");
        row.ProcessedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task TryRecordAsyncReturnsFalseForDuplicateDelivery()
    {
        await using var fixture = await SqliteDeliveryStoreFixture.CreateAsync();
        var store = fixture.CreateStore();

        var first = await store.TryRecordAsync("delivery-1", CancellationToken.None);
        var second = await store.TryRecordAsync("delivery-1", CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
        await using var db = fixture.CreateContext();
        (await db.Deliveries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TryRecordAsyncIsAtomicAcrossConcurrentDuplicateCalls()
    {
        await using var fixture = await SqliteDeliveryStoreFixture.CreateAsync();
        var store = fixture.CreateStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => store.TryRecordAsync("delivery-concurrent", CancellationToken.None)));

        results.Count(recorded => recorded).Should().Be(1);
        await using var db = fixture.CreateContext();
        (await db.Deliveries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CleanupAsyncRemovesOldRowsAndKeepsNewerRows()
    {
        await using var fixture = await SqliteDeliveryStoreFixture.CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            db.Deliveries.AddRange(
                new DeliveryRecord
                {
                    DeliveryId = "old",
                    ProcessedAt = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                },
                new DeliveryRecord
                {
                    DeliveryId = "new",
                    ProcessedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
                });
            await db.SaveChangesAsync();
        }

        var store = fixture.CreateStore();
        await store.CleanupAsync(new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        await using var assertionDb = fixture.CreateContext();
        var remainingIds = await assertionDb.Deliveries
            .OrderBy(delivery => delivery.DeliveryId)
            .Select(delivery => delivery.DeliveryId)
            .ToListAsync();
        remainingIds.Should().Equal("new");
    }

    [Fact]
    public async Task MigrationsCreateDeliverySchema()
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

        var factory = new TestDbContextFactory(options);
        var store = new EfCoreDeliveryStore(factory, TimeProvider.System, NullLogger<EfCoreDeliveryStore>.Instance);

        var recorded = await store.TryRecordAsync("delivery-after-migration", CancellationToken.None);

        recorded.Should().BeTrue();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SqliteDeliveryStoreFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ReviewBotDbContext> options;
        private readonly TestDbContextFactory factory;

        private SqliteDeliveryStoreFixture(SqliteConnection connection)
        {
            this.connection = connection;
            options = new DbContextOptionsBuilder<ReviewBotDbContext>()
                .UseSqlite(connection)
                .Options;
            factory = new TestDbContextFactory(options);
        }

        public static async Task<SqliteDeliveryStoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var fixture = new SqliteDeliveryStoreFixture(connection);
            await using var db = fixture.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public ReviewBotDbContext CreateContext() => new(options);

        public EfCoreDeliveryStore CreateStore(TimeProvider? clock = null) =>
            new(factory, clock ?? TimeProvider.System, NullLogger<EfCoreDeliveryStore>.Instance);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ReviewBotDbContext> options)
        : IDbContextFactory<ReviewBotDbContext>
    {
        public ReviewBotDbContext CreateDbContext() => new(options);

        public Task<ReviewBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
