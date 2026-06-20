using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Verification;

namespace ReviewBot.Core.Tests.Verification;

public class FindingRefuterTests
{
    private static InlineComment Comment(string path, string body) =>
        new(path, 5, "RIGHT", body, Severity.Error);

    private static IReadOnlySet<string> Clean(params string[] paths) =>
        new HashSet<string>(paths, StringComparer.Ordinal);

    [Fact]
    public void RefutesCompileClaimOnACleanlyParsedFile()
    {
        var comments = new[] { Comment("app/main.py", "This is invalid syntax and will not compile.") };

        var result = FindingRefuter.Refute(comments, Clean("app/main.py"));

        result.Kept.Should().BeEmpty();
        result.Refuted.Should().ContainSingle().Which.Should().BeSameAs(comments[0]);
    }

    [Fact]
    public void KeepsCompileClaimWhenItsFileWasNotProvenToParse()
    {
        var comments = new[] { Comment("app/main.py", "This is invalid syntax and will not compile.") };

        var result = FindingRefuter.Refute(comments, Clean("app/other.py"));

        result.Kept.Should().ContainSingle();
        result.Refuted.Should().BeEmpty();
    }

    [Fact]
    public void KeepsNonCompileClaimsEvenOnACleanlyParsedFile()
    {
        var comments = new[] { Comment("app/main.py", "This could dereference null at runtime.") };

        var result = FindingRefuter.Refute(comments, Clean("app/main.py"));

        result.Kept.Should().ContainSingle();
        result.Refuted.Should().BeEmpty();
    }

    [Fact]
    public void NoCleanlyParsedPathsIsANoOp()
    {
        var comments = new[] { Comment("app/main.py", "syntax error here") };

        var result = FindingRefuter.Refute(comments, Clean());

        result.Kept.Should().BeSameAs(comments);
        result.Refuted.Should().BeEmpty();
    }
}
