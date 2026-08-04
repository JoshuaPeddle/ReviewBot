using FluentAssertions;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;

namespace ReviewBot.Core.Tests.Llm;

public class EnsembleMergerTests
{
    private static ReviewResult Sample(params InlineComment[] comments) =>
        new("summary", comments);

    private static InlineComment Comment(
        string path,
        int line,
        Severity severity = Severity.Warning,
        Confidence confidence = Confidence.High,
        string body = "finding") =>
        new(path, line, "RIGHT", body, severity, confidence);

    [Fact]
    public void FindingReportedByEnoughSamplesSurvives()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10))
            ],
            minAgreement: 2);

        merged.Comments.Should().ContainSingle().Which.Line.Should().Be(10);
    }

    [Fact]
    public void FindingBelowThresholdIsDropped()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(),
                Sample()
            ],
            minAgreement: 2);

        merged.Comments.Should().BeEmpty();
    }

    [Fact]
    public void ThresholdOfOneKeepsEveryDistinctFinding()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("b.cs", 20)),
                Sample(Comment("c.cs", 30))
            ],
            minAgreement: 1);

        merged.Comments.Should().HaveCount(3);
    }

    [Fact]
    public void CommentsWithinLineWindowCountAsTheSameFinding()
    {
        // The same defect rarely lands on the same line twice across samples; exact-line
        // matching would score agreement as disagreement.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 12))
            ],
            minAgreement: 2,
            lineWindow: 3);

        merged.Comments.Should().ContainSingle();
    }

    [Fact]
    public void CommentsBeyondLineWindowStaySeparateFindings()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 40))
            ],
            minAgreement: 1,
            lineWindow: 3);

        merged.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void RepeatedCommentsFromOneSampleDoNotReachThresholdAlone()
    {
        // Support is counted per sample. Three comments on one line from a single sample is one
        // opinion, not three votes — otherwise a verbose sample manufactures its own consensus.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10), Comment("a.cs", 11), Comment("a.cs", 12)),
                Sample()
            ],
            minAgreement: 2);

        merged.Comments.Should().BeEmpty();
    }

    [Fact]
    public void DifferentPathsAtTheSameLineAreDifferentFindings()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("b.cs", 10))
            ],
            minAgreement: 1);

        merged.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void DifferentSidesAtTheSameLineAreDifferentFindings()
    {
        var left = new InlineComment("a.cs", 10, "LEFT", "finding", Severity.Warning);
        var right = new InlineComment("a.cs", 10, "RIGHT", "finding", Severity.Warning);

        var merged = EnsembleMerger.Merge([Sample(left), Sample(right)], minAgreement: 1);

        merged.Comments.Should().HaveCount(2);
    }

    [Fact]
    public void RepresentativeKeepsTheMostSevereMemberOfTheCluster()
    {
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10, Severity.Info, body: "nit")),
                Sample(Comment("a.cs", 10, Severity.Error, body: "real bug"))
            ],
            minAgreement: 2);

        var comment = merged.Comments.Should().ContainSingle().Subject;
        comment.Severity.Should().Be(Severity.Error);
        comment.Body.Should().Be("real bug");
    }

    [Fact]
    public void ThresholdAboveSampleCountIsClampedRatherThanDroppingEverything()
    {
        // A misconfigured threshold should not be indistinguishable from "the model found
        // nothing", which is how an unclamped comparison would present.
        var merged = EnsembleMerger.Merge(
            [
                Sample(Comment("a.cs", 10)),
                Sample(Comment("a.cs", 10))
            ],
            minAgreement: 99);

        merged.Comments.Should().ContainSingle();
    }

    [Fact]
    public void EmptySampleSetProducesEmptyResult()
    {
        var merged = EnsembleMerger.Merge([], minAgreement: 1);

        merged.Comments.Should().BeEmpty();
        merged.Summary.Should().BeEmpty();
    }

    [Fact]
    public void TokenUsageIsSummedAcrossSamples()
    {
        var first = new ReviewResult("s", []) { TokenUsage = new LlmTokenUsage(100, 50) };
        var second = new ReviewResult("s", []) { TokenUsage = new LlmTokenUsage(200, 70) };

        var merged = EnsembleMerger.Merge([first, second], minAgreement: 1);

        merged.TokenUsage!.PromptTokens.Should().Be(300);
        merged.TokenUsage.CompletionTokens.Should().Be(120);
    }
}
