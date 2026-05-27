namespace ReviewBot.Core.Context;

public sealed class ModelContextRegistry : IModelContextRegistry
{
    public const int FallbackContextTokens = 8_192;

    private static readonly IReadOnlyList<ModelContextPattern> Defaults =
    [
        new("claude-*", 200_000, IsConfigured: false),
        new("gpt-4*", 128_000, IsConfigured: false),
        new("gpt-5*", 128_000, IsConfigured: false),
        new("qwen*:*b*", 32_768, IsConfigured: false),
        new("granite*", 128_000, IsConfigured: false)
    ];

    private readonly IReadOnlyList<ModelContextPattern> patterns;

    public ModelContextRegistry(ModelContextOptions? options = null)
    {
        var configured = options?.Limits
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0)
            .Select(pair => new ModelContextPattern(pair.Key.Trim(), pair.Value, IsConfigured: true))
            .ToArray() ?? [];

        patterns = configured.Concat(Defaults).ToArray();
    }

    public int GetContextWindowTokens(string modelIdentifier)
    {
        if (string.IsNullOrWhiteSpace(modelIdentifier))
        {
            return FallbackContextTokens;
        }

        return patterns
            .Where(pattern => pattern.IsMatch(modelIdentifier))
            .OrderByDescending(pattern => pattern.LiteralPrefixLength)
            .ThenByDescending(pattern => pattern.IsConfigured)
            .ThenByDescending(pattern => pattern.Pattern.Length)
            .Select(pattern => pattern.ContextTokens)
            .FirstOrDefault(FallbackContextTokens);
    }

    private sealed record ModelContextPattern(string Pattern, int ContextTokens, bool IsConfigured)
    {
        public int LiteralPrefixLength { get; } = Pattern.IndexOf('*') is var index && index >= 0
            ? index
            : Pattern.Length;

        public bool IsMatch(string value)
        {
            var patternIndex = 0;
            var valueIndex = 0;

            while (patternIndex < Pattern.Length)
            {
                if (Pattern[patternIndex] == '*')
                {
                    patternIndex++;
                    if (patternIndex == Pattern.Length)
                    {
                        return true;
                    }

                    var nextWildcard = Pattern.IndexOf('*', patternIndex);
                    var segment = nextWildcard >= 0
                        ? Pattern[patternIndex..nextWildcard]
                        : Pattern[patternIndex..];
                    var segmentIndex = value.IndexOf(segment, valueIndex, StringComparison.OrdinalIgnoreCase);
                    if (segmentIndex < 0)
                    {
                        return false;
                    }

                    valueIndex = segmentIndex + segment.Length;
                    patternIndex += segment.Length;
                    continue;
                }

                if (valueIndex >= value.Length ||
                    char.ToUpperInvariant(value[valueIndex]) != char.ToUpperInvariant(Pattern[patternIndex]))
                {
                    return false;
                }

                valueIndex++;
                patternIndex++;
            }

            return valueIndex == value.Length;
        }
    }
}
