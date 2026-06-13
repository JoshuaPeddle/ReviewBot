using ReviewBot.Core.Metrics;

namespace ReviewBot.Api.Metrics;

public static class MetricsStartup
{
    public static void ConfigureMetrics(this IServiceProvider services)
    {
        var flusher = services.GetRequiredService<MetricsFlusher>();
        // Refresh dashboards faster now that reviews finish in under a minute.
        flusher.Configure(TimeSpan.FromSeconds(30));
        flusher.Start();
    }
}
