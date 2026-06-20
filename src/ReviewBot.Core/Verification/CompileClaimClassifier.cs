using System.Text;

namespace ReviewBot.Core.Verification;

/// <summary>
/// Detects review comments that assert the code does not compile / does not parse
/// (a syntax or build failure). This is the one finding class that ground truth can
/// <em>totally</em> refute: a successful build or a clean parse proves there is no
/// such error. Deliberately narrow — it must not match logic, type, or
/// "might not compile under condition X" claims, which ground truth cannot disprove.
/// </summary>
public static class CompileClaimClassifier
{
    public static bool IsCompileFailureClaim(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        // Collapse runs of spaces so phrases match cleanly (Normalize turns "C#" into
        // "c " plus the following space, i.e. a double space).
        var normalized = " " + string.Join(
            ' ',
            Normalize(body).Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
        return CompileFailureClaimPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append(' ');
        foreach (var character in value)
        {
            sb.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        sb.Append(' ');
        return sb.ToString();
    }

    // Phrases (normalized: lower-cased, non-alphanumerics -> spaces, space-padded,
    // whitespace-collapsed) that assert the code does not compile or parse. " invalid c
    // syntax " (rather than a bare " invalid c ") avoids firing on "invalid cache/class".
    private static readonly string[] CompileFailureClaimPhrases =
    [
        " syntax error ",
        " invalid syntax ",
        " invalid c syntax ",
        " does not compile ",
        " will not compile ",
        " won t compile ",
        " doesn t compile ",
        " cannot compile ",
        " can t compile ",
        " fails to compile ",
        " compilation error ",
        " compile error ",
        " compiler error ",
        " will not build ",
        " won t build ",
        " fails to build ",
        " build error ",
        " will not parse ",
        " fails to parse ",
        " does not parse ",
    ];
}
