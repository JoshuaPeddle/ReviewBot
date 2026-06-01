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

    [Fact]
    public void MergeSumsTokenUsageAcrossChunks()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("", []) { TokenUsage = new LlmTokenUsage(100, 50, 10) },
            new ReviewResult("", []) { TokenUsage = new LlmTokenUsage(200, 80, 20) },
            new ReviewResult("", []) { TokenUsage = new LlmTokenUsage(300, 60, 0) }
        ]);

        merged.TokenUsage.Should().BeEquivalentTo(new LlmTokenUsage(600, 190, 30));
    }

    [Fact]
    public void MergeHandlesNullTokenUsageInSubset()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("", []) { TokenUsage = new LlmTokenUsage(100, 50) },
            new ReviewResult("", []) { TokenUsage = null },
            new ReviewResult("", []) { TokenUsage = new LlmTokenUsage(200, 80) }
        ]);

        merged.TokenUsage.Should().BeEquivalentTo(new LlmTokenUsage(300, 130, 0));
    }

    [Fact]
    public void MergeReturnsNullTokenUsageWhenAllChunksHaveNone()
    {
        var merged = ReviewResultMerger.Merge(
        [
            new ReviewResult("", []) { TokenUsage = null },
            new ReviewResult("", []) { TokenUsage = null }
        ]);

        merged.TokenUsage.Should().BeNull();
    }

    [Fact]
    public void LlmTokenUsageAddAccumulatesCorrectly()
    {
        var a = new LlmTokenUsage(100, 50, 10);
        var b = new LlmTokenUsage(200, 80, 20);

        var sum = a.Add(b);

        sum.Should().BeEquivalentTo(new LlmTokenUsage(300, 130, 30));
    }

    [Fact]
    public void LlmTokenUsageAddReturnsBaseWhenOtherIsNull()
    {
        var usage = new LlmTokenUsage(100, 50, 10);

        var result = usage.Add(null);

        result.Should().BeSameAs(usage);
    }

    private static InlineComment Comment(
        string path,
        int line,
        string body,
        Severity severity,
        Confidence confidence = Confidence.High) =>
        new(path, line, "RIGHT", body, severity, confidence);
}
