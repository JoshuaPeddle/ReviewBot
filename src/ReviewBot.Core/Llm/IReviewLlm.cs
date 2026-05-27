using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Llm;

public interface IReviewLlm
{
    bool SupportsParallelRequests => false;

    Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct);

    Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review");
}
