using System.Diagnostics.Metrics;
using ReviewBot.Core.Llm;

namespace ReviewBot.Api.Workers;

public sealed class ReviewBotMetrics : IDisposable
{
    public const string MeterName = ReviewBotLlmMetrics.MeterName;

    private readonly Meter meter;
    private readonly Counter<long> jobsProcessed;
    private readonly Counter<long> jobsSkipped;
    private readonly Histogram<double> llmDurationMs;
    private readonly Histogram<int> reviewCommentsPosted;
    private readonly Histogram<double> groundingDurationMs;
    private readonly Counter<long> incrementalReviews;

    public ReviewBotMetrics()
    {
        meter = new Meter(MeterName);
        jobsProcessed = meter.CreateCounter<long>(
            "reviewbot.jobs.processed",
            description: "Number of review jobs processed");
        jobsSkipped = meter.CreateCounter<long>(
            "reviewbot.jobs.skipped",
            description: "Number of review jobs skipped, broken down by reason");
        llmDurationMs = meter.CreateHistogram<double>(
            "reviewbot.llm.duration_ms",
            unit: "ms",
            description: "Duration of LLM review calls");
        reviewCommentsPosted = meter.CreateHistogram<int>(
            "reviewbot.review.comments_posted",
            description: "Number of inline comments included in a posted review");
        groundingDurationMs = meter.CreateHistogram<double>(
            "reviewbot.grounding.duration_ms",
            unit: "ms",
            description: "Duration of grounding context collection");
        incrementalReviews = meter.CreateCounter<long>(
            "reviewbot.review.incremental_type",
            description: "Number of reviews by incremental review type");
    }

    public void RecordJobProcessed(string status) =>
        jobsProcessed.Add(1, new KeyValuePair<string, object?>("status", status));

    public void RecordSkip(string reason) =>
        jobsSkipped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordLlmDuration(double durationMs, string provider, string phase = "review") =>
        llmDurationMs.Record(
            durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("phase", phase));

    public void RecordCommentsPosted(int count) =>
        reviewCommentsPosted.Record(count);

    public void RecordGroundingDuration(double durationMs, string result) =>
        groundingDurationMs.Record(durationMs, new KeyValuePair<string, object?>("result", result));

    public void RecordIncrementalReview(string type) =>
        incrementalReviews.Add(1, new KeyValuePair<string, object?>("type", type));

    public void Dispose() => meter.Dispose();
}
