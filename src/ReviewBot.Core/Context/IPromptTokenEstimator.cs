namespace ReviewBot.Core.Context;

public interface IPromptTokenEstimator
{
    int EstimateTokens(string? text);
}
