using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;

namespace ReviewBot.Core.Tests.Llm;

public class ReviewResultMergerTests
{
    [Fact]
    public void MergeReturnsAllUniqueComments()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("Chunk one.", [Comment("src/A.cs", 1, "A issue.", Severity.Warning)]),
            new ReviewResult("Chunk two.", [Comment("src/B.cs", 2, "B issue.", Severity.Error)])
        ]);

        merged.Summary.Should().BeEmpty();
        merged.Comments.Select(comment => comment.Body).Should().Equal("A issue.", "B issue.");
    }

    [Fact]
    public void MergeDeduplicatesCommentsOnSamePathLineAndSide()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("First.", [Comment("src/A.cs", 10, "Lower severity.", Severity.Warning)]),
            new ReviewResult("Second.", [Comment("src/A.cs", 10, "Higher severity.", Severity.Error)])
        ]);

        merged.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("Higher severity.");
    }

    [Fact]
    public void MergeUsesHigherConfidenceWhenSeverityMatches()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("First.", [Comment("src/A.cs", 10, "Medium confidence.", Severity.Warning, Confidence.Medium)]),
            new ReviewResult("Second.", [Comment("src/A.cs", 10, "High confidence.", Severity.Warning, Confidence.High)])
        ]);

        merged.Comments.Should().ContainSingle()
            .Which.Body.Should().Be("High confidence.");
    }

    private static InlineComment Comment(
        string path,
        int line,
        string body,
        Severity severity,
        Confidence confidence = Confidence.High) =>
        new(path, line, "RIGHT", body, severity, confidence);
}
