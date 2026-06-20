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
        config.Model.Name.Should().Be("qwen3.6-27b");
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

    [Fact]
    public void FileChangeStatusValuesAreStable()
    {
        var values = new Dictionary<FileChangeStatus, int>
        {
            [FileChangeStatus.Added] = 0,
            [FileChangeStatus.Modified] = 1,
            [FileChangeStatus.Removed] = 2,
            [FileChangeStatus.Renamed] = 3,
            [FileChangeStatus.Copied] = 4
        };

        values.Should().OnlyContain(pair => (int)pair.Key == pair.Value);
    }
}
