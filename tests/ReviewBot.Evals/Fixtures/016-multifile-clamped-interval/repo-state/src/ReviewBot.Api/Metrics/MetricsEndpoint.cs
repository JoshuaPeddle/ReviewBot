namespace ReviewBot.Api.Metrics;

public static class MetricsEndpoint
{
    /// <summary>
    /// Maps the Prometheus scrape endpoint. Values reflect the most recent
    /// flush, so scrape intervals shorter than the flush interval read
    /// repeated values rather than fresher ones.
    /// </summary>
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder routes)
    {
        routes.MapPrometheusScrapingEndpoint("/metrics");
        return routes;
    }
}
