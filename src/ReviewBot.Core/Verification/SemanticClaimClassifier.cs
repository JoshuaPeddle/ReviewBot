using System.Text;

namespace ReviewBot.Core.Verification;

/// <summary>
/// The language-semantics claims a syntax tree can settle outright.
/// </summary>
public enum SemanticClaimKind
{
    /// <summary>No claim this tier can adjudicate.</summary>
    None = 0,

    /// <summary>
    /// "This interpolation does not interpolate" — the expression is asserted to reach
    /// the output as literal text.
    /// </summary>
    InterpolationIsInert = 1,

    /// <summary>
    /// "This raw string literal keeps the newline after its opening delimiter."
    /// </summary>
    RawStringRetainsOpeningNewline = 2
}

/// <summary>
/// Detects review comments that assert how a C# language construct behaves, in the
/// narrow set of cases a syntax tree can prove wrong.
/// </summary>
/// <remarks>
/// Distinct from <see cref="CompileClaimClassifier"/>, which catches "this does not
/// compile". These comments never say the code fails to compile — they say it compiles
/// and then behaves differently — so the parse-based refuter has nothing to contradict
/// and they reach the PR unchallenged. Two independent instances motivated this:
/// a claim that a raw string retains the newline after its opening delimiter (the
/// language strips it), and a claim that <c>{{expr}}</c> inside a <c>$$"""</c> string is
/// emitted as literal text (that is precisely the interpolation hole).
///
/// Deliberately narrow. Each kind here must be decidable from the tree alone; anything
/// needing type resolution or runtime behaviour belongs to a later tier.
/// </remarks>
public static class SemanticClaimClassifier
{
    public static SemanticClaimKind Classify(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return SemanticClaimKind.None;
        }

        var normalized = Normalize(body);

        if (InterpolationInertPhrases.Any(p => normalized.Contains(p, StringComparison.Ordinal)))
        {
            return SemanticClaimKind.InterpolationIsInert;
        }

        if (RawStringNewlinePhrases.Any(p => normalized.Contains(p, StringComparison.Ordinal)))
        {
            return SemanticClaimKind.RawStringRetainsOpeningNewline;
        }

        return SemanticClaimKind.None;
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append(' ');
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        sb.Append(' ');
        return " " + string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
    }

    // Space-padded, lower-cased, non-alphanumerics collapsed to single spaces.
    private static readonly string[] InterpolationInertPhrases =
    [
        " yields the literal text ",
        " produces the literal text ",
        " emitted as literal text ",
        " will be literal text ",
        " does not interpolate ",
        " will not interpolate ",
        " is not interpolated ",
        " are not interpolated ",
        " never interpolated ",
        " not actually interpolated ",
    ];

    private static readonly string[] RawStringNewlinePhrases =
    [
        " keeps the newline after ",
        " retains the newline after ",
        " includes the newline after ",
        " starts with a newline ",
        " begins with a newline ",
        " leading newline is preserved ",
        " retains the leading newline ",
        " includes the leading newline ",
    ];
}
