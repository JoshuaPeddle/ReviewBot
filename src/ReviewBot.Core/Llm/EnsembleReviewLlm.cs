using ReviewBot.Core.Domain;
using ReviewBot.Core.Prompting;

namespace ReviewBot.Core.Llm;

/// <summary>
/// Samples the wrapped reviewer <c>k</c> times for one request and keeps the findings enough
/// samples agree on.
/// </summary>
/// <remarks>
/// Self-consistency costs k× the tokens for one review, which is why token-metered cloud
/// reviewers cannot run it. On unmetered local inference it is the cheapest lever available on
/// the precision/recall trade: <see cref="MinAgreement"/> at 1 is a recall-buying union,
/// at <c>k</c> it is a precision-buying unanimity requirement.
/// </remarks>
public sealed class EnsembleReviewLlm : IReviewLlm
{
    private readonly IReviewLlm inner;

    public EnsembleReviewLlm(IReviewLlm inner, int samples, int minAgreement, int lineWindow = EnsembleMerger.DefaultLineWindow)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minAgreement, 1);

        this.inner = inner;
        Samples = samples;
        MinAgreement = Math.Min(minAgreement, samples);
        LineWindow = lineWindow;
    }

    public int Samples { get; }

    public int MinAgreement { get; }

    public int LineWindow { get; }

    public int MaxConcurrentRequests => inner.MaxConcurrentRequests;

    /// <summary>
    /// Reviews once per sample and merges. Samples run concurrently when the provider supports
    /// it; a sample that throws is dropped rather than failing the review, because k-1 samples
    /// still produce a usable answer and the alternative is losing the whole review to one
    /// transport error.
    /// </summary>
    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Samples == 1)
        {
            return await inner.ReviewAsync(request, ct).ConfigureAwait(false);
        }

        var results = new ReviewResult?[Samples];
        var parallelism = Math.Max(1, Math.Min(Samples, inner.MaxConcurrentRequests));

        await Parallel.ForAsync(
            0,
            Samples,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (index, loopCt) =>
            {
                try
                {
                    results[index] = await inner.ReviewAsync(request, loopCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[index] = null;
                }
            })
            .ConfigureAwait(false);

        var succeeded = results.OfType<ReviewResult>().ToArray();
        if (succeeded.Length == 0)
        {
            // Every sample failed: surface that as an error rather than an empty review, which
            // would score as "the model found nothing".
            throw new InvalidOperationException(
                $"All {Samples} ensemble samples failed for the review request.");
        }

        // Agreement is relative to the samples that came back. Holding the threshold against
        // the requested k would make a dropped sample silently raise the bar.
        var required = Math.Min(MinAgreement, succeeded.Length);
        return EnsembleMerger.Merge(succeeded, required, LineWindow);
    }

    public Task<string> CompleteRawAsync(PromptPayload prompt, CancellationToken ct, string phase = "review") =>
        inner.CompleteRawAsync(prompt, ct, phase);
}
