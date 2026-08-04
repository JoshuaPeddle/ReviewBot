using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Llm;

/// <summary>
/// Merges several independent reviews of the same diff into one, keeping findings that enough
/// of the samples agree on.
/// </summary>
/// <remarks>
/// <para>
/// This is self-consistency applied to code review: the same request is sampled <c>k</c> times
/// and a finding survives only if at least <c>minAgreement</c> distinct samples report it. The
/// threshold is the whole point of the mechanism — a high threshold trades recall for
/// precision, a threshold of 1 is a union that trades the other way — so it is a parameter
/// here rather than a hardcoded majority vote.
/// </para>
/// <para>
/// This differs from <see cref="ReviewResultMerger"/>, which merges reviews of *different*
/// chunks and therefore keys on an exact location. Samples of the same diff disagree about
/// exact lines for the same defect, so agreement is matched within a line window.
/// </para>
/// </remarks>
public static class EnsembleMerger
{
    /// <summary>How far apart two comments can sit and still be treated as the same finding.</summary>
    public const int DefaultLineWindow = 3;

    /// <summary>
    /// A finding that was reported but by too few samples to survive.
    /// </summary>
    /// <remarks>
    /// These are reported rather than silently dropped because the per-review trace is the
    /// instrument used to judge the bot, and a merge that filters findings before they ever
    /// become candidates makes "the model found nothing" and "consensus rejected everything"
    /// indistinguishable in that trace. Dogfooding PR #63 hit exactly that: a review that
    /// spent 45k completion tokens recorded zero candidates and zero drops.
    /// </remarks>
    public sealed record EnsembleRejection(InlineComment Comment, int Support, int Required);

    public sealed record EnsembleMergeResult(
        ReviewResult Result,
        IReadOnlyList<EnsembleRejection> BelowThreshold);

    public static EnsembleMergeResult Merge(
        IReadOnlyList<ReviewResult> samples,
        int minAgreement,
        int lineWindow = DefaultLineWindow)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(lineWindow);

        if (samples.Count == 0)
        {
            return new EnsembleMergeResult(new ReviewResult(string.Empty, []), []);
        }

        // A threshold above the sample count would silently drop everything, which reads as a
        // model failure rather than a misconfiguration. Clamp into the meaningful range.
        var required = Math.Clamp(minAgreement, 1, samples.Count);

        var clusters = BuildClusters(samples, lineWindow);
        var comments = clusters
            .Where(cluster => cluster.SampleIndices.Count >= required)
            .Select(cluster => cluster.Representative())
            .OrderBy(comment => comment.Path, StringComparer.Ordinal)
            .ThenBy(comment => comment.Line)
            .ThenBy(comment => comment.Side, StringComparer.Ordinal)
            .ToArray();
        var belowThreshold = clusters
            .Where(cluster => cluster.SampleIndices.Count < required)
            .Select(cluster => new EnsembleRejection(
                cluster.Representative(), cluster.SampleIndices.Count, required))
            .OrderByDescending(rejection => rejection.Support)
            .ThenBy(rejection => rejection.Comment.Path, StringComparer.Ordinal)
            .ThenBy(rejection => rejection.Comment.Line)
            .ToArray();

        var contextRequests = samples
            .SelectMany(sample => sample.ContextRequests)
            .GroupBy(request => request.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(request => request.Path, StringComparer.Ordinal)
            .ToArray();

        var tokenUsage = samples
            .Select(sample => sample.TokenUsage)
            .Aggregate((LlmTokenUsage?)null, (acc, usage) => acc is null ? usage : usage is null ? acc : acc.Add(usage));

        // The summary is prose, not a finding, so there is nothing to take a vote on; the
        // first non-empty one stands in for the set.
        var summary = samples
            .Select(sample => sample.Summary)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;

        return new EnsembleMergeResult(
            new ReviewResult(summary, comments, contextRequests) { TokenUsage = tokenUsage },
            belowThreshold);
    }

    private static List<Cluster> BuildClusters(IReadOnlyList<ReviewResult> samples, int lineWindow)
    {
        var clusters = new List<Cluster>();

        for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            foreach (var comment in samples[sampleIndex].Comments)
            {
                var match = clusters.FirstOrDefault(cluster => cluster.Accepts(comment, lineWindow));
                if (match is null)
                {
                    match = new Cluster(comment.Path, comment.Side, comment.Line);
                    clusters.Add(match);
                }

                match.Add(sampleIndex, comment);
            }
        }

        return clusters;
    }

    private sealed class Cluster(string path, string side, int anchorLine)
    {
        private readonly List<InlineComment> members = [];

        public HashSet<int> SampleIndices { get; } = [];

        public bool Accepts(InlineComment comment, int lineWindow) =>
            string.Equals(comment.Path, path, StringComparison.Ordinal) &&
            string.Equals(comment.Side, side, StringComparison.Ordinal) &&
            Math.Abs(comment.Line - anchorLine) <= lineWindow;

        public void Add(int sampleIndex, InlineComment comment)
        {
            // Support is per-sample: a single sample repeating itself on the same line must not
            // be able to vote a finding through on its own.
            SampleIndices.Add(sampleIndex);
            members.Add(comment);
        }

        public InlineComment Representative() => members
            .OrderByDescending(comment => comment.Severity)
            .ThenByDescending(comment => comment.Confidence)
            .ThenBy(comment => comment.Body.Length)
            .First();
    }
}
