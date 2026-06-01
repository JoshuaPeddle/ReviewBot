namespace ReviewBot.Api.Tracing;

public sealed class NullReviewTraceWriter : IReviewTraceWriter
{
    public static readonly NullReviewTraceWriter Instance = new();

    private NullReviewTraceWriter() { }

    public bool IncludePrompts => false;

    public Task WriteAsync(ReviewTrace trace, CancellationToken ct = default) => Task.CompletedTask;
}
