using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Context;

public sealed class ReviewPromptTokenEstimator : IReviewPromptTokenEstimator
{
    private readonly IPromptTokenEstimator fallback;
    private readonly IReadOnlyDictionary<string, IProviderPromptTokenEstimator> providerEstimators;

    public ReviewPromptTokenEstimator(
        IPromptTokenEstimator fallback,
        IEnumerable<IProviderPromptTokenEstimator> providerEstimators)
    {
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        ArgumentNullException.ThrowIfNull(providerEstimators);

        this.providerEstimators = providerEstimators
            .GroupBy(estimator => estimator.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public int EstimateTokens(ModelConfig model, string? text)
    {
        ArgumentNullException.ThrowIfNull(model);

        return providerEstimators.TryGetValue(model.Provider, out var estimator)
            ? estimator.EstimateTokens(model, text)
            : fallback.EstimateTokens(text);
    }
}
