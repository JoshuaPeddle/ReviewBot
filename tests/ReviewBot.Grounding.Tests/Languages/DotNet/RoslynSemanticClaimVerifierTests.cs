using FluentAssertions;
using ReviewBot.Core.Verification;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public sealed class RoslynSemanticClaimVerifierTests
{
    // These fixtures are themselves full of raw-string delimiters, so they are assembled
    // from lines rather than nested inside another raw string.
    private const string Q3 = "\"\"\"";
    private const string Q4 = "\"\"\"\"";

    // The construct from ReviewBot's own PR #50, where the bot claimed the interpolated
    // expression reaches the output as literal text. Line 8 holds the interpolation.
    private static readonly string DoubleDollarRawString = string.Join('\n',
        "using System.Text.Json;",
        "",
        "public static class SseBuilder",
        "{",
        "    public static string Chunk(string content)",
        "    {",
        "        var delta = $$" + Q3,
        "            {\"content\":{{JsonSerializer.Serialize(content)}},\"done\":false}",
        "            " + Q3 + ";",
        "",
        "        return delta;",
        "    }",
        "}");

    // A multi-line raw string with no interpolation at all. Line 4 holds the body.
    private static readonly string PlainRawString = string.Join('\n',
        "public static class Json",
        "{",
        "    public const string Empty = " + Q4,
        "        {\"items\": []}",
        "        " + Q4 + ";",
        "}");

    [Fact]
    public void RefutesInertInterpolationClaimWhenAHoleExists()
    {
        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.InterpolationIsInert, DoubleDollarRawString, line: 8)
            .Should().Be(SemanticVerdict.Refuted);
    }

    [Fact]
    public void DoesNotRefuteWhenTheStringGenuinelyHasNoHole()
    {
        // A comment saying these braces are literal is correct, so the tier must stay
        // out of the way. Refuting here would delete a true finding.
        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.InterpolationIsInert, PlainRawString, line: 4)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Fact]
    public void RefutesRawStringNewlineClaimBecauseTheLanguageStripsIt()
    {
        // The 019 hallucination. Roslyn's ValueText applies the language's own rule, so
        // this asks the compiler rather than reimplementing the stripping behaviour.
        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.RawStringRetainsOpeningNewline, PlainRawString, line: 4)
            .Should().Be(SemanticVerdict.Refuted);
    }

    [Fact]
    public void DoesNotRefuteTheNewlineClaimFromAnInterpolatedStartToken()
    {
        // An interpolated raw string's start token carries only the opening delimiter, so
        // its ValueText never begins with a newline. Testing it would refute on a
        // meaningless basis; the content lives in the interpolated expression's parts.
        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.RawStringRetainsOpeningNewline, DoubleDollarRawString, line: 8)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Fact]
    public void DoesNotRefuteTheNewlineClaimForASingleLineRawString()
    {
        // The stripping rule the claim describes applies to multi-line raw strings only.
        var source = string.Join('\n',
            "public static class Single",
            "{",
            "    public const string Value = " + Q3 + "hello" + Q3 + ";",
            "}");

        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.RawStringRetainsOpeningNewline, source, line: 3)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Fact]
    public void ReturnsUnknownWhenNoSuchConstructIsNearTheLine()
    {
        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.InterpolationIsInert, DoubleDollarRawString, line: 1)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Fact]
    public void ReturnsUnknownWhenTheFileDoesNotParse()
    {
        // A file with a real syntax error cannot settle claims about its own semantics.
        var broken = string.Join('\n',
            "public static class Broken",
            "{",
            "    public const string X = ;");

        RoslynSemanticClaimVerifier.Verify(
                SemanticClaimKind.RawStringRetainsOpeningNewline, broken, line: 3)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9999)]
    public void ReturnsUnknownForAnOutOfRangeLine(int line)
    {
        RoslynSemanticClaimVerifier.Verify(SemanticClaimKind.InterpolationIsInert, DoubleDollarRawString, line)
            .Should().Be(SemanticVerdict.Unknown);
    }

    [Fact]
    public void ReturnsUnknownForAnUnclassifiedClaim()
    {
        RoslynSemanticClaimVerifier.Verify(SemanticClaimKind.None, DoubleDollarRawString, line: 8)
            .Should().Be(SemanticVerdict.Unknown);
    }
}
