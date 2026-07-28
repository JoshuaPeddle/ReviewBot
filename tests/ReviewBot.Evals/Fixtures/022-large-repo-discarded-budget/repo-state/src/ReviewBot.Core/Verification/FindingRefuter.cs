using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Verification;

/// <summary>
/// Drops findings that ground truth provably contradicts. Scoped to compile/syntax
/// failure claims on files proven to parse cleanly (a successful build, or a
/// diagnostic provider that ran over the file and reported no error). Unlike
/// corroboration this removes a finding, so it stays inside the only domain where
/// the ground truth is total — it never refutes a logic, type, or conditional claim.
/// </summary>
public static class FindingRefuter
{
    public static RefutationResult Refute(
        IReadOnlyList<InlineComment> comments,
        IReadOnlySet<string> cleanlyParsedPaths)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(cleanlyParsedPaths);

        if (comments.Count == 0 || cleanlyParsedPaths.Count == 0)
        {
            return new RefutationResult(comments, []);
        }

        var kept = new List<InlineComment>(comments.Count);
        var refuted = new List<InlineComment>();
        foreach (var comment in comments)
        {
            if (cleanlyParsedPaths.Contains(comment.Path) &&
                CompileClaimClassifier.IsCompileFailureClaim(comment.Body))
            {
                refuted.Add(comment);
            }
            else
            {
                kept.Add(comment);
            }
        }

        return refuted.Count == 0
            ? new RefutationResult(comments, [])
            : new RefutationResult(kept, refuted);
    }
}

public sealed record RefutationResult(
    IReadOnlyList<InlineComment> Kept,
    IReadOnlyList<InlineComment> Refuted);
