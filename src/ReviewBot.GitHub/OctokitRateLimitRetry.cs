using Microsoft.Extensions.Logging;
using Octokit;

namespace ReviewBot.GitHub;

internal static class OctokitRateLimitRetry
{
    private const int MaxRetryAttempts = 1;

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);

        for (var retryAttempt = 0; ; retryAttempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (RateLimitExceededException ex) when (retryAttempt < MaxRetryAttempts)
            {
                var delay = ex.GetRetryAfterTimeSpan();
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                logger.LogWarning(
                    ex,
                    "GitHub rate limit exceeded; retrying after {RetryDelay}",
                    delay);
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            }
        }
    }
}
