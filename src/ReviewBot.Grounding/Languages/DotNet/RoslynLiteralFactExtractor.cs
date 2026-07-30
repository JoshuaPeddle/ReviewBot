using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewBot.Core.Domain;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace ReviewBot.Grounding.Languages.DotNet;

/// <summary>
/// States the value of multi-line raw string literals in changed C# code, so the model is
/// not left to work out raw-string semantics for itself.
/// </summary>
/// <remarks>
/// Raw strings are the corpus's most durable hallucination. The language strips the
/// newline after the opening delimiter, the newline before the closing one, and the
/// closing delimiter's indentation from every line — and models repeatedly claim the
/// opposite, in wording that shifts every run: "preserve the newline immediately after",
/// "includes a trailing newline", "has a leading-whitespace bug". Fixture 027 passed 1 run
/// in 5 while a phrase-matching refuter tried to catch each variant after the fact.
///
/// Roslyn already computes the answer: <c>ValueText</c> on the literal token (or on each
/// text token of an interpolated string) is the post-processing value. Handing that to the
/// model removes the guess instead of policing it.
///
/// Deliberately narrow. Only multi-line raw strings are described, because that is where
/// the stripping rules are non-obvious and where the observed failures are; a single-line
/// raw string has no such rules and needs no explaining.
/// </remarks>
public static class RoslynLiteralFactExtractor
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>Longest literal value rendered into a fact before it is elided.</summary>
    private const int MaxRenderedValueLength = 240;

    /// <summary>
    /// Facts for the multi-line raw strings in <paramref name="sourceText"/>.
    /// </summary>
    /// <param name="lines">
    /// When given, only literals starting on one of these lines are described — the
    /// changed lines, so an unrelated literal elsewhere in a large file costs nothing.
    /// </param>
    public static IReadOnlyList<LanguageFact> Extract(
        string path,
        string sourceText,
        IReadOnlySet<int>? lines = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sourceText);

        var tree = CSharpSyntaxTree.ParseText(sourceText, ParseOptions);

        // A file that does not parse cannot settle anything about its own literals.
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == RoslynSeverity.Error))
        {
            return [];
        }

        var facts = new List<LanguageFact>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var described = node switch
            {
                LiteralExpressionSyntax literal when IsMultiLineRawLiteral(literal) =>
                    BuildPlainFact(literal.Token.ValueText),
                InterpolatedStringExpressionSyntax interpolated when IsMultiLineRawInterpolation(interpolated) =>
                    BuildInterpolatedFact(interpolated),
                _ => null
            };
            if (described is null)
            {
                continue;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (lines is not null && !lines.Contains(line))
            {
                continue;
            }

            facts.Add(new LanguageFact(path, line, described));
        }

        return facts;
    }

    private static bool IsMultiLineRawLiteral(LiteralExpressionSyntax literal) =>
        literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    private static bool IsMultiLineRawInterpolation(InterpolatedStringExpressionSyntax interpolated) =>
        interpolated.StringStartToken.IsKind(SyntaxKind.InterpolatedMultiLineRawStringStartToken);

    private static string BuildPlainFact(string value) =>
        $"this multi-line raw string's value is exactly {Render(value)}. {StrippingRule(value, value)} "
        + "Any claim about its leading or trailing whitespace must match this value.";

    /// <summary>
    /// Describes an interpolated raw string as an ordered list of literal runs and
    /// evaluated expressions.
    /// </summary>
    /// <remarks>
    /// An earlier version spliced each hole into the value as <c>{expression}</c> and
    /// stated the result as "the value is exactly …". A model read that rendering as the
    /// literal output and concluded the expression was never evaluated — the very
    /// "interpolation is inert" hallucination this is meant to prevent, caused by the fact
    /// text itself. Naming the parts separately, and saying outright that the holes are
    /// evaluated, removes the ambiguity: there is no rendering left to misread as text.
    /// </remarks>
    private static string BuildInterpolatedFact(InterpolatedStringExpressionSyntax interpolated)
    {
        var parts = new List<string>();
        var literalOnly = new StringBuilder();
        string? firstText = null;
        string? lastText = null;

        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    var value = text.TextToken.ValueText;
                    parts.Add($"the literal text {Render(value)}");
                    literalOnly.Append(value);
                    firstText ??= value;
                    lastText = value;
                    break;
                case InterpolationSyntax hole:
                    parts.Add($"the evaluated value of the expression `{hole.Expression}`");
                    lastText = null;
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return "this multi-line raw string is empty.";
        }

        return $"this multi-line raw string produces, in order: {string.Join(", then ", parts)}. "
            + "In a `$$\"\"\"` raw string the interpolation delimiter is `{{ }}` and a single brace is "
            + "literal, so those expressions ARE evaluated and their values inserted — they do not "
            + $"reach the output as text. {StrippingRule(firstText, lastText)} "
            + "Any claim about its leading or trailing whitespace must match this.";
    }

    /// <summary>
    /// States whether the produced string begins or ends with a newline, and why.
    /// </summary>
    /// <param name="leading">
    /// The first literal run, or null when an interpolation comes first (in which case
    /// nothing can be said about a leading newline).
    /// </param>
    private static string StrippingRule(string? leading, string? trailing)
    {
        var starts = leading is null
            ? "It begins with an interpolated value"
            : $"It {(leading.StartsWith('\n') ? "does" : "does not")} start with a newline";
        var ends = trailing is null
            ? "and ends with an interpolated value"
            : $"and {(trailing.EndsWith('\n') ? "does" : "does not")} end with one";

        return $"{starts} {ends}: the newline after the opening delimiter, the newline before the "
            + "closing delimiter, and the closing delimiter's indentation are all removed by the language.";
    }

    /// <summary>Renders the value on one line, with escapes, so it survives the prompt.</summary>
    private static string Render(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal);

        if (escaped.Length > MaxRenderedValueLength)
        {
            escaped = escaped[..MaxRenderedValueLength] + "… (truncated)";
        }

        return $"`\"{escaped}\"`";
    }
}
