using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

public interface IReviewPromptTokenEstimator
{
    int EstimateTokens(ModelConfig model, string? text);
}
