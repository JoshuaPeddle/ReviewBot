namespace ReviewBot.Core.Metrics;

public static class MetricNames
{
    public const string JobsProcessed = "reviewbot.jobs.processed";
    public const string LlmDurationMs = "reviewbot.llm.duration_ms";
    public const string CommentsPosted = "reviewbot.review.comments_posted";
    public const string QueueDepth = "reviewbot.worker.queue_depth";
}
