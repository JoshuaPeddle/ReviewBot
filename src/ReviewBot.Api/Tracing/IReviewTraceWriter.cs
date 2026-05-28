namespace ReviewBot.Api.Tracing;

public interface IReviewTraceWriter
{
    Task WriteAsync(ReviewTrace trace, CancellationToken ct = default);
}
