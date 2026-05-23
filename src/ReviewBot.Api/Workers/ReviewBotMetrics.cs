using System.Diagnostics.Metrics;

namespace ReviewBot.Api.Workers;

public sealed class ReviewBotMetrics : IDisposable
{
    public const string MeterName = "ReviewBot";

    private readonly Meter meter;
    private readonly Counter<long> jobsProcessed;
    private readonly Histogram<double> llmDurationMs;
    private readonly Histogram<int> reviewCommentsPosted;

    public ReviewBotMetrics()
    {
        meter = new Meter(MeterName);
        jobsProcessed = meter.CreateCounter<long>(
            "reviewbot.jobs.processed",
            description: "Number of review jobs processed");
        llmDurationMs = meter.CreateHistogram<double>(
            "reviewbot.llm.duration_ms",
            unit: "ms",
            description: "Duration of LLM review calls");
        reviewCommentsPosted = meter.CreateHistogram<int>(
            "reviewbot.review.comments_posted",
            description: "Number of inline comments included in a posted review");
    }

    public void RecordJobProcessed(string status) =>
        jobsProcessed.Add(1, new KeyValuePair<string, object?>("status", status));

    public void RecordLlmDuration(double durationMs, string provider) =>
        llmDurationMs.Record(durationMs, new KeyValuePair<string, object?>("provider", provider));

    public void RecordCommentsPosted(int count) =>
        reviewCommentsPosted.Record(count);

    public void Dispose() => meter.Dispose();
}
