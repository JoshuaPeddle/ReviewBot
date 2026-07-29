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
    /// burns another full request on an empty string), so say why instead of failing
    /// mutely.
    /// </remarks>
    /// <param name="allowanceWasExpanded">
    /// True when the caller already retried at a larger allowance and got another empty
    /// response. That changes the diagnosis: "give it more room" is the right advice the
    /// first time and the wrong advice once more room has demonstrably not helped.
    /// </param>
    public static void ThrowIfUnusable(
        string? content,
        string providerName,
        int? completionTokens,
        int maxOutputTokens,
        bool allowanceWasExpanded = false)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var consumed = completionTokens ?? 0;
        var diagnosis = consumed switch
        {
            0 => " The provider reported no completion tokens at all, so the request produced nothing.",
            _ when allowanceWasExpanded =>
                $" It consumed {consumed} completion tokens against an allowance of {maxOutputTokens}, "
                + "which had already been raised once after an identical empty response, without emitting "
                + "any content either time. A model that merely lacked room would finish at some size, so "
                + "this generation is not converging — raising the allowance again would spend another "
                + "full request to learn the same thing. Reduce what the request has to reason about "
                + "(lower review.max_patch_lines or review.max_files so chunks are smaller), or try a "
                + "different model or sampling settings.",
            _ =>
                $" It consumed {consumed} completion tokens against an allowance of {maxOutputTokens} "
                + "without emitting any content, which is what a reasoning model looks like when it "
                + "exhausts its output budget mid-thought. Raise review.response_reserve_tokens (or the "
                + "host's OpenAi:MaxTokens / Anthropic:MaxTokens) so the model has room to answer."
        };

        throw new LlmResponseUnusableException($"{providerName} returned an empty response.{diagnosis}");
    }
}
