using ReviewBot.Core.Options;

namespace ReviewBot.Api.Workers;

public sealed class RetryLoop
{
    public async Task RunAsync(RetryOptions options, Func<Task> action)
    {
        if (options.MaxAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAttempts));
        }

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            await action();
        }
    }
}
