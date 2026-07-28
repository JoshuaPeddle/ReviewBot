using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

public interface IProviderPromptTokenEstimator
{
    string ProviderName { get; }

    int EstimateTokens(ModelConfig model, string? text);
}
