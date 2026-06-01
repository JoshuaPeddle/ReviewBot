using Microsoft.Extensions.Options;

namespace ReviewBot.Api.Tracing;

public static class DependencyInjection
{
    public static IServiceCollection AddReviewTracing(
        this IServiceCollection services,
        Action<TracingOptions>? configure = null)
    {
        var optionsBuilder = services
            .AddOptions<TracingOptions>()
            .BindConfiguration(TracingOptions.SectionName);

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IReviewTraceWriter, JsonReviewTraceWriter>();
        services.AddHostedService<TraceCleanupService>();
        return services;
    }
}
