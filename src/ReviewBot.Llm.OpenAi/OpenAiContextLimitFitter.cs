using System.Text.RegularExpressions;

namespace ReviewBot.Llm.OpenAi;

/// <summary>
/// Refits the requested output-token count when an OpenAI-compatible server
/// (e.g. vLLM, LM Studio) rejects a request because prompt + output would exceed
/// the model's context window.
///
/// ReviewBot's prompt budget sizes a chunk to fit the context window using a
/// heuristic token estimator, but two things can still tip a request over the
/// server's hard limit: the server's real tokenizer counts a few more tokens
/// than the estimate, and the configured output allowance can be larger than the
/// budget's response reserve. Rather than lose the whole review, we shrink the
/// output allowance and retry.
///
/// We intentionally do NOT trust any token count in the error body: servers like
/// vLLM report the input size as a lower bound derived from the requested output
/// ("for a total of at least N tokens"), so it moves with our own request and is
/// useless for arithmetic. Geometric backoff is robust to that — each rejection
/// proves the output ask is still too big, so we halve it until it fits or we hit
/// a floor below which no useful review could be produced.
/// </summary>
internal static partial class OpenAiContextLimitFitter
{
    // Never refit below this — a smaller window can't hold a useful review reply.
    private const int MinOutputTokens = 512;

    /// <summary>
    /// When <paramref name="errorBody"/> is a recognised context-overflow error,
    /// returns a smaller output-token allowance (half the current one) to retry
    /// with. Returns false for unrelated errors or when halving would drop below
    /// a useful floor.
    /// </summary>
    public static bool TryFitMaxOutputTokens(string? errorBody, int currentMaxTokens, out int fittedMaxTokens)
    {
        fittedMaxTokens = 0;
        if (!IsContextOverflow(errorBody))
        {
            return false;
        }

        var candidate = currentMaxTokens / 2;
        if (candidate < MinOutputTokens)
        {
            return false;
        }

        fittedMaxTokens = candidate;
        return true;
    }

    private static bool IsContextOverflow(string? errorBody) =>
        !string.IsNullOrWhiteSpace(errorBody) &&
        (ContextLengthRegex().IsMatch(errorBody) || MaxModelLenRegex().IsMatch(errorBody));

    // "This model's maximum context length is 32768 tokens. However, you requested..."
    [GeneratedRegex(@"maximum context length", RegexOptions.IgnoreCase)]
    private static partial Regex ContextLengthRegex();

    // "...cannot be greater than max_model_len..." (alternate vLLM phrasing)
    [GeneratedRegex(@"max_model_len", RegexOptions.IgnoreCase)]
    private static partial Regex MaxModelLenRegex();
}
