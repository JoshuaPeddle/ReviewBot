namespace ReviewBot.Core.Context;

public sealed class HeuristicTokenEstimator : IPromptTokenEstimator
{
    private const double CharactersPerToken = 3d;

    public int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / CharactersPerToken);
    }
}
