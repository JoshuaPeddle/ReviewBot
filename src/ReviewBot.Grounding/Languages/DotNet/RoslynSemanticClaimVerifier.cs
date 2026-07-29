using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ReviewBot.Core.Verification;

namespace ReviewBot.Grounding.Languages.DotNet;

/// <summary>
/// Settles a <see cref="SemanticClaimKind"/> against the C# syntax tree.
/// </summary>
/// <remarks>
/// The syntax tier proves a file <em>parses</em>, which refutes "this does not compile"
/// but says nothing about a claim that the code compiles and then behaves differently.
/// These checks close that gap for the cases the tree decides outright.
///
/// Refutes only on certainty. If the construct the comment describes is not found at
/// that line, the answer is <see cref="SemanticVerdict.Unknown"/> and the comment
/// survives — the same discipline the parse-based refuter follows, because a wrong
/// refutation silently deletes a real finding.
/// </remarks>
public static class RoslynSemanticClaimVerifier
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    // A comment's line can sit a line or two off the construct it describes, so search a
    // small window rather than demanding an exact hit.
    private const int LineWindow = 2;

    public static SemanticVerdict Verify(SemanticClaimKind claim, string sourceText, int line)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        if (claim == SemanticClaimKind.None || line <= 0)
        {
            return SemanticVerdict.Unknown;
        }

        var tree = CSharpSyntaxTree.ParseText(sourceText, ParseOptions);

        // A file that does not parse cannot settle anything about its own semantics.
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return SemanticVerdict.Unknown;
        }

        var root = tree.GetRoot();
        var span = TryGetLineSpan(tree, line);
        if (span is null)
        {
            return SemanticVerdict.Unknown;
        }

        return claim switch
        {
            SemanticClaimKind.InterpolationIsInert => VerifyInterpolation(root, span.Value),
            SemanticClaimKind.RawStringRetainsOpeningNewline => VerifyRawStringNewline(root, span.Value),
            _ => SemanticVerdict.Unknown
        };
    }

    /// <summary>
    /// An interpolated string containing at least one interpolation hole disproves the
    /// claim that its expression reaches the output as literal text.
    /// </summary>
    /// <remarks>
    /// Covers the <c>$$"""</c> case that prompted this: there the interpolation delimiter
    /// is <c>{{ }}</c> and a single brace is literal, which reads as backwards to anyone
    /// expecting the single-<c>$</c> rule. Roslyn simply reports whether a hole exists.
    /// </remarks>
    private static SemanticVerdict VerifyInterpolation(SyntaxNode root, TextSpan span)
    {
        var interpolated = root.DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .Where(node => node.Span.IntersectsWith(span))
            .ToArray();

        if (interpolated.Length == 0)
        {
            return SemanticVerdict.Unknown;
        }

        return interpolated.Any(node => node.Contents.OfType<InterpolationSyntax>().Any())
            ? SemanticVerdict.Refuted
            : SemanticVerdict.Unknown;
    }

    /// <summary>
    /// A multi-line raw string literal whose value does not begin with a newline
    /// disproves the claim that it retains the newline after its opening delimiter.
    /// </summary>
    /// <remarks>
    /// Roslyn applies the language's own rule when it computes ValueText, so this asks
    /// the compiler rather than reimplementing the stripping behaviour.
    /// </remarks>
    private static SemanticVerdict VerifyRawStringNewline(SyntaxNode root, TextSpan span)
    {
        var tokens = root.DescendantTokens()
            .Where(token => token.Span.IntersectsWith(span))
            .Where(IsRawStringToken)
            .ToArray();

        if (tokens.Length == 0)
        {
            return SemanticVerdict.Unknown;
        }

        return tokens.Any(token => !token.ValueText.StartsWith('\n') && !token.ValueText.StartsWith('\r'))
            ? SemanticVerdict.Refuted
            : SemanticVerdict.Unknown;
    }

    /// <summary>
    /// Only a multi-line raw string literal token, whose ValueText is the string content.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes the interpolated start tokens and single-line raw strings.
    /// An interpolated start token carries only the opening delimiter, so its ValueText
    /// never begins with a newline and testing it would refute on a meaningless basis;
    /// and the newline-stripping rule the claim describes applies to multi-line raw
    /// strings only. Both cases now fall through to Unknown and the comment survives,
    /// which is the conservative direction. Caught by ReviewBot reviewing this change.
    /// </remarks>
    private static bool IsRawStringToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);

    private static TextSpan? TryGetLineSpan(SyntaxTree tree, int line)
    {
        var text = tree.GetText();
        var zeroBased = line - 1;
        if (zeroBased < 0 || zeroBased >= text.Lines.Count)
        {
            return null;
        }

        var start = text.Lines[Math.Max(0, zeroBased - LineWindow)].Start;
        var end = text.Lines[Math.Min(text.Lines.Count - 1, zeroBased + LineWindow)].End;
        return TextSpan.FromBounds(start, end);
    }
}
