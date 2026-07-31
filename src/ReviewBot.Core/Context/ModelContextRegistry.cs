namespace ReviewBot.Core.Context;

public sealed class ModelContextRegistry : IModelContextRegistry
{
    public const int FallbackContextTokens = 8_192;

    private static readonly IReadOnlyList<ModelContextPattern> Defaults =
    [
        new("claude-*", 200_000, IsConfigured: false),
        new("gpt-4*", 128_000, IsConfigured: false),
        new("gpt-5*", 128_000, IsConfigured: false),
        // Reference local model — exact entries so the longest-literal-prefix
        // tiebreaker beats the wider qwen patterns below.
        new("qwen/qwen3.6-27b", 32_768, IsConfigured: false),
        new("qwen3.6-27b", 32_768, IsConfigured: false),
        // Wider patterns cover Ollama, LM Studio, and bare model name styles
        // for the standard Qwen 32K-context variants.
        new("qwen*:*b*", 32_768, IsConfigured: false),
        new("qwen*/*b*", 32_768, IsConfigured: false),
        new("qwen*-*b*", 32_768, IsConfigured: false),
        new("qwen*b*", 32_768, IsConfigured: false),
        new("llama3*:8b*", 8_192, IsConfigured: false),
        new("*:8b*", 8_192, IsConfigured: false),
        new("llama3*:70b*", 131_072, IsConfigured: false),
        new("*:70b*", 131_072, IsConfigured: false),
        new("granite*", 128_000, IsConfigured: false)
    ];

    private readonly IReadOnlyList<ModelContextPattern> patterns;
    private readonly int? globalCap;

    public ModelContextRegistry(ModelContextOptions? options = null)
    {
        options ??= new ModelContextOptions();
        ModelContextOptionsValidator.Validate(options);

        globalCap = options.MaxContextWindowTokens;
        var configured = new List<ModelContextPattern>();
        foreach (var pair in options.Limits)
        {
            configured.Add(new ModelContextPattern(pair.Key, pair.Value, IsConfigured: true));
        }

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

    public int ApplyConfiguredCap(string modelIdentifier, int discoveredTokens)
    {
        if (discoveredTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discoveredTokens), discoveredTokens, "Discovered context tokens must be positive.");
        }

        var modelCap = patterns
            .Where(pattern => pattern.IsConfigured && pattern.IsMatch(modelIdentifier))
            .OrderByDescending(pattern => pattern.LiteralPrefixLength)
            .ThenByDescending(pattern => pattern.Pattern.Length)
            .Select(pattern => (int?)pattern.ContextTokens)
            .FirstOrDefault();
        var cap = new[] { globalCap, modelCap }
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty(discoveredTokens)
            .Min();
        return Math.Min(discoveredTokens, cap);
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
