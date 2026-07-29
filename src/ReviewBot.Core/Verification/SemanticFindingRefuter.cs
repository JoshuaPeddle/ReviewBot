using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Verification;

/// <summary>
/// Drops findings whose claim about a language construct's behaviour the syntax tree
/// disproves.
/// </summary>
/// <remarks>
/// The sibling of <see cref="FindingRefuter"/>, which refutes "this does not compile"
/// using parse results. These comments concede the code compiles and then assert it
/// behaves differently, so parse results cannot contradict them and they reach the PR
/// unchallenged — twice in this project, both times about raw string literals.
///
/// Resolution is delegated so Core stays free of a Roslyn dependency: the caller passes
/// the language-specific verifier.
/// </remarks>
public static class SemanticFindingRefuter
{
    public static RefutationResult Refute(
        IReadOnlyList<InlineComment> comments,
        Func<SemanticClaimKind, string, int, SemanticVerdict> verify,
        Func<string, string?> readSource)
    {
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(readSource);

        if (comments.Count == 0)
        {
            return new RefutationResult(comments, []);
        }

        var kept = new List<InlineComment>(comments.Count);
        var refuted = new List<InlineComment>();
        var sources = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var comment in comments)
        {
            var claim = SemanticClaimClassifier.Classify(comment.Body);
            if (claim == SemanticClaimKind.None)
            {
                kept.Add(comment);
                continue;
            }

            if (!sources.TryGetValue(comment.Path, out var source))
            {
                source = readSource(comment.Path);
                sources[comment.Path] = source;
            }

            if (string.IsNullOrEmpty(source))
            {
                kept.Add(comment);
                continue;
            }

            if (verify(claim, source, comment.Line) == SemanticVerdict.Refuted)
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
