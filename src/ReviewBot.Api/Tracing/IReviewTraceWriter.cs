namespace ReviewBot.Api.Tracing;

public interface IReviewTraceWriter
{
    bool IncludePrompts { get; }

    Task WriteAsync(ReviewTrace trace, CancellationToken ct = default);
}
