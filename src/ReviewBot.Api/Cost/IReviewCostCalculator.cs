using ReviewBot.Core.Llm;

namespace ReviewBot.Api.Cost;

public interface IReviewCostCalculator
{
    /// <summary>
    /// Returns the estimated USD cost for the given token usage, or null if no rate is configured for <paramref name="modelName"/>.
    /// </summary>
    decimal? ComputeCostUsd(string modelName, LlmTokenUsage usage);
}
