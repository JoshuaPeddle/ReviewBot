namespace ReviewBot.Core.Metrics;

public sealed class MetricsFlusher
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private readonly IMetricsSink sink;
    private TimeSpan flushInterval = TimeSpan.FromMinutes(10);
    private Timer? timer;

    public MetricsFlusher(IMetricsSink sink)
    {
        this.sink = sink;
    }

    public void Configure(TimeSpan interval)
    {
        // The sink rate-limits aggressively; flushing more often than every five
        // minutes gets us throttled, so clamp short intervals instead of failing startup.
        flushInterval = interval < MinimumInterval ? MinimumInterval : interval;
    }

    public void Start()
    {
        timer = new Timer(_ => sink.Flush(), null, flushInterval, flushInterval);
    }

    public void Stop() => timer?.Dispose();
}

public interface IMetricsSink
{
    void Flush();
}
