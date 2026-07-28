namespace ReviewBot.Core.Llm;

/// <summary>
/// Shared handling for a provider response that came back with no usable content.
/// </summary>
public static class EmptyLlmResponse
{
    /// <summary>
    /// Throws when <paramref name="content"/> is empty or whitespace.
    /// </summary>
    /// <remarks>
    /// Reasoning models emit chain-of-thought into a separate response field and only
    /// then produce the answer. When the output allowance runs out mid-reasoning the
    /// provider reports completion tokens but no content — observed on Qwen3.6-27B,
    /// which spent all 4606 of its allowed output tokens reasoning and returned an
    /// empty body. There is nothing to repair in that case (a repair round-trip just
    /// burns another full request on an empty string), and the answer is more output
    /// headroom, so say so instead of failing mutely.
    /// </remarks>
    public static void ThrowIfUnusable(
        string? content,
        string providerName,
        int? completionTokens,
        int maxOutputTokens)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var consumed = completionTokens ?? 0;
        var diagnosis = consumed > 0
            ? $" It consumed {consumed} completion tokens against an allowance of {maxOutputTokens} "
              + "without emitting any content, which is what a reasoning model looks like when it "
              + "exhausts its output budget mid-thought. Raise review.response_reserve_tokens (or the "
              + "host's OpenAi:MaxTokens / Anthropic:MaxTokens) so the model has room to answer."
            : " The provider reported no completion tokens at all, so the request produced nothing.";

        throw new LlmResponseUnusableException($"{providerName} returned an empty response.{diagnosis}");
    }
}
