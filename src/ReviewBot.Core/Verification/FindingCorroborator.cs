using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Verification;

/// <summary>
/// Cross-checks review findings against ground-truth build diagnostics. A finding
/// whose line is independently flagged by the compiler is marked
/// <see cref="VerificationStatus.Verified"/> and paired with the corroborating
/// diagnostic as evidence; everything else is returned unchanged. This only ever
/// upgrades a finding's standing — it never drops one — so it is safe to run on
/// any candidate set.
/// </summary>
public static class FindingCorroborator
{
    public const int DefaultLineTolerance = 2;

    public static IReadOnlyList<CorroboratedFinding> Corroborate(
        IReadOnlyList<InlineComment> comments,
        IReadOnlyList<Diagnostic> diagnostics,
        int lineTolerance = DefaultLineTolerance)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (comments.Count == 0 || diagnostics.Count == 0)
        {
            return comments.Select(c => new CorroboratedFinding(c, null)).ToArray();
        }

        var diagnosticsByPath = diagnostics
            .GroupBy(d => d.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var results = new List<CorroboratedFinding>(comments.Count);
        foreach (var comment in comments)
        {
            var evidence = FindEvidence(comment, diagnosticsByPath, lineTolerance);
            results.Add(evidence is null
                ? new CorroboratedFinding(comment, null)
                : new CorroboratedFinding(comment with { Verification = VerificationStatus.Verified }, evidence));
        }

        return results;
    }

    private static Diagnostic? FindEvidence(
        InlineComment comment,
        IReadOnlyDictionary<string, Diagnostic[]> diagnosticsByPath,
        int lineTolerance)
    {
        if (!diagnosticsByPath.TryGetValue(comment.Path, out var candidates))
        {
            return null;
        }

        // Closest line wins; ties break toward the more severe diagnostic so the
        // evidence we surface is the strongest available corroboration.
        return candidates
            .Where(d => Math.Abs(d.Line - comment.Line) <= lineTolerance)
            .OrderBy(d => Math.Abs(d.Line - comment.Line))
            .ThenByDescending(d => d.Severity)
            .FirstOrDefault();
    }
}

public sealed record CorroboratedFinding(InlineComment Comment, Diagnostic? Evidence);
