using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReviewBot.Core.Jobs;
using ReviewBot.Persistence.Entities;

namespace ReviewBot.Persistence;

public sealed class EfCoreReviewJobQueue(
    IDbContextFactory<ReviewBotDbContext> factory,
    TimeProvider clock,
    ILogger<EfCoreReviewJobQueue> logger) : IReviewJobQueue
{
    private const string QueuedStatus = "queued";
    private const string RunningStatus = "running";
    private const string RetryStatus = "retry";
    private const string CompletedStatus = "completed";
    private const string DeadLetterStatus = "dead_letter";
    private const int MaxAttempts = 3;
    private const int MaxErrorLength = 4000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // A crashed worker should release work quickly enough to preserve webhook/check UX.
    // Active workers renew every minute, leaving a wide margin for brief SQLite contention.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LeaseRecoveryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TerminalJobRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan TerminalCleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReviewCommentCooldown = TimeSpan.FromMinutes(1);

    private int activeReader;

    public async ValueTask<bool> TryEnqueueAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = clock.GetUtcNow();
        var cooldownStart = now - ReviewCommentCooldown;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // GitHub keeps X-GitHub-Delivery stable across manual redeliveries. A matching
        // dead-lettered delivery is therefore an operator-requested retry, not a duplicate.
        // Match every immutable payload field so a reused delivery ID cannot revive a
        // different event.
        var revived = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "ReviewJobs"
                 SET "Status" = {QueuedStatus},
                     "AttemptCount" = 0,
                     "CreatedAt" = {now},
                     "AvailableAt" = {now},
                     "StartedAt" = NULL,
                     "CompletedAt" = NULL,
                     "LeaseExpiresAt" = NULL,
                     "LeaseToken" = NULL,
                     "LastError" = NULL
                 WHERE "DeliveryId" = {job.DeliveryId}
                   AND "Status" = {DeadLetterStatus}
                   AND "InstallationId" = {job.InstallationId}
                   AND "Owner" = {job.Owner}
                   AND "Repo" = {job.Repo}
                   AND "PrNumber" = {job.PrNumber}
                   AND "HeadSha" IS {job.HeadSha}
                   AND "Reason" = {job.Reason}
                 """,
                ct)
            .ConfigureAwait(false);

        if (revived == 1)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Revived dead-lettered review delivery {DeliveryId} for {Owner}/{Repo}#{PrNumber}",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
            return true;
        }

        const string insertSql = """
            INSERT OR IGNORE INTO "ReviewJobs"
                ("DeliveryId", "InstallationId", "Owner", "Repo", "PrNumber", "HeadSha", "Reason",
                 "Status", "AttemptCount", "CreatedAt", "AvailableAt")
            SELECT {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 0, {8}, {8}
            WHERE NOT EXISTS (
                SELECT 1
                FROM "ReviewJobs"
                WHERE "InstallationId" = {1}
                  AND "Owner" = {2}
                  AND "Repo" = {3}
                  AND "PrNumber" = {4}
                  AND (
                    ({6} = 'review_comment'
                     AND "Reason" = 'review_comment'
                     AND "CreatedAt" >= {9}
                     AND "Status" <> 'dead_letter')
                    OR
                    ({6} <> 'review_comment'
                     AND "Reason" = {6}
                     AND "HeadSha" IS {5}
                     AND "Status" IN ('queued', 'retry', 'running'))
                  )
            )
            """;

        var inserted = await db.Database.ExecuteSqlRawAsync(
                insertSql,
                [
                    job.DeliveryId,
                    job.InstallationId,
                    job.Owner,
                    job.Repo,
                    job.PrNumber,
                    new SqliteParameter("headSha", (object?)job.HeadSha ?? DBNull.Value),
                    job.Reason,
                    QueuedStatus,
                    now,
                    cooldownStart
                ],
                ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (inserted == 0)
        {
            logger.LogInformation(
                "Skipped duplicate or rate-limited review delivery {DeliveryId} for {Owner}/{Repo}#{PrNumber}",
                job.DeliveryId,
                job.Owner,
                job.Repo,
                job.PrNumber);
        }

        return inserted == 1;
    }

    public async IAsyncEnumerable<ReviewJob> DequeueAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
        {
            throw new InvalidOperationException("EfCoreReviewJobQueue supports a single active reader.");
        }

        try
        {
            var nextLeaseRecoveryAt = DateTimeOffset.MinValue;
            var nextTerminalCleanupAt = DateTimeOffset.MinValue;
            while (!ct.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                if (now >= nextTerminalCleanupAt)
                {
                    await CleanupTerminalJobsAsync(now - TerminalJobRetention, ct).ConfigureAwait(false);
                    nextTerminalCleanupAt = now + TerminalCleanupInterval;
                }

                if (now >= nextLeaseRecoveryAt)
                {
                    await RecoverExpiredJobsAsync(now, ct).ConfigureAwait(false);
                    nextLeaseRecoveryAt = now + LeaseRecoveryInterval;
                }

                var job = await TryClaimNextAsync(ct).ConfigureAwait(false);
                if (job is not null)
                {
                    yield return job;
                    continue;
                }

                await Task.Delay(PollInterval, clock, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref activeReader, 0);
        }
    }

    public async ValueTask<bool> CompleteAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.LeaseToken))
        {
            logger.LogWarning("Ignored completion for unowned review delivery {DeliveryId}", job.DeliveryId);
            return false;
        }

        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.ReviewJobs
            .Where(record =>
                record.DeliveryId == job.DeliveryId &&
                record.Status == RunningStatus &&
                record.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.Status, CompletedStatus)
                .SetProperty(record => record.CompletedAt, now)
                .SetProperty(record => record.LeaseToken, (string?)null)
                .SetProperty(record => record.LeaseExpiresAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            logger.LogWarning(
                "Ignored completion for review delivery {DeliveryId} because its lease is no longer owned",
                job.DeliveryId);
        }

        return updated == 1;
    }

    public async ValueTask<bool> ReleaseAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.LeaseToken))
        {
            logger.LogWarning("Ignored release for unowned review delivery {DeliveryId}", job.DeliveryId);
            return false;
        }

        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Refund the attempt. A worker shutting down cleanly has learned nothing about
        // whether the job can succeed, so charging it would let three ordinary deploys
        // dead-letter a review that never actually failed.
        var updated = await db.ReviewJobs
            .Where(record =>
                record.DeliveryId == job.DeliveryId &&
                record.Status == RunningStatus &&
                record.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(record => record.Status, QueuedStatus)
                .SetProperty(record => record.AvailableAt, now)
                .SetProperty(record => record.AttemptCount, record => Math.Max(0, record.AttemptCount - 1))
                .SetProperty(record => record.StartedAt, (DateTimeOffset?)null)
                .SetProperty(record => record.LeaseToken, (string?)null)
                .SetProperty(record => record.LeaseExpiresAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            logger.LogWarning(
                "Ignored release for review delivery {DeliveryId} because its lease is no longer owned",
                job.DeliveryId);
            return false;
        }

        logger.LogInformation(
            "Released review delivery {DeliveryId} back to the queue without charging an attempt",
            job.DeliveryId);
        return true;
    }

    public async ValueTask<ReviewJobFailureDisposition> FailAsync(
        ReviewJob job,
        string error,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);
        if (string.IsNullOrWhiteSpace(job.LeaseToken))
        {
            logger.LogWarning("Ignored failure for unowned review delivery {DeliveryId}", job.DeliveryId);
            return ReviewJobFailureDisposition.LeaseLost;
        }

        var now = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.ReviewJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.DeliveryId == job.DeliveryId &&
                item.Status == RunningStatus &&
                item.LeaseToken == job.LeaseToken, ct)
            .ConfigureAwait(false);
        if (record is null)
        {
            logger.LogWarning(
                "Ignored failure for review delivery {DeliveryId} because its lease is no longer owned",
                job.DeliveryId);
            return ReviewJobFailureDisposition.LeaseLost;
        }

        var lastError = Truncate(error, MaxErrorLength);
        var deadLettered = record.AttemptCount >= MaxAttempts;
        var status = deadLettered ? DeadLetterStatus : RetryStatus;
        var availableAt = deadLettered ? now : now + RetryDelay(record.AttemptCount);
        var completedAt = deadLettered ? now : (DateTimeOffset?)null;
        var updated = await db.ReviewJobs
            .Where(item =>
                item.DeliveryId == job.DeliveryId &&
                item.Status == RunningStatus &&
                item.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, status)
                .SetProperty(item => item.AvailableAt, availableAt)
                .SetProperty(item => item.CompletedAt, completedAt)
                .SetProperty(item => item.LastError, lastError)
                .SetProperty(item => item.LeaseToken, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            logger.LogWarning(
                "Ignored failure for review delivery {DeliveryId} because its lease changed concurrently",
                job.DeliveryId);
            return ReviewJobFailureDisposition.LeaseLost;
        }

        if (deadLettered)
        {
            logger.LogError(
                "Review delivery {DeliveryId} moved to the dead-letter queue after {AttemptCount} attempts: {Error}",
                job.DeliveryId,
                record.AttemptCount,
                lastError);
            return ReviewJobFailureDisposition.DeadLettered;
        }

        logger.LogWarning(
            "Review delivery {DeliveryId} will retry after attempt {AttemptCount} at {AvailableAt}",
            job.DeliveryId,
            record.AttemptCount,
            availableAt);
        return ReviewJobFailureDisposition.RetryScheduled;
    }

    public async ValueTask<bool> RenewLeaseAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.LeaseToken))
        {
            return false;
        }

        var leaseExpiresAt = clock.GetUtcNow() + LeaseDuration;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.ReviewJobs
            .Where(record =>
                record.DeliveryId == job.DeliveryId &&
                record.Status == RunningStatus &&
                record.LeaseToken == job.LeaseToken)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(record => record.LeaseExpiresAt, leaseExpiresAt),
                ct)
            .ConfigureAwait(false);
        return updated == 1;
    }


    private async Task RecoverExpiredJobsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // A worker killed mid-review (OOM, SIGKILL, container eviction) never reaches
        // FailAsync, so this is the only place its attempt is ever accounted for. Without
        // a bound, a job that reliably kills its worker would be reclaimed every lease
        // period forever, burning a concurrency slot and LLM spend on every pass.
        //
        // The bound is deliberately one past MaxAttempts. An expired final attempt is
        // still reclaimed once, because that pass is how a worker finalizes the GitHub
        // check the dead process left in progress; only an expiry after that recovery
        // pass is terminal. Total claims are therefore capped at MaxAttempts + 1.
        const string deadLetterSql = """
            UPDATE "ReviewJobs"
            SET "Status" = {0},
                "AvailableAt" = {1},
                "CompletedAt" = {1},
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "LastError" = {2}
            WHERE "Status" = 'running'
              AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= {1})
              AND "AttemptCount" > {3}
            """;
        var deadLettered = await db.Database.ExecuteSqlRawAsync(
                deadLetterSql,
                [
                    DeadLetterStatus,
                    now,
                    $"The review lease expired without a completion after {MaxAttempts + 1} claim(s), "
                        + "including the recovery pass reserved for finalizing external status; "
                        + "the worker is terminating before it can report an outcome.",
                    MaxAttempts
                ],
                ct)
            .ConfigureAwait(false);

        const string recoverSql = """
            UPDATE "ReviewJobs"
            SET "Status" = {0},
                "AvailableAt" = {1},
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Status" = 'running'
              AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= {1})
              AND "AttemptCount" <= {2}
            """;
        var recovered = await db.Database.ExecuteSqlRawAsync(
                recoverSql,
                [RetryStatus, now, MaxAttempts],
                ct)
            .ConfigureAwait(false);

        if (recovered > 0)
        {
            logger.LogWarning("Recovered {RecoveredCount} expired review lease(s)", recovered);
        }

        if (deadLettered > 0)
        {
            logger.LogError(
                "Moved {DeadLetteredCount} review job(s) to the dead-letter queue after their leases expired on every one of {MaxClaims} claim(s) without a reported outcome",
                deadLettered,
                MaxAttempts + 1);
        }
    }

    private async Task CleanupTerminalJobsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        const string cleanupSql = """
            DELETE FROM "ReviewJobs"
            WHERE "Status" IN ('completed', 'dead_letter')
              AND "CompletedAt" IS NOT NULL
              AND "CompletedAt" < {0}
            """;
        var deleted = await db.Database.ExecuteSqlRawAsync(cleanupSql, [cutoff], ct).ConfigureAwait(false);
        if (deleted > 0)
        {
            logger.LogInformation("Deleted {DeletedCount} terminal review job(s) older than {Cutoff}", deleted, cutoff);
        }
    }

    private async Task<ReviewJob?> TryClaimNextAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now + LeaseDuration;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string claimSql = """
            UPDATE "ReviewJobs"
            SET "Status" = {0},
                "AttemptCount" = "AttemptCount" + 1,
                "StartedAt" = {1},
                "LeaseToken" = {2},
                "LeaseExpiresAt" = {3}
            WHERE "DeliveryId" = (
                SELECT "DeliveryId"
                FROM "ReviewJobs"
                WHERE "Status" IN ('queued', 'retry')
                  AND "AvailableAt" <= {1}
                ORDER BY "AvailableAt", "CreatedAt", "DeliveryId"
                LIMIT 1
            )
            """;
        var claimed = await db.Database.ExecuteSqlRawAsync(
                claimSql,
                [RunningStatus, now, leaseToken, leaseExpiresAt],
                ct)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            return null;
        }

        var record = await db.ReviewJobs
            .AsNoTracking()
            .SingleAsync(item => item.LeaseToken == leaseToken, ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new ReviewJob(
            record.DeliveryId,
            record.InstallationId,
            record.Owner,
            record.Repo,
            record.PrNumber,
            record.HeadSha,
            record.Reason,
            record.LeaseToken);
    }

    private static TimeSpan RetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(2)
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
