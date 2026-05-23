using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReviewBot.Core.Idempotency;

namespace ReviewBot.Persistence;

public sealed class EfCoreDeliveryStore(
    IDbContextFactory<ReviewBotDbContext> factory,
    TimeProvider clock,
    ILogger<EfCoreDeliveryStore> logger) : IDeliveryStore
{
    public async Task<bool> TryRecordAsync(string deliveryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var sql = db.Database.IsSqlite()
            ? "INSERT OR IGNORE INTO \"Deliveries\" (\"DeliveryId\", \"ProcessedAt\") VALUES ({0}, {1})"
            : throw new NotSupportedException(
                "Only SQLite is wired in v1. Add the Postgres branch when the provider changes.");

        var rows = await db.Database
            .ExecuteSqlRawAsync(sql, [deliveryId, clock.GetUtcNow()], ct)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            logger.LogInformation("Skipped duplicate GitHub delivery {DeliveryId}", deliveryId);
        }

        return rows == 1;
    }

    public async Task CleanupAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var sql = db.Database.IsSqlite()
            ? "DELETE FROM \"Deliveries\" WHERE \"ProcessedAt\" < {0}"
            : throw new NotSupportedException(
                "Only SQLite is wired in v1. Add the Postgres branch when the provider changes.");
        var deleted = await db.Database
            .ExecuteSqlRawAsync(sql, [olderThan], ct)
            .ConfigureAwait(false);

        logger.LogInformation("Deleted {DeliveryCount} delivery idempotency records older than {OlderThan}", deleted, olderThan);
    }
}
