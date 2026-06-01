using System.Diagnostics.Metrics;

namespace ReviewBot.Core.Llm;

public static class ReviewBotLlmMetrics
{
    public const string MeterName = "ReviewBot";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<int> Tokens = Meter.CreateHistogram<int>(
        "reviewbot.llm.tokens",
        description: "Number of tokens reported by LLM providers");

    private static readonly Counter<long> ParseFailures = Meter.CreateCounter<long>(
        "reviewbot.llm.parse_failures_total",
        description: "Number of LLM review parse failures, broken down by repair outcome");

    public static void RecordTokenUsage(string provider, string phase, LlmTokenUsage usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(usage);

        Tokens.Record(
            usage.PromptTokens,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("direction", "prompt"));
        Tokens.Record(
            usage.CompletionTokens,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("direction", "completion"));
    }

    public static void RecordParseFailure(string provider, bool repaired)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        ParseFailures.Add(
            1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("repaired", repaired.ToString().ToLowerInvariant()));
    }
}

public sealed record LlmTokenUsage(int PromptTokens, int CompletionTokens, int CachedPromptTokens = 0)
{
    public LlmTokenUsage Add(LlmTokenUsage? other)
    {
        if (other is null) return this;
        return new LlmTokenUsage(
            PromptTokens + other.PromptTokens,
            CompletionTokens + other.CompletionTokens,
            CachedPromptTokens + other.CachedPromptTokens);
    }
}
