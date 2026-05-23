using Microsoft.EntityFrameworkCore;
using ReviewBot.Core.Storage;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence;

public sealed class EfCorePrReviewStateStore(
    IDbContextFactory<ReviewBotDbContext> factory,
    TimeProvider clock) : IPrReviewStateStore
{
    public async Task<string?> GetLastShaAsync(
        long installationId, string repoFullName, int pullNumber, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.PrReviewStates
            .AsNoTracking()
            .Where(r => r.InstallationId == installationId
                     && r.RepoFullName == repoFullName
                     && r.PullNumber == pullNumber)
            .Select(r => r.LastSha)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return record;
    }

    public async Task SetLastShaAsync(
        long installationId, string repoFullName, int pullNumber, string sha, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.PrReviewStates
            .FindAsync([installationId, repoFullName, pullNumber], ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.LastSha = sha;
            existing.ReviewedAt = clock.GetUtcNow();
        }
        else
        {
            db.PrReviewStates.Add(new PrReviewStateRecord
            {
                InstallationId = installationId,
                RepoFullName = repoFullName,
                PullNumber = pullNumber,
                LastSha = sha,
                ReviewedAt = clock.GetUtcNow(),
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
