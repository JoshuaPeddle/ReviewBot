using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Tracing;

public sealed class TraceCleanupService(
    IOptions<TracingOptions> options,
    TimeProvider clock,
    ILogger<TraceCleanupService> logger) : BackgroundService
{
    private readonly TracingOptions options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(24), clock, stoppingToken).ConfigureAwait(false);
                if (!options.Enabled)
                {
                    continue;
                }

                try
                {
                    RunCleanup();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Trace cleanup failed; will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Trace cleanup service stopped");
        }
    }

    internal void RunCleanup()
    {
        var tracesDir = options.TracesDir;
        if (!Directory.Exists(tracesDir))
        {
            return;
        }

        var cutoff = clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(options.RetentionDays);
        var deletedByAge = 0;
        var remainingFiles = new List<FileInfo>();

        foreach (var fi in Directory
            .EnumerateFiles(tracesDir, "*.json", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)))
        {
            if (fi.LastWriteTimeUtc < cutoff)
            {
                TryDelete(fi.FullName);
                deletedByAge++;
            }
            else
            {
                remainingFiles.Add(fi);
            }
        }

        var maxBytes = (long)options.MaxDiskMb * 1024 * 1024;
        var totalBytes = remainingFiles.Sum(fi => fi.Length);
        var deletedBySize = 0;

        foreach (var fi in remainingFiles.OrderBy(fi => fi.LastWriteTimeUtc))
        {
            if (totalBytes <= maxBytes)
            {
                break;
            }

            TryDelete(fi.FullName);
            totalBytes -= fi.Length;
            deletedBySize++;
        }

        if (deletedByAge > 0 || deletedBySize > 0)
        {
            logger.LogInformation(
                "Trace cleanup: deleted {ByAge} expired file(s) and {BySize} file(s) for disk cap",
                deletedByAge,
                deletedBySize);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete trace file {Path}", path);
        }
    }
}
