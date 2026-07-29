using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Llm;

public interface IReviewLlm
{
    /// <summary>
    /// How many chunk reviews may be in flight against this provider at once.
    /// </summary>
    /// <remarks>
    /// A boolean could not express the range that matters. "Parallel or not" forced every
    /// OpenAI-compatible endpoint to be treated as single-threaded because Ollama on a
    /// laptop is, which left a batching server — vLLM or SGLang on a real GPU, where
    /// continuous batching serves several requests at close to the latency of one — doing
    /// one chunk at a time. A 10-chunk review then took ten times longer than the hardware
    /// required.
    ///
    /// 1 means sequential. The ceiling on total in-flight requests is this multiplied by
    /// <c>Worker:Concurrency</c>, since jobs run concurrently too.
    /// </remarks>
    int MaxConcurrentRequests => 1;

    Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct);

    Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review");
}
