namespace ReviewBot.Llm.Anthropic;

internal static class AnthropicLlmOptionsValidator
{
    private const int MaxTokenCount = 1_000_000;
    private const int MaxConcurrency = 64;

    public static void Validate(AnthropicLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            throw new InvalidOperationException("Anthropic:ModelName must be provided.");
        }

        if (!string.Equals(options.ModelName, options.ModelName.Trim(), StringComparison.Ordinal) ||
            options.ModelName.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "Anthropic:ModelName must not have leading or trailing whitespace or contain control characters.");
        }

        ValidateIntegerRange("Anthropic:MaxTokens", options.MaxTokens, 1, MaxTokenCount);
        if (options.Temperature is < 0 or > 1)
        {
            throw new InvalidOperationException("Anthropic:Temperature must be between 0 and 1.");
        }

        ValidateIntegerRange(
            "Anthropic:TokenCountingHeuristicThresholdTokens",
            options.TokenCountingHeuristicThresholdTokens,
            1,
            MaxTokenCount);
        ValidateIntegerRange(
            "Anthropic:MaxConcurrentRequests",
            options.MaxConcurrentRequests,
            1,
            MaxConcurrency);
    }

    private static void ValidateIntegerRange(string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }
    }
}
