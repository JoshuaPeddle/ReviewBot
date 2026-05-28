using Microsoft.Extensions.Options;
using ReviewBot.Core.Llm;

namespace ReviewBot.Api.Cost;

public sealed class ReviewCostCalculator : IReviewCostCalculator
{
    private readonly CostRateOptions options;

    public ReviewCostCalculator(IOptions<CostRateOptions> options)
    {
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public decimal? ComputeCostUsd(string modelName, LlmTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (!options.Rates.TryGetValue(modelName, out var rate))
            return null;

        return (usage.PromptTokens / 1_000_000m) * rate.InputPer1M
             + (usage.CompletionTokens / 1_000_000m) * rate.OutputPer1M;
    }
}
