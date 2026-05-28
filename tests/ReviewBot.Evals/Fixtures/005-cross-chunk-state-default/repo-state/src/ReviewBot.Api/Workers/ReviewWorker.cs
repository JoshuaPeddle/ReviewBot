using ReviewBot.Core.Storage;

namespace ReviewBot.Api.Workers;

public sealed class ReviewWorker
{
    private ReviewScope SelectScope(PrReviewState state)
    {
        if (string.IsNullOrWhiteSpace(state.LastReviewedHeadSha))
        {
            return ReviewScope.FullReview;
        }

        return ReviewScope.DeltaReview;
    }
}

public enum ReviewScope
{
    FullReview,
    DeltaReview
}
