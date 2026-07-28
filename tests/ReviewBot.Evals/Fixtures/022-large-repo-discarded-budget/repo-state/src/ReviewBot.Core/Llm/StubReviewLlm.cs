using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Llm;

public sealed class StubReviewLlm : IReviewLlm
{
    private readonly Func<ReviewRequest, ReviewResult> review;
    private readonly Func<PromptPayload, string> completeRaw;

    public StubReviewLlm(ReviewResult result)
        : this(_ => result, _ => string.Empty)
    {
        ArgumentNullException.ThrowIfNull(result);
    }

    public StubReviewLlm(Func<ReviewRequest, ReviewResult> review)
        : this(review, _ => string.Empty)
    {
    }

    public StubReviewLlm(Func<ReviewRequest, ReviewResult> review, Func<PromptPayload, string> completeRaw)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(completeRaw);

        this.review = review;
        this.completeRaw = completeRaw;
    }

    public Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(review(request));
    }

    public Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review")
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(completeRaw(prompt));
    }
}
