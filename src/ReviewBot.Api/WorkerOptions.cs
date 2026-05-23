namespace ReviewBot.Api;

public sealed record WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Number of jobs processed concurrently. Default 1; raise for higher throughput.</summary>
    /// <remarks>
    /// The installation-token provider uses per-installation SemaphoreSlim gates, so it is
    /// safe to call from multiple concurrent jobs without any additional locking.
    /// </remarks>
    public int Concurrency { get; set; } = 1;
}
