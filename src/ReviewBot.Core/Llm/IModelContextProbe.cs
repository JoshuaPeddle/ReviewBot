namespace ReviewBot.Core.Llm;

/// <summary>
/// Optional capability for a review LLM that can discover the model's real
/// context window from the live provider (e.g. a vLLM server reports
/// <c>max_model_len</c>). This is more reliable than guessing from the model
/// name, because for self-hosted models the served window is whatever the
/// operator launched, independent of the name.
///
/// Implementations must never throw and should be cheap to call repeatedly
/// (cache the result): the worker calls this on the hot path before budgeting.
/// Return <c>null</c> when the window cannot be determined so the caller can
/// fall back to the static registry.
/// </summary>
public interface IModelContextProbe
{
    Task<int?> TryGetContextWindowTokensAsync(string modelName, CancellationToken ct);
}
