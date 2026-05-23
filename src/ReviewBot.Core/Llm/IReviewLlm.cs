using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

public interface IReviewLlm
{
    Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct);
}
