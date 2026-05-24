using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReviewBot.Core.Idempotency;

namespace ReviewBot.Persistence;

public sealed class DeliveryStoreCleanupService(
    IDeliveryStore store,
    TimeProvider clock,
    ILogger<DeliveryStoreCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await store.CleanupAsync(clock.GetUtcNow().AddDays(-30), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Delivery cleanup failed; will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Delivery cleanup service stopped");
        }
    }
}
