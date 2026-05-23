using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ReviewBot.Api;

public static class ReviewBotHttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddReviewBotHttpResilience(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromMilliseconds(100);
            options.Retry.BackoffType = DelayBackoffType.Exponential;
        });

        return builder;
    }
}
