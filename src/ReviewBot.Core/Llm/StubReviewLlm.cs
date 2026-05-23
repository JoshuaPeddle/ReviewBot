using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public sealed class StubReviewLlm : IReviewLlm
{
    private readonly Func<ReviewRequest, ReviewResult> review;

    public StubReviewLlm(ReviewResult result)
        : this(_ => result)
    {
        ArgumentNullException.ThrowIfNull(result);
    }

    public StubReviewLlm(Func<ReviewRequest, ReviewResult> review)
    {
        ArgumentNullException.ThrowIfNull(review);

        this.review = review;
    }

    public Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(review(request));
    }
}
