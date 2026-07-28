using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReviewBot.Retrieval.Indexing;

public sealed class RepoIndexCleanupService(
    IRepoIndexFactory indexFactory,
    TimeProvider clock,
    ILogger<RepoIndexCleanupService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().Subtract(Retention);
        var deleted = 0;

        foreach (var cacheDirectory in indexFactory.GetKnownCacheDirectories())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                deleted += await indexFactory
                    .Create(cacheDirectory)
                    .DeleteUnusedBeforeAsync(cutoff, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Repository index cleanup failed for cache directory {CacheDirectory}; will retry next interval",
                    cacheDirectory);
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Repository index cleanup deleted {DeletedRows} stale index row(s)", deleted);
        }

        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Repository index cleanup service stopped");
        }
    }
}
