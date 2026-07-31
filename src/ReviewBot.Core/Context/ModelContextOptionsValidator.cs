namespace ReviewBot.Core.Context;

internal static class ModelContextOptionsValidator
{
    private const int MaxPatternLength = 256;
    private const int MaxContextWindowTokens = 2_000_000;

    public static void Validate(ModelContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxContextWindowTokens is { } globalCap)
        {
            ValidateTokenCount("ModelContext:MaxContextWindowTokens", globalCap);
        }

        if (options.Limits is null)
        {
            throw new InvalidOperationException("ModelContext:Limits must be an object when configured.");
        }

        foreach (var (pattern, tokens) in options.Limits)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new InvalidOperationException(
                    "ModelContext:Limits keys must contain a model name or wildcard pattern.");
            }

            if (pattern.Length > MaxPatternLength)
            {
                throw new InvalidOperationException(
                    $"ModelContext:Limits pattern '{pattern}' must not exceed {MaxPatternLength} characters.");
            }

            if (!string.Equals(pattern, pattern.Trim(), StringComparison.Ordinal) ||
                pattern.Any(char.IsControl) ||
                pattern.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"ModelContext:Limits pattern '{pattern}' must not contain whitespace or control characters.");
            }

            ValidateTokenCount($"ModelContext:Limits:{pattern}", tokens);
        }
    }

    private static void ValidateTokenCount(string key, int tokens)
    {
        if (tokens is < 1 or > MaxContextWindowTokens)
        {
            throw new InvalidOperationException(
                $"{key} must be between 1 and {MaxContextWindowTokens} tokens.");
        }
    }
}
