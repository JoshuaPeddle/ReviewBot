using FluentAssertions;
using ReviewBot.Core.Domain;

namespace ReviewBot.Core.Tests.Domain;

public class DomainModelTests
{
    [Fact]
    public void DefaultReviewConfigUsesExpectedValues()
    {
        var config = ReviewConfig.Default;

        config.Model.Provider.Should().Be("openai");
        config.Model.Name.Should().BeEmpty();
        config.Review.InlineComments.Should().BeTrue();
        config.Review.Summary.Should().BeTrue();
        config.Review.MaxFiles.Should().Be(50);
        config.Review.MaxPatchLines.Should().Be(1500);
    }

    [Fact]
    public void InlineCommentUsesRecordEquality()
    {
        var first = new InlineComment(
            Path: "src/ReviewBot.Core/Domain/ReviewResult.cs",
            Line: 17,
            Side: "RIGHT",
            Body: "Prefer the existing domain type here.",
            Severity: Severity.Warning);

        var second = new InlineComment(
            Path: "src/ReviewBot.Core/Domain/ReviewResult.cs",
            Line: 17,
            Side: "RIGHT",
            Body: "Prefer the existing domain type here.",
            Severity: Severity.Warning);

        first.Should().Be(second);
    }

}
