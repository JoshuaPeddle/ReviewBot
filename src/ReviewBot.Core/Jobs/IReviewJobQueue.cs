namespace ReviewBot.Core.Jobs;

public interface IReviewJobQueue
{
    ValueTask EnqueueAsync(ReviewJob job, CancellationToken ct);

    IAsyncEnumerable<ReviewJob> DequeueAllAsync(CancellationToken ct);
}
