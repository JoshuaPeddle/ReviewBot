namespace ReviewBot.Core.Jobs;

public interface IReviewJobQueue
{
    ValueTask<bool> TryEnqueueAsync(ReviewJob job, CancellationToken ct);

    IAsyncEnumerable<ReviewJob> DequeueAllAsync(CancellationToken ct);

    ValueTask<bool> CompleteAsync(ReviewJob job, CancellationToken ct);

    /// <summary>
    /// Returns an owned job to the queue without charging an attempt, for interruptions
    /// that say nothing about whether the job can succeed — worker shutdown above all.
    /// </summary>
    ValueTask<bool> ReleaseAsync(ReviewJob job, CancellationToken ct);

    ValueTask<ReviewJobFailureDisposition> FailAsync(ReviewJob job, string error, CancellationToken ct);

    ValueTask<bool> RenewLeaseAsync(ReviewJob job, CancellationToken ct);

}

public enum ReviewJobFailureDisposition
{
    RetryScheduled,
    DeadLettered,
    LeaseLost
}
