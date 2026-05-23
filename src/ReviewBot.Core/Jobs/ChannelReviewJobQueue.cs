using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ReviewBot.Core.Jobs;

public sealed class ChannelReviewJobQueue : IReviewJobQueue
{
    public const int DefaultCapacity = 1000;

    private readonly Channel<ReviewJob> channel;
    private int activeReader;

    public ChannelReviewJobQueue()
        : this(DefaultCapacity)
    {
    }

    public ChannelReviewJobQueue(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        channel = Channel.CreateBounded<ReviewJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async ValueTask EnqueueAsync(ReviewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        await channel.Writer.WriteAsync(job, ct);
    }

    public async IAsyncEnumerable<ReviewJob> DequeueAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
        {
            throw new InvalidOperationException("ChannelReviewJobQueue supports a single active reader.");
        }

        try
        {
            await foreach (var job in channel.Reader.ReadAllAsync(ct))
            {
                yield return job;
            }
        }
        finally
        {
            Volatile.Write(ref activeReader, 0);
        }
    }
}
