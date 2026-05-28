namespace ReviewBot.Api.Tracing;

public sealed class NullReviewTraceWriter : IReviewTraceWriter
{
    public static readonly NullReviewTraceWriter Instance = new();

    private NullReviewTraceWriter() { }

    public Task WriteAsync(ReviewTrace trace, CancellationToken ct = default) => Task.CompletedTask;
}
