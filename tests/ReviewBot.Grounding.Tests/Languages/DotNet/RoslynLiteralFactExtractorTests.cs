using FluentAssertions;
using ReviewBot.Grounding.Languages.DotNet;

namespace ReviewBot.Grounding.Tests.Languages.DotNet;

public sealed class RoslynLiteralFactExtractorTests
{
    /// <summary>
    /// Fixture 027's source verbatim. The model repeatedly claims this literal carries a
    /// leading or trailing newline; the language strips both, and stating the value leaves
    /// the claim no room to form.
    /// </summary>
    private const string SseBuilderSource = """"""
        using System.Text.Json;

        namespace Demo;

        public static class SseBuilder
        {
            public static string Chunk(string content)
            {
                var delta = $$"""
                    {"content":{{JsonSerializer.Serialize(content)}},"done":false}
                    """;

                return $"data: {delta}\n\n";
            }
        }
        """""";

    [Fact]
    public void ExtractStatesTheExactValueOfAnInterpolatedMultiLineRawString()
    {
        var facts = RoslynLiteralFactExtractor.Extract("src/Demo/SseBuilder.cs", SseBuilderSource);

        var fact = facts.Should().ContainSingle().Subject;
        fact.Path.Should().Be("src/Demo/SseBuilder.cs");
        // Parts are named separately: an earlier version spliced the hole into the value
        // as {expr}, and a model read that as literal output — the "interpolation is inert"
        // hallucination, caused by the fact text itself.
        fact.Fact.Should().Contain("""the literal text `"{"content":"`""");
        fact.Fact.Should().Contain("the evaluated value of the expression `JsonSerializer.Serialize(content)`");
        fact.Fact.Should().Contain("""the literal text `","done":false}"`""");
        fact.Fact.Should().Contain("ARE evaluated and their values inserted");
        fact.Fact.Should().Contain("does not start with a newline");
        fact.Fact.Should().Contain("does not end with one");
    }

    [Fact]
    public void ExtractStatesTheValueOfAPlainMultiLineRawString()
    {
        const string source = """"
            class C
            {
                const string S = """
                    hello
                    world
                    """;
            }
            """";

        var fact = RoslynLiteralFactExtractor.Extract("src/C.cs", source).Should().ContainSingle().Subject;

        // Indentation stripped, no leading or trailing newline — the value is "hello\nworld".
        fact.Fact.Should().Contain(@"hello\nworld");
        fact.Fact.Should().Contain("does not start with a newline");
    }

    /// <summary>
    /// Single-line raw strings have no stripping rules, so there is nothing to explain and
    /// nothing worth spending prompt on.
    /// </summary>
    [Fact]
    public void ExtractIgnoresSingleLineRawAndOrdinaryStrings()
    {
        const string source = """"
            class C
            {
                const string A = """single line""";
                const string B = "ordinary";
                const string D = @"verbatim";
            }
            """";

        RoslynLiteralFactExtractor.Extract("src/C.cs", source).Should().BeEmpty();
    }

    [Fact]
    public void ExtractLimitsFactsToTheRequestedLines()
    {
        var all = RoslynLiteralFactExtractor.Extract("src/Demo/SseBuilder.cs", SseBuilderSource);
        var line = all.Should().ContainSingle().Subject.Line;

        RoslynLiteralFactExtractor
            .Extract("src/Demo/SseBuilder.cs", SseBuilderSource, new HashSet<int> { line })
            .Should().ContainSingle();
        RoslynLiteralFactExtractor
            .Extract("src/Demo/SseBuilder.cs", SseBuilderSource, new HashSet<int> { line + 500 })
            .Should().BeEmpty();
    }

    /// <summary>
    /// A file that does not parse cannot settle anything about its own literals, and a
    /// wrong "fact" is worse than none.
    /// </summary>
    [Fact]
    public void ExtractReturnsNothingForSourceThatDoesNotParse()
    {
        RoslynLiteralFactExtractor.Extract("src/Broken.cs", "class C { void M( { } ")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractReportsALeadingNewlineWhenTheLiteralGenuinelyHasOne()
    {
        // A blank line after the opening delimiter survives: only the *first* newline goes.
        const string source = "class C { const string S = \"\"\"\n\n    body\n    \"\"\"; }";

        var fact = RoslynLiteralFactExtractor.Extract("src/C.cs", source).Should().ContainSingle().Subject;

        fact.Fact.Should().Contain("does start with a newline");
    }

    [Fact]
    public void ExtractTruncatesAnOversizedValueRatherThanFloodingThePrompt()
    {
        var body = string.Join('\n', Enumerable.Repeat("    aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 40));
        var source = $"class C {{ const string S = \"\"\"\n{body}\n    \"\"\"; }}";

        var fact = RoslynLiteralFactExtractor.Extract("src/C.cs", source).Should().ContainSingle().Subject;

        fact.Fact.Should().Contain("(truncated)");
        fact.Fact.Length.Should().BeLessThan(700);
    }
}
