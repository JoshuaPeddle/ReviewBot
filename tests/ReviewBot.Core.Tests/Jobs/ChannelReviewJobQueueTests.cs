using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReviewBot.Core.Jobs;

namespace ReviewBot.Core.Tests.Jobs;

public class ChannelReviewJobQueueTests
{
    [Fact]
    public async Task EnqueuedItemsComeOutInFifoOrder()
    {
        var queue = new ChannelReviewJobQueue();
        var first = CreateJob("delivery-1", prNumber: 1);
        var second = CreateJob("delivery-2", prNumber: 2);

        await queue.EnqueueAsync(first, CancellationToken.None);
        await queue.EnqueueAsync(second, CancellationToken.None);

        var results = await TakeAsync(queue, count: 2, CancellationToken.None);

        results.Should().ContainInOrder(first, second);
    }

    [Fact]
    public async Task DequeueAllAsyncRespectsCancellation()
    {
        var queue = new ChannelReviewJobQueue();
        using var cts = new CancellationTokenSource();
        var enumerator = queue.DequeueAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await cts.CancelAsync();
        var moveNext = async () => await enumerator.MoveNextAsync();

        await moveNext.Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task EnqueueAsyncWaitsWhenCapacityReached()
    {
        var queue = new ChannelReviewJobQueue(capacity: 1);
        await queue.EnqueueAsync(CreateJob("delivery-1"), CancellationToken.None);

        var blockedWrite = queue.EnqueueAsync(CreateJob("delivery-2"), CancellationToken.None).AsTask();
        var completedEarly = await Task.WhenAny(blockedWrite, Task.Delay(TimeSpan.FromMilliseconds(100)));

        completedEarly.Should().NotBe(blockedWrite);

        var results = await TakeAsync(queue, count: 2, CancellationToken.None);

        results.Select(job => job.DeliveryId).Should().Equal("delivery-1", "delivery-2");
        await blockedWrite.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SecondActiveReaderIsRejected()
    {
        var queue = new ChannelReviewJobQueue();
        await queue.EnqueueAsync(CreateJob("delivery-1"), CancellationToken.None);
        using var holdReader = new CancellationTokenSource();
        var firstReaderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstReader = Task.Run(async () =>
        {
            await foreach (var _ in queue.DequeueAllAsync(holdReader.Token))
            {
                firstReaderStarted.SetResult();
                await releaseFirstReader.Task;
                break;
            }
        });

        await firstReaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondReader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();

        var moveNext = async () => await secondReader.MoveNextAsync();

        await moveNext.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("ChannelReviewJobQueue supports a single active reader.");

        await secondReader.DisposeAsync();
        releaseFirstReader.SetResult();
        await firstReader.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AddChannelReviewJobQueueRegistersQueueAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddChannelReviewJobQueue();

        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IReviewJobQueue>();
        var concrete = provider.GetRequiredService<ChannelReviewJobQueue>();

        concrete.Should().BeSameAs(queue);
    }

    private static async Task<IReadOnlyList<ReviewJob>> TakeAsync(
        IReviewJobQueue queue,
        int count,
        CancellationToken ct)
    {
        var results = new List<ReviewJob>();

        await foreach (var job in queue.DequeueAllAsync(ct))
        {
            results.Add(job);

            if (results.Count == count)
            {
                break;
            }
        }

        return results;
    }

    private static ReviewJob CreateJob(string deliveryId, int prNumber = 123)
    {
        return new ReviewJob(
            DeliveryId: deliveryId,
            InstallationId: 456,
            Owner: "octo-org",
            Repo: "reviewbot",
            PrNumber: prNumber,
            HeadSha: "abc123",
            Reason: "review_requested");
    }
}
